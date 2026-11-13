using Telemavix.Core.Telemetry.External;
using Telemavix.Tracks;

namespace Telemavix.Tests.Tracks;

[TestClass]
public sealed class TrackWriterTests
{
	private static readonly DateTime Start = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

	private static Track Sample()
	{
		List<TrackPoint> points = [];
		for (int i = 0; i < 20; i++)
		{
			points.Add(new TrackPoint(Start.AddSeconds(i), 52.2297 + i * 0.0001, 21.0122 + i * 0.0001, 110 + i * 0.5, 5 + i * 0.1, 18.5, 120 + i, i * 5.0));
		}

		return new Track(points, [new TrackPause(Start.AddSeconds(8.5), Start.AddSeconds(10.5))], "Test ride");
	}

	[TestMethod]
	public void FitWithoutAnyFixIsWellFormedButNotATrack()
	{
		var phone = new PhoneTelemetry(BatteryPercent: 80, PressureHectopascals: 1000);
		var track = new Track([], samples: [new PhoneSample(Start, phone), new PhoneSample(Start.AddSeconds(1), phone)]);
		using var stream = new MemoryStream();
		FitWriter.Write(track, stream);

		Assert.ThrowsExactly<InvalidDataException>(() => FitReader.Read(stream.ToArray()));
	}

	[TestMethod]
	public void FitRoundTripsThroughStudioReader()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(Sample(), stream);

		ExternalTrack read = FitReader.Read(stream.ToArray());

		Assert.AreEqual(20, read.Points.Count);
		Assert.AreEqual(52.2297, read.Points[0].Latitude, 1e-6);
		Assert.AreEqual(21.0122, read.Points[0].Longitude, 1e-6);
		Assert.AreEqual(110, read.Points[0].AltitudeMeters!.Value, 0.2);
		Assert.AreEqual(5, read.Points[0].SpeedMs!.Value, 0.001);
		Assert.AreEqual(Start, read.Points[0].Utc);
		Assert.AreEqual(1, read.Pauses.Count);
	}

	[TestMethod]
	public void FitKeepsAnAltitudeBelowSeaLevelAndTheEnhancedSpeed()
	{
		var track = new Track([new TrackPoint(Start, 52.2297, 21.0122, -12.4, 33.3), new TrackPoint(Start.AddSeconds(1), 52.2298, 21.0123, -12.2, 33.4)]);
		using var stream = new MemoryStream();
		FitWriter.Write(track, stream);

		ExternalTrack read = FitReader.Read(stream.ToArray());

		Assert.AreEqual(-12.4, read.Points[0].AltitudeMeters!.Value, 0.2);
		Assert.AreEqual(33.3, read.Points[0].SpeedMs!.Value, 0.001);
	}

	[TestMethod]
	public void FitCutsALongNameBetweenCharactersInsteadOfFailing()
	{
		var track = new Track(Sample().Points, null, new string('ż', 100));
		using var stream = new MemoryStream();

		FitWriter.Write(track, stream);

		Assert.AreEqual(20, FitReader.Read(stream.ToArray()).Points.Count);
	}

	[TestMethod]
	public void GpxRoundTripsThroughStudioReader()
	{
		using var stream = new MemoryStream();
		GpxWriter.Write(Sample(), stream);

		ExternalTrack read = GpxReader.Read(new MemoryStream(stream.ToArray()));

		Assert.AreEqual(20, read.Points.Count);
		Assert.AreEqual(52.2297, read.Points[0].Latitude, 1e-7);
		Assert.AreEqual(110, read.Points[0].AltitudeMeters!.Value, 0.01);
		Assert.AreEqual(18.5, read.Points[0].TemperatureCelsius!.Value, 0.01);
		Assert.AreEqual(Start, read.Points[0].Utc);
	}

	private static Track PhoneTrack()
	{
		var phone = new PhoneTelemetry(Satellites: 17, AccuracyMeters: 4, BearingDegrees: 123.5, PressureHectopascals: 1009.5, BatteryPercent: 71, BatteryTemperatureCelsius: 32.5,
			IsCharging: true, AccelerationMetersPerSecondSquared: new Vector3(0.25, -9.5, 1.5), AngularVelocityRadiansPerSecond: new Vector3(0.5, 0.25, -0.125), LightLux: 850, CellSignalLevel: 3, CellSignalDbm: -95);
		return new Track([new TrackPoint(Start, 52.2297, 21.0122, 110, 5, 18.5, 142, Phone: phone), new TrackPoint(Start.AddSeconds(1), 52.2298, 21.0123, 110, 5, 18.5, 143, Phone: phone)]);
	}

	private static void AssertPhoneReadings(ExternalTrackPoint point)
	{
		Assert.AreEqual(17, point.Satellites);
		Assert.AreEqual(142, point.HeartRateBpm);
		PhoneReading phone = point.Phone!;
		Assert.AreEqual(4, phone.AccuracyMeters!.Value, 0.5);
		Assert.AreEqual(123.5, phone.BearingDegrees!.Value, 0.01);
		Assert.AreEqual(1009.5, phone.PressureHectopascals!.Value, 0.01);
		Assert.AreEqual(32.5, phone.BatteryTemperatureCelsius!.Value, 0.01);
		Assert.AreEqual(true, phone.IsCharging);
		Assert.AreEqual(1.5, phone.Acceleration!.Value.Z, 0.001);
		Assert.AreEqual(-0.125, phone.AngularVelocity!.Value.Z, 0.001);
		Assert.AreEqual(850, phone.LightLux!.Value, 0.01);
		Assert.AreEqual(3, phone.CellSignalLevel);
		Assert.AreEqual(-95, phone.CellSignalDbm);
	}

	[TestMethod]
	public void FitPhoneReadingsReachStudio()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(PhoneTrack(), stream);

		AssertPhoneReadings(FitReader.Read(stream.ToArray()).Points[0]);
	}

	[TestMethod]
	public void GpxPhoneReadingsReachStudio()
	{
		using var stream = new MemoryStream();
		GpxWriter.Write(PhoneTrack(), stream);

		AssertPhoneReadings(GpxReader.Read(new MemoryStream(stream.ToArray())).Points[0]);
	}
}
