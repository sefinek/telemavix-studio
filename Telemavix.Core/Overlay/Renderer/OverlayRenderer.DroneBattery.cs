using SkiaSharp;
using Telemavix.Core.Localization;
using Telemavix.Core.Telemetry;

namespace Telemavix.Core.Overlay;

/// <summary>
///     The battery widgets. The drone battery (DroneBatteryElement): the caption, a battery icon filled to the charge beside the percent, and
///     under the percent the extra lines the user asked for (voltage, temperature, current, cells). The icon and the percent take
///     a color that follows the charge: green when full, through yellow to LowColor (orange) at LowPercent and CriticalColor (red) at
///     CriticalPercent and below, blended in between. The voltage, temperature and cell lines stay the text color while they are fine and
///     take LowColor or CriticalColor when the reading is poor (DroneBatteryHealth). The device battery (DeviceBatteryElement) draws
///     the same caption, icon and percent when ShowIcon is on, and otherwise is a plain stat (DrawStat) that can still take the charge's color.
/// </summary>
public sealed partial class OverlayRenderer
{
	private static readonly SKColor BatteryLowDefault = SKColor.Parse("#FF9800");
	private static readonly SKColor BatteryCriticalDefault = SKColor.Parse("#F44336");
	private static readonly SKColor BatteryFullColor = SKColor.Parse("#4CAF50");
	private static readonly SKColor BatteryMidColor = SKColor.Parse("#FFC107");

	private const string CurrentSeparator = " · ";

	internal static readonly DroneBattery SampleDroneBattery = new(100, 99.9, 99.9, 99.9, [3.99, 3.99, 3.99]);

	private readonly SKPaint _batteryPaint = new() { IsAntialias = true };

	/// <summary>A lightning bolt in the middle of the icon's body, drawn while the phone is charging.</summary>
	private readonly SKPath _chargingBolt = BuildChargingBolt();

	private static SKPath BuildChargingBolt()
	{
		float cx = OverlayElementBounds.DroneBatteryIconWidth / 2f;
		float cy = (OverlayElementBounds.DroneBatteryIconTop + OverlayElementBounds.DroneBatteryCapHeight + OverlayElementBounds.DroneBatteryIconBottom) / 2f;
		float h = (OverlayElementBounds.DroneBatteryIconBottom - OverlayElementBounds.DroneBatteryIconTop - OverlayElementBounds.DroneBatteryCapHeight) * 0.55f;
		float w = OverlayElementBounds.DroneBatteryIconWidth * 0.5f;
		using var path = new SKPathBuilder();
		path.MoveTo(cx + w * 0.12f, cy - h / 2);
		path.LineTo(cx - w / 2, cy + h * 0.08f);
		path.LineTo(cx - w * 0.04f, cy + h * 0.08f);
		path.LineTo(cx - w * 0.12f, cy + h / 2);
		path.LineTo(cx + w / 2, cy - h * 0.08f);
		path.LineTo(cx + w * 0.04f, cy - h * 0.08f);
		path.Close();
		return path.Detach();
	}

	/// <summary>
	///     The charge's color: stops at 100 % (green), halfway between LowPercent and full (yellow), LowPercent and CriticalPercent, blended
	///     between them, the critical color below. Without a reading the widget's accent.
	/// </summary>
	internal static SKColor BatteryColor(BatteryElement element, double? percent)
	{
		if (percent is not { } charge || !element.ColorByCharge) return AccentColorOf(element);

		double critical = element.CriticalPercent, low = Math.Max(element.LowPercent, critical);
		ReadOnlySpan<(double At, SKColor Color)> stops =
		[
			(critical, ResolveColor(element.CriticalColor, BatteryCriticalDefault)),
			(low, ResolveColor(element.LowColor, BatteryLowDefault)),
			((low + 100) / 2, BatteryMidColor),
			(100, BatteryFullColor)
		];

		if (charge <= stops[0].At) return stops[0].Color;
		for (int i = 1; i < stops.Length; i++)
		{
			if (charge > stops[i].At) continue;

			double span = stops[i].At - stops[i - 1].At;
			return span <= 0 ? stops[i].Color : Blend(stops[i - 1].Color, stops[i].Color, (charge - stops[i - 1].At) / span);
		}

		return stops[^1].Color;
	}

	private static SKColor Blend(SKColor from, SKColor to, double share)
	{
		static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
		return new SKColor(Mix(from.Red, to.Red, share), Mix(from.Green, to.Green, share), Mix(from.Blue, to.Blue, share));
	}

