using SkiaSharp;

namespace Telemavix.Core.Overlay;

/// <summary>
///     The position marker of the Compass and Map widgets - a heading arrow, or a dot - and of the map behind a track viewed with no
///     recording (TrackBackdrop), so all of them draw it the same. Not thread-safe: one per renderer.
/// </summary>
internal sealed class TrailMarker : IDisposable
{
	private const double ArrowLength = 42, WingAngle = 2.5, WingLength = ArrowLength * 0.55;

	private readonly SKPath _arrow = CreateArrow();
	private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
	private readonly SKPaint _outline = new() { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
	private readonly SKPaint _dotOutline = new() { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };

	/// <summary>
	///     At (cx, cy): the arrow turned `headingDegrees` clockwise from north, or a dot. `scale` is relative to the built-in size
	///     (an arrow 42 units long), already clamped by the caller.
	/// </summary>
	public void Draw(SKCanvas canvas, float cx, float cy, double headingDegrees, float scale, SKColor fill, bool arrow)
	{
		_fill.Color = fill;
		canvas.Save();
		canvas.Translate(cx, cy);
		canvas.Scale(scale);
		if (arrow)
		{
			canvas.RotateDegrees((float)headingDegrees);
			canvas.DrawPath(_arrow, _fill);
			canvas.DrawPath(_arrow, _outline);
		}
		else
		{
			canvas.DrawCircle(0, 0, 9, _dotOutline);
			canvas.DrawCircle(0, 0, 6, _fill);
		}

		canvas.Restore();
	}

	/// <summary>The arrow pointing north around (0, 0).</summary>
	private static SKPath CreateArrow()
	{
		using var builder = new SKPathBuilder();
		builder.MoveTo(0, (float)-ArrowLength);
		builder.LineTo((float)(Math.Sin(WingAngle) * WingLength), (float)(-Math.Cos(WingAngle) * WingLength));
		builder.LineTo(0, 0);
		builder.LineTo((float)(Math.Sin(-WingAngle) * WingLength), (float)(-Math.Cos(-WingAngle) * WingLength));
		builder.Close();
		return builder.Detach();
	}

	public void Dispose()
	{
		_arrow.Dispose();
		_fill.Dispose();
		_outline.Dispose();
		_dotOutline.Dispose();
	}
}
