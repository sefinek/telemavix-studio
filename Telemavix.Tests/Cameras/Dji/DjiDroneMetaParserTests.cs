using Telemavix.Cameras.Dji;
using Telemavix.Core.Cameras;
using Telemavix.Core.Telemetry;

namespace Telemavix.Tests.Cameras.Dji;

[TestClass]
public sealed class DjiDroneMetaParserTests
{
	private const double Fps = 30;

	private static Proto Sample(double? lat, double? lon, long altitudeMm, float heightMm, float? north, float? east)
	{
		Proto position = new();
		if (lat is { } la && lon is { } lo) position.Message(1, new Proto().Double(2, la).Double(3, lo));
		position.Varint(2, (ulong)altitudeMm).Varint(4, 1);

		Proto velocity = new();
		if (north is { } n) velocity.Float(1, n);
		if (east is { } e) velocity.Float(2, e);

		return new Proto()
			.Message(2, new Proto()
				.Message(9, new Proto().Float(1, 120))
				.Message(10, new Proto().Bytes(1, Proto.RawVarints(1, 4000)))
				.Message(32, new Proto().Varint(1, 5600)))
			.Message(3, new Proto()
				.Message(1, new Proto().String(4, "DJI FC9589"))
				.Message(2, velocity)
				.Message(4, position)
				.Message(5, new Proto().Float(1, heightMm)))
			.Message(4, new Proto().Message(4, new Proto().Float(1, 1)));
	}

	private static byte[] Stream(params Proto[] samples)
	{
		Proto stream = new Proto().Message(1, new Proto().Message(1, new Proto().String(10, "DJI Lito X1")));
		foreach (Proto s in samples) stream.Message(3, s);
		return stream.ToArray();
	}

	[TestMethod]
	public void ParseDrone_DecodesPositionHeightSpeedAndExposure()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.ParseDrone(Stream(Sample(51.9, 16.9, 28_288, 8_900, 3, 4)), Fps);

		TelemetryFrame f = result.Frames.Single();
		Assert.AreEqual(51.9, f.Latitude);
		Assert.AreEqual(16.9, f.Longitude);
		Assert.AreEqual(28.288, f.AltitudeMeters, 1e-9);
		Assert.AreEqual(8.9, f.HeightAboveHomeMeters!.Value, 1e-9);
		Assert.AreEqual(5.0, f.GpsSpeedMs!.Value, 1e-6, "north and east velocity make the speed");
		Assert.AreEqual(120f, f.Iso);
		Assert.AreEqual(1 / 4000.0, f.ShutterSeconds!.Value, 1e-12);
		Assert.AreEqual(5600, f.ColorTemperatureKelvin);
		Assert.IsTrue(f.HasGpsFix);
		Assert.IsNull(f.GpsTimestamp, "the stream has no UTC clock");
		Assert.AreEqual("DJI Lito X1", result.CameraModel);
	}

	[TestMethod]
	public void ParseDrone_ZeroVelocityLeftOffTheWire_IsStanding()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.ParseDrone(Stream(Sample(51.9, 16.9, 28_288, 0, null, null)), Fps);

		Assert.AreEqual(0.0, result.Frames[0].GpsSpeedMs);
		Assert.AreEqual(0.0, result.Frames[0].HeightAboveHomeMeters);
	}

	[TestMethod]
	public void ParseDrone_NoPosition_CarriesTheLastFixAndFlagsIt()
	{
		TelemetryExtractionResult result = DjiMetaTelemetryParser.ParseDrone(
			Stream(Sample(51.9, 16.9, 28_288, 8_900, 1, 0), Sample(null, null, 0, 8_900, 1, 0)), Fps);

		Assert.IsFalse(result.Frames[1].HasGpsFix);
		Assert.AreEqual(51.9, result.Frames[1].Latitude);
	}

	[TestMethod]
	public void ParseDrone_ClimbRateComesFromTheHeight()
	{
		Proto[] samples = [.. Enumerable.Range(0, 90).Select(i => Sample(51.9, 16.9, 28_288, 10_000 + i * 100f, 1, 0))];

		TelemetryExtractionResult result = DjiMetaTelemetryParser.ParseDrone(Stream(samples), Fps);

		Assert.AreEqual(3.0, result.Frames[45].VerticalSpeedMs!.Value, 1e-3, "0.1 m per frame at 30 fps");
	}

	[TestMethod]
	public void ParseDrone_NoSamples_Throws()
	{
		Assert.ThrowsExactly<InvalidOperationException>(() => DjiMetaTelemetryParser.ParseDrone(Stream(), Fps));
	}
}