	/// <summary>The lines under the percent for what the widget shows - "--" with its unit where the log has no reading.</summary>
	internal static string[] DroneBatteryLines(DroneBatteryElement element, DroneBattery? battery)
	{
		return [.. DroneBatteryRows(element, battery).Select(r => r.Text)];
	}

	/// <summary>Those lines with how their reading stands (DroneBatteryHealth): the voltage and the cells by the lowest cell, the temperature by its range, the current by none.</summary>
	internal static (string Text, BatteryCondition Condition)[] DroneBatteryRows(DroneBatteryElement element, DroneBattery? battery)
	{
		List<(string, BatteryCondition)> rows = [];
		BatteryCondition cells = DroneBatteryHealth.Cells(battery, element.LowCellVolts, element.CriticalCellVolts);
		string current = battery?.CurrentA is { } amps ? $"{F(amps, "0.0")} A" : "-- A";
		bool beside = element is { ShowVoltage: true, ShowCurrent: true, CurrentBesideVoltage: true };
		if (element.ShowVoltage)
		{
			string voltage = battery?.VoltageV is { } volts ? $"{F(volts, "0.0")} V" : "-- V";
			rows.Add((beside ? voltage + CurrentSeparator + current : voltage, cells));
		}

		if (element.ShowTemperature)
		{
			(string value, string unit) = FormatTemperature(battery?.TemperatureCelsius, element.Units);
			rows.Add(($"{value} {unit}", DroneBatteryHealth.Temperature(battery?.TemperatureCelsius, element.ColdCautionCelsius, element.ColdCriticalCelsius, element.HotCautionCelsius, element.HotCriticalCelsius)));
		}

		if (element.ShowCurrent && !beside) rows.Add((current, BatteryCondition.Normal));
		if (element.ShowCells) rows.Add((battery?.CellVoltages is { Count: > 0 } list ? string.Join(" / ", list.Select(c => F(c, "0.00"))) + " V" : "-- V", cells));
		return [.. rows];
	}

	/// <summary>A line's color: the plain text color while all is well, the widget's low or critical color when its reading isn't.</summary>
	private static SKColor DroneBatteryLineColor(DroneBatteryElement element, BatteryCondition condition, SKColor text)
	{
		return !element.ColorByCharge || condition == BatteryCondition.Normal ? text
			: condition == BatteryCondition.Critical ? ResolveColor(element.CriticalColor, BatteryCriticalDefault)
			: ResolveColor(element.LowColor, BatteryLowDefault);
	}

