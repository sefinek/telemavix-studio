using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using Telemavix.Cameras.Dji;
using Telemavix.Cameras.Insta360;
using Telemavix.Core.Cameras;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Tests.Overlay;

[TestClass]
public sealed class SensorWidgetTests
{
	private static readonly OverlayElementType[] Types =
	[
		OverlayElementType.AmbientTemperature, OverlayElementType.ImuTemperature,
		OverlayElementType.DeviceDistance, OverlayElementType.DeviceBattery,
		OverlayElementType.AudioLevel, OverlayElementType.WindProcessing, OverlayElementType.ImageLuma
	];

	[TestMethod]
	public void DjiAndInstaWithoutSensorReadingsDisableTheNewWidgets()
	{
		List<TelemetryFrame> frames = [new(0, 0, 50, 20, 100, DateTime.UtcNow, 0, 0, 1)];
		foreach (ICameraFormat camera in new ICameraFormat[] { new DjiOsmoFormat(), new Insta360Format() })
		{
			var availability = OverlayAvailability.Of(frames, true, camera);
			foreach (OverlayElementType type in Types) Assert.IsFalse(OverlayDataRequirements.IsSupported(type, availability), $"{camera.Id}: {type}");
			OverlayElement[] layout = [.. OverlayPreset.CreateDefault("p", "test").Elements.Where(e => Types.Contains(e.Type)).Select(e => e with { Visible = true })];
			Assert.IsTrue(availability.Apply(layout).All(e => !e.Visible));
			Assert.IsTrue(layout.All(e => e.Visible), "the preset is unchanged");
		}
	}

	[TestMethod]
	public void TheDeviceBatteryIsPlainByDefaultAndTheDroneBatteryHasItsIcon()
	{
		var device = new DeviceBatteryElement { X = 0, Y = 0 };
		var drone = new DroneBatteryElement { X = 0, Y = 0 };

		Assert.IsFalse(device.ShowIcon);
		Assert.IsFalse(device.ColorByCharge);
		Assert.IsTrue(drone.ShowIcon);
		Assert.IsTrue(drone.ColorByCharge);
	}

	[TestMethod]
	public void TheDeviceBatteryWithTheIconIsWiderAndColoredByItsCharge()
	{
		var plain = new DeviceBatteryElement { X = 0, Y = 0, Label = "B" };
		DeviceBatteryElement icon = plain with { ShowIcon = true, ColorByCharge = true };

		SKRect without = OverlayElementBounds.GetBounds(OverlayElementType.DeviceBattery, 0, 0, 1, label: plain.Label, element: plain);
		SKRect with = OverlayElementBounds.GetBounds(OverlayElementType.DeviceBattery, 0, 0, 1, label: icon.Label, element: icon);

		Assert.IsTrue(with.Width > without.Width);
		Assert.IsTrue(with.Height > without.Height);
		Assert.AreEqual(SKColor.Parse("#4CAF50"), OverlayRenderer.BatteryColor(icon, 100));
		Assert.AreEqual(SKColor.Parse("#F44336"), OverlayRenderer.BatteryColor(icon, 5));
		Assert.AreNotEqual(SKColor.Parse("#F44336"), OverlayRenderer.BatteryColor(plain, 5));
	}

	[TestMethod]
	public void TheBatterySettingsSurviveAFileAndAnOldDroneBatteryKeepsItsIcon()
	{
		var device = new DeviceBatteryElement { X = 1, Y = 2, ShowIcon = true, ColorByCharge = true, LowPercent = 40 };
		var read = (DeviceBatteryElement)JsonSerializer.Deserialize<OverlayElement>(JsonSerializer.Serialize<OverlayElement>(device))!;
		Assert.AreEqual(device, read);

		JsonObject old = JsonNode.Parse(JsonSerializer.Serialize<OverlayElement>(new DroneBatteryElement { X = 0, Y = 0 }))!.AsObject();
		old.Remove("ShowIcon");
		old.Remove("ColorByCharge");
		var drone = (DroneBatteryElement)JsonSerializer.Deserialize<OverlayElement>(old.ToJsonString())!;
		Assert.IsTrue(drone.ShowIcon);
		Assert.IsTrue(drone.ColorByCharge);
	}