	private void DrawDroneBattery(SKCanvas canvas, DerivedFrame frame, DroneBatteryElement element)
	{
		DroneBattery? battery = frame.Raw.DroneBattery is { } reading && double.IsFinite(reading.Percent) ? reading : null;
		SKColor textColor = TextColorOf(element), outlineColor = OutlineColorOf(element);
		float left = DrawBatteryReading(canvas, element, element.Label ?? CoreStrings.Overlay_DroneBattery, battery?.Percent);

		SKFont lineFont = TextFont(element, OverlayElementBounds.SmallFontSize);
		(string Text, BatteryCondition Condition)[] rows = DroneBatteryRows(element, battery);
		for (int i = 0; i < rows.Length; i++)
		{
			float baseline = OverlayElementBounds.DroneBatteryLineBaseline + i * OverlayElementBounds.DroneBatteryLineStep;
			string text = rows[i].Text;
			SKColor color = DroneBatteryLineColor(element, rows[i].Condition, textColor);
			// The current on the voltage's line keeps the plain color: the voltage's condition says nothing about it.
			int split = i == 0 && element is { ShowVoltage: true, ShowCurrent: true, CurrentBesideVoltage: true } ? text.IndexOf(CurrentSeparator, StringComparison.Ordinal) : -1;
			if (split < 0)
			{
				DrawOutlined(canvas, text, left, baseline, lineFont, color, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
				continue;
			}

			string voltage = text[..split];
			DrawOutlined(canvas, voltage, left, baseline, lineFont, color, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
			DrawOutlined(canvas, text[split..], left + lineFont.MeasureText(voltage), baseline, lineFont, textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		}
	}

	/// <summary>
	///     The caption, the icon (when the widget shows it) and the percent in the charge's color: what the drone battery and the device battery share.
	///     The widgets that line their text up (OverlayElementBounds.SupportsTextAlign: the device battery) line the caption, the icon with the percent and the note up to the right or the
	///     center of the box as a block each; the drone battery stays on the left. Returns where the readout's text starts, for what a widget draws under it.
	/// </summary>
	private float DrawBatteryReading(SKCanvas canvas, BatteryElement element, string caption, double? percent, bool charging = false, string? note = null)
	{
		SKColor textColor = TextColorOf(element), outlineColor = OutlineColorOf(element), level = BatteryColor(element, percent);
		SKFont captionFont = TextFont(element, OverlayElementBounds.LabelFontSize), valueFont = TextFont(element, OverlayElementBounds.DroneBatteryValueFontSize);
		SKFont unitFont = TextFont(element, OverlayElementBounds.UnitFontSize), noteFont = TextFont(element, OverlayElementBounds.SmallFontSize);
		string title = caption.ToUpperInvariant(), value = percent is { } charge ? F(charge, "0") : "--";

		float iconPart = element.ShowIcon ? OverlayElementBounds.DroneBatteryIconWidth + OverlayElementBounds.DroneBatteryIconGap : 0;
		float valueWidth = valueFont.MeasureText(value);
		float readout = iconPart + valueWidth + OverlayElementBounds.StatUnitGapX + unitFont.MeasureText("%");
		float titleWidth = captionFont.MeasureText(title), noteWidth = note is null ? 0 : noteFont.MeasureText(note);
		TextAlign align = OverlayElementBounds.SupportsTextAlign(element) ? element.TextAlign : TextAlign.Left;
		float box = align == TextAlign.Left ? 0 : Math.Max(OverlayElementBounds.TextBoxRight(element), Math.Max(titleWidth, Math.Max(readout, noteWidth)));
		float readoutX = OverlayElementBounds.AlignedX(align, box, readout);

		DrawOutlined(canvas, title, OverlayElementBounds.AlignedX(align, box, titleWidth), 0, captionFont, textColor, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);

		if (element.ShowIcon)
		{
			canvas.Save();
			canvas.Translate(readoutX, 0);
			DrawBatteryIcon(canvas, percent, level, textColor, charging);
			canvas.Restore();
		}

		float left = readoutX + iconPart;
		DrawOutlined(canvas, value, left, OverlayElementBounds.DroneBatteryValueBaseline, valueFont, level, outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		DrawOutlined(canvas, "%", left + valueWidth + OverlayElementBounds.StatUnitGapX, OverlayElementBounds.DroneBatteryValueBaseline, unitFont, textColor,
			outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		if (note is not null)
		{
			DrawOutlined(canvas, note, align == TextAlign.Left ? left : OverlayElementBounds.AlignedX(align, box, noteWidth), OverlayElementBounds.DroneBatteryLineBaseline, noteFont, textColor,
				outlineColor: outlineColor, outlineWidthScale: element.OutlineWidth);
		}

		return left;
	}

	/// <summary>An upright battery - a body with a cap on top - outlined in the text color and filled from the bottom to `percent`.</summary>
	private void DrawBatteryIcon(SKCanvas canvas, double? percent, SKColor fill, SKColor outline, bool charging)
	{
		const float stroke = 5, radius = 9;
		float width = OverlayElementBounds.DroneBatteryIconWidth;
		float capHeight = OverlayElementBounds.DroneBatteryCapHeight, capWidth = OverlayElementBounds.DroneBatteryCapWidth;
		float top = OverlayElementBounds.DroneBatteryIconTop + capHeight, bottom = OverlayElementBounds.DroneBatteryIconBottom;
		var body = new SKRect(0, top, width, bottom);

		_batteryPaint.Style = SKPaintStyle.Fill;
		_batteryPaint.Color = outline;
		canvas.DrawRoundRect(new SKRect((width - capWidth) / 2, OverlayElementBounds.DroneBatteryIconTop, (width + capWidth) / 2, top + 2), 3, 3, _batteryPaint);

		if (percent is { } charge)
		{
			var inner = SKRect.Inflate(body, -(stroke + 3), -(stroke + 3));
			float height = inner.Height * (float)Math.Clamp(charge / 100.0, 0, 1);
			if (height > 0)
			{
				canvas.Save();
				using var clip = new SKRoundRect(inner, radius - stroke / 2);
				canvas.ClipRoundRect(clip);
				_batteryPaint.Color = fill;
				canvas.DrawRect(new SKRect(inner.Left, inner.Bottom - height, inner.Right, inner.Bottom), _batteryPaint);
				canvas.Restore();
			}
		}

		_batteryPaint.Style = SKPaintStyle.Stroke;
		_batteryPaint.StrokeWidth = stroke;
		_batteryPaint.Color = outline;
		canvas.DrawRoundRect(SKRect.Inflate(body, -stroke / 2, -stroke / 2), radius, radius, _batteryPaint);

		if (!charging) return;

		_batteryPaint.Style = SKPaintStyle.Fill;
		_batteryPaint.Color = outline;
		canvas.DrawPath(_chargingBolt, _batteryPaint);
	}
}