	[TestMethod]
	public void EachWidgetRequiresItsOwnReading()
	{
		TelemetryFrame frame = new(0, 0, 50, 20, 100, null, 0, 0, 1, HasGpsFix: false, ImuTemperatureCelsius: 0);
		var availability = OverlayAvailability.Of([frame], false, null);
		Assert.IsTrue(OverlayDataRequirements.IsSupported(OverlayElementType.ImuTemperature, availability), "zero is a real reading, and GPS is not needed");
		foreach (OverlayElementType type in Types.Where(t => t != OverlayElementType.ImuTemperature)) Assert.IsFalse(OverlayDataRequirements.IsSupported(type, availability));
		availability = OverlayAvailability.Of([
			frame with
			{
				AmbientTemperatureCelsius = -5, DeviceDistanceMeters = 0, DeviceBatteryPercent = 0,
				AudioRmsDbfs = -128, AudioPeakDbfs = 0, WindProcessingActive = false, WindProcessingLevel = 0, ImageLuma = 0
			}
		], false, null);
		foreach (OverlayElementType type in Types) Assert.IsTrue(OverlayDataRequirements.IsSupported(type, availability));
	}

	[TestMethod]
	public void NewWidgetTypesRoundTripWithLabelsAndUnits()
	{
		var preset = new OverlayPreset("p", "sensors",
		[
			new AmbientTemperatureElement { Id = "a", X = 100, Y = 200, Units = UnitSystem.Imperial, Label = "Weather" },
			new ImuTemperatureElement { Id = "i", X = 200, Y = 200 },
			new DeviceDistanceElement { Id = "d", X = 100, Y = 400, Units = UnitSystem.Imperial },
			new DeviceBatteryElement { Id = "b", X = 200, Y = 400 },
			new AudioLevelElement { Id = "audio", X = 100, Y = 600, Label = "Mic", AccentColor = "#123456" },
			new WindProcessingElement { Id = "wind", X = 200, Y = 600 },
			new ImageLumaElement { Id = "luma", X = 100, Y = 800 }
		]);
		OverlayPreset loaded = OverlayPresetStore.Sanitize(JsonSerializer.Deserialize<OverlayPreset>(JsonSerializer.Serialize(preset)));
		CollectionAssert.AreEqual(preset.Elements, loaded.Elements);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void AudioBarRendersAccentFillAndCurrentPeakIndependentlyOfPlaybackOrder(bool shadows)
	{
		TelemetryFrame raw = new(0, 0, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false, AudioRmsDbfs: -60, AudioPeakDbfs: -50);
		List<DerivedFrame> frames = TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1, AudioRmsDbfs = -30, AudioPeakDbfs = -12 }], null);
		var element = new AudioLevelElement { X = 300, Y = 300, AccentColor = "#FF0000", TextColor = "#00FF00", ShadowEnabled = shadows };
		using var renderer = new OverlayRenderer(1280, 720, 0, [element], frames, showWatermark: false);
		byte[] first = new byte[renderer.FrameBufferSize()];
		byte[] second = new byte[first.Length];
		renderer.RenderInto(frames[1], first, premultiplied: true);
		int rmsPixel = (112 * 1280 + 150) * 4;
		Assert.AreEqual((byte)255, first[rmsPixel + 2], "RMS fill uses the widget's accent color");
		Assert.AreEqual((byte)0, first[rmsPixel + 1]);
		int peakPixel = (112 * 1280 + 265) * 4;
		Assert.AreEqual((byte)255, first[peakPixel + 1], "peak marker uses the text color");
		renderer.RenderInto(frames[0], second, premultiplied: true);
		renderer.RenderInto(frames[1], second, premultiplied: true);
		CollectionAssert.AreEqual(first, second);
	}

	[TestMethod]
	public void PhoneReadingsMakeTheirWidgetsAvailableOnlyWhenValid()
	{
		TelemetryFrame frame = new(0, 0, 0, 0, 0, null, 0, 0, 1, HeartRateBpm: 0, Phone: new PhoneReading(PressureHectopascals: 0, LightLux: -1));
		var none = OverlayAvailability.Of([frame], false, null);
		Assert.IsFalse(none.HeartRate || none.Pressure || none.Light || none.CellSignal);

		var some = OverlayAvailability.Of([frame with { HeartRateBpm = 90, Phone = new PhoneReading(PressureHectopascals: 1000, LightLux: 0, CellSignalLevel: 0) }], false, null);
		Assert.IsTrue(some.HeartRate && some.Pressure && some.Light && some.CellSignal);
		foreach (OverlayElementType type in new[] { OverlayElementType.HeartRate, OverlayElementType.Pressure, OverlayElementType.Light, OverlayElementType.CellSignal })
		{
			Assert.IsFalse(OverlayDataRequirements.IsSupported(type, none), type.ToString());
			Assert.IsTrue(OverlayDataRequirements.IsSupported(type, some), type.ToString());
		}
	}

	[TestMethod]
	public void APhonesGpsBarsFollowTheAccuracyAndTheMobileBarsThePhonesLevelOrItsDbm()
	{
		Assert.AreEqual(5, GpsSignal.LevelFromAccuracy(2.5));
		Assert.AreEqual(4, GpsSignal.LevelFromAccuracy(5));
		Assert.AreEqual(3, GpsSignal.LevelFromAccuracy(8));
		Assert.AreEqual(2, GpsSignal.LevelFromAccuracy(20));
		Assert.AreEqual(1, GpsSignal.LevelFromAccuracy(60));
		Assert.IsNull(GpsSignal.LevelFromAccuracy(null));
		Assert.IsNull(GpsSignal.LevelFromAccuracy(-1));

		Assert.AreEqual(0, GpsSignal.CellBars(0, null));
		Assert.AreEqual(5, GpsSignal.CellBars(4, -130), "the phone's own level wins over its dBm");
		Assert.AreEqual(4, GpsSignal.CellBars(3, null));
		Assert.AreEqual(5, GpsSignal.CellBars(null, -80));
		Assert.AreEqual(3, GpsSignal.CellBars(null, -100));
		Assert.AreEqual(0, GpsSignal.CellBars(null, -140));
		Assert.IsNull(GpsSignal.CellBars(null, null));
	}

	[TestMethod]
	public void TheBatterysTemperatureAndTheGpsAccuracyAreOptionsOfTheirWidgetsAndMakeThemTaller()
	{
		SKRect plain = OverlayElementBounds.GetBounds(OverlayElementType.DeviceBattery, 0, 0, 1, element: new DeviceBatteryElement { X = 0, Y = 0 });
		SKRect withTemperature = OverlayElementBounds.GetBounds(OverlayElementType.DeviceBattery, 0, 0, 1, element: new DeviceBatteryElement { X = 0, Y = 0, ShowTemperature = true });
		Assert.IsTrue(withTemperature.Bottom > plain.Bottom);
		SKRect iconPlain = OverlayElementBounds.GetBounds(OverlayElementType.DeviceBattery, 0, 0, 1, element: new DeviceBatteryElement { X = 0, Y = 0, ShowIcon = true });
		SKRect iconWithTemperature = OverlayElementBounds.GetBounds(OverlayElementType.DeviceBattery, 0, 0, 1, element: new DeviceBatteryElement { X = 0, Y = 0, ShowIcon = true, ShowTemperature = true });
		// The note sits inside the icon's height, so the box grows only where the font's descent reaches past the icon: it must hold the note, not necessarily be taller.
		Assert.IsTrue(iconWithTemperature.Bottom >= iconPlain.Bottom);
		Assert.IsTrue(iconWithTemperature.Bottom > OverlayElementBounds.DroneBatteryLineBaseline);

		SKRect gps = OverlayElementBounds.GetBounds(OverlayElementType.Satellites, 0, 0, 1, element: new SatellitesElement { X = 0, Y = 0 });
		SKRect gpsWithAccuracy = OverlayElementBounds.GetBounds(OverlayElementType.Satellites, 0, 0, 1, element: new SatellitesElement { X = 0, Y = 0, ShowAccuracy = true });
		Assert.IsTrue(gpsWithAccuracy.Bottom > gps.Bottom);

		SKRect signal = OverlayElementBounds.GetBounds(OverlayElementType.CellSignal, 0, 0, 1, element: new CellSignalElement { X = 0, Y = 0, ShowBars = false });
		SKRect signalWithBars = OverlayElementBounds.GetBounds(OverlayElementType.CellSignal, 0, 0, 1, element: new CellSignalElement { X = 0, Y = 0 });
		Assert.IsTrue(signalWithBars.Right > signal.Right);
	}

	[TestMethod]
	public void APhonesGpsAndMobileSignalDrawTheirBarsAndNotesAndStayIdempotent()
	{
		TelemetryFrame raw = new(0, 0, 53, 18, 100, null, 0, 0, 1, GpsSatellites: 18, DeviceBatteryPercent: 60,
			Phone: new PhoneReading(AccuracyMeters: 4, CellSignalDbm: -90, CellSignalLevel: 3, BatteryTemperatureCelsius: 31.5, IsCharging: true));
		List<DerivedFrame> frames = TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1 }], null);
		OverlayElement[] layout =
		[
			new SatellitesElement { X = 100, Y = 100, ShowAccuracy = true, TextAlign = TextAlign.Right }, new CellSignalElement { X = 100, Y = 400 },
			new DeviceBatteryElement { X = 100, Y = 700, ShowTemperature = true }, new DeviceBatteryElement { Id = "icon", X = 100, Y = 1000, ShowIcon = true, ShowTemperature = true }
		];
		using var renderer = new OverlayRenderer(1280, 720, 0, layout, frames, showWatermark: false);
		byte[] first = new byte[renderer.FrameBufferSize()], second = new byte[first.Length];
		renderer.RenderInto(frames[0], first, premultiplied: true);
		renderer.RenderInto(frames[1], second, premultiplied: true);

		Assert.IsTrue(first.Any(b => b != 0));
		CollectionAssert.AreEqual(first, second);
	}

	/// <summary>A widget lined up to the right ends where the preset put its right edge, whatever the reading is (the widest sample is the box).</summary>
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void ARightAlignedGsmWidgetEndsAtTheSameEdgeForAnyReading(bool satellites)
	{
		static int RightEdge(LabeledStatElement element, TelemetryFrame raw)
		{
			List<DerivedFrame> frames = TelemetryProcessor.Process([raw, raw with { FrameNumber = 1, SampleTimeSeconds = 1 }], null);
			using var renderer = new OverlayRenderer(1920, 1080, 0, [element], frames, showWatermark: false);
			byte[] buffer = new byte[renderer.FrameBufferSize()];
			renderer.RenderInto(frames[0], buffer, premultiplied: true);
			int right = -1;
			for (int y = 0; y < 1080; y++)
			for (int x = 1919; x > right; x--)
			{
				if (buffer[(y * 1920 + x) * 4 + 3] > 40)
				{
					right = x;
					break;
				}
			}

			return right;
		}

		LabeledStatElement widget = satellites
			? new SatellitesElement { X = 900, Y = 300, TextAlign = TextAlign.Right, ShowAccuracy = true, ShadowEnabled = false }
			: new CellSignalElement { X = 900, Y = 300, TextAlign = TextAlign.Right, ShadowEnabled = false };
		TelemetryFrame Frame(int dbm, int level, int sats) => new(0, 0, 53, 18, 100, null, 0, 0, 1, GpsSatellites: sats, Phone: new PhoneReading(CellSignalDbm: dbm, CellSignalLevel: level, AccuracyMeters: sats));

		int[] edges = [RightEdge(widget, Frame(-98, 4, 9)), RightEdge(widget, Frame(-113, 1, 29)), RightEdge(widget, Frame(-125, 0, 88)), RightEdge(widget, Frame(-60, 4, 3))];

		Assert.IsTrue(edges.Max() - edges.Min() <= 1, string.Join(", ", edges));
	}

	[TestMethod]
	public void PressureShowsHectopascalsOrInchesOfMercury()
	{
		Assert.AreEqual(("1013.3", "hPa"), OverlayRenderer.FormatPressure(1013.25, UnitSystem.Metric));
		Assert.AreEqual(("29.92", "inHg"), OverlayRenderer.FormatPressure(1013.25, UnitSystem.Imperial));
		Assert.AreEqual(("1013", "hPa"), OverlayRenderer.FormatPressure(1013.25, UnitSystem.Metric, 0));
		Assert.AreEqual(("--", "hPa"), OverlayRenderer.FormatPressure(null, UnitSystem.Metric));
		Assert.AreEqual(("--", "inHg"), OverlayRenderer.FormatPressure(0, UnitSystem.Imperial));
	}

	[TestMethod]
	public void TheNewPhoneWidgetsRoundTripThroughJsonAndHaveLabels()
	{
		OverlayElement[] widgets =
		[
			new HeartRateElement { X = 1, Y = 2 }, new PressureElement { X = 1, Y = 2, Units = UnitSystem.Imperial }, new LightElement { X = 1, Y = 2 }, new CellSignalElement { X = 1, Y = 2, ShowBars = false },
			new SatellitesElement { X = 1, Y = 2, ShowAccuracy = true, Units = UnitSystem.Imperial }, new DeviceBatteryElement { X = 1, Y = 2, ShowTemperature = true, Units = UnitSystem.Imperial }
		];
		foreach (OverlayElement widget in widgets)
		{
			string json = JsonSerializer.Serialize(widget);
			OverlayElement back = JsonSerializer.Deserialize<OverlayElement>(json)!;
			Assert.AreEqual(widget, back, widget.Type.ToString());
			Assert.IsFalse(string.IsNullOrEmpty(OverlayRenderer.DefaultLabel((LabeledStatElement)widget)), widget.Type.ToString());
			Assert.IsTrue(OverlayElementBounds.SupportsTextAlign(widget), widget.Type.ToString());
		}
	}

	[TestMethod]
	public void CameraDiagnosticsNeedTheirOwnValidValuesAndNoGps()
	{
		TelemetryFrame frame = new(0, 0, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false,
			AudioRmsDbfs: double.NaN, AudioPeakDbfs: 1, WindProcessingLevel: 101, ImageLuma: -1);
		var invalid = OverlayAvailability.Of([frame], false, null);
		Assert.IsFalse(invalid.AudioLevel);
		Assert.IsFalse(invalid.WindProcessing);
		Assert.IsFalse(invalid.ImageLuma);
		var valid = OverlayAvailability.Of([frame with { AudioPeakDbfs = -10, WindProcessingActive = false, ImageLuma = 255 }], false, null);
		Assert.IsTrue(valid.AudioLevel);
		Assert.IsTrue(valid.WindProcessing);
		Assert.IsTrue(valid.ImageLuma);
	}

	[TestMethod]
	[DataRow(-128.0, 0.0)]
	[DataRow(-60.0, 0.0)]
	[DataRow(-30.0, 0.5)]
	[DataRow(0.0, 1.0)]
	public void AudioBarUsesDbfsWithoutTreatingItAsLinearAmplitude(double dbfs, double expected)
	{
		Assert.AreEqual(expected, OverlayRenderer.AudioMeterFraction(dbfs), 1e-6);
	}

	[TestMethod]
	[DataRow(0.0, 32.0)]
	[DataRow(-40.0, -40.0)]
	[DataRow(100.0, 212.0)]
	public void TemperatureUsesCelsiusOrFahrenheit(double celsius, double fahrenheit)
	{
		Assert.AreEqual((celsius.ToString("0.0", CultureInfo.InvariantCulture), "°C"), OverlayRenderer.FormatTemperature(celsius, UnitSystem.Metric));
		Assert.AreEqual((fahrenheit.ToString("0.0", CultureInfo.InvariantCulture), "°F"), OverlayRenderer.FormatTemperature(celsius, UnitSystem.Imperial));
		Assert.AreEqual(("--", "°C"), OverlayRenderer.FormatTemperature(null, UnitSystem.Metric));
	}
}
