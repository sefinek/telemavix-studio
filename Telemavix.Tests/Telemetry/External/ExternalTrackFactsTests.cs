using Telemavix.Core.Telemetry.External;
using Telemavix.Tracks;

namespace Telemavix.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackFactsTests
{
	private static readonly DateTime Start = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
	private static readonly TrackProducer Producer = new("Telemavix GPS", new Version(1, 2, 3));

	private static Track Ride()
	{
		List<TrackPoint> points = [];
		for (int i = 0; i < 60; i++)
		{
			if (i is >= 40 and < 47) continue;

			points.Add(new TrackPoint(Start.AddSeconds(i), 52.2297 + i * 0.0001, 21.0122, 110 + i * 0.5, 5, DistanceMeters: i * 5.0,
				Phone: new PhoneTelemetry(Satellites: 10 + i % 5, AccuracyMeters: 4 + i % 3, PressureHectopascals: 1002, BatteryPercent: 90 - i / 10, IsCharging: false,
					AccelerationMetersPerSecondSquared: new Vector3(0, 0, 9.8), LightLux: 500)));
		}

		List<PhoneSample> samples = [.. Enumerable.Range(0, 5).Select(i => new PhoneSample(Start.AddSeconds(60 + i), new PhoneTelemetry(BatteryPercent: 84, PressureHectopascals: 1002)))];
		return new Track(points, [new TrackPause(Start.AddSeconds(20), Start.AddSeconds(30))], "Ride", samples);
	}

	[TestMethod]
	public void FitSaysItIsTelemavixGpsWithItsVersion()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(Ride(), stream, Producer);

		TrackCreator creator = FitReader.Read(stream.ToArray()).Creator!;

		Assert.IsTrue(creator.IsTelemavixGps);
		Assert.AreEqual("1.2.3", creator.Version);
	}

	[TestMethod]
	public void GpxSaysItIsTelemavixGpsWithItsVersion()
	{
		using var stream = new MemoryStream();
		GpxWriter.Write(Ride(), stream, Producer);
		stream.Position = 0;

		TrackCreator creator = GpxReader.Read(stream).Creator!;

		Assert.IsTrue(creator.IsTelemavixGps);
		Assert.AreEqual("1.2.3", creator.Version);
	}

	[TestMethod]
	public void AVersionThatSaysNothingIsNotShown()
	{
		Assert.IsFalse(TrackCreator.FromGpx("Telemavix GPS 0.0")!.HasVersion);
		Assert.IsFalse(new TrackCreator("Telemavix GPS", "0.0.0").HasVersion);
		Assert.IsFalse(new TrackCreator("Garmin Connect").HasVersion);
		Assert.IsTrue(new TrackCreator("Telemavix GPS", "0.1.0").HasVersion);
	}

	[TestMethod]
	public void GpxCreatorOfAnotherProgramIsKeptAsItStands()
	{
		Assert.AreEqual(new TrackCreator("Garmin Connect"), TrackCreator.FromGpx("Garmin Connect"));
		Assert.AreEqual(new TrackCreator("Strava", "2.1.0"), TrackCreator.FromGpx(" Strava 2.1.0 "));
		Assert.IsFalse(TrackCreator.FromGpx("Strava 2.1.0")!.IsTelemavixGps);
		Assert.IsNull(TrackCreator.FromGpx("  "));
		Assert.IsNull(TrackCreator.FromGpx(null));
	}

	[TestMethod]
	public void WhatTheFileCarriesAndHowWellItWasRecorded()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(Ride(), stream, Producer);

		ExternalTrackFacts facts = ExternalTrackFacts.Of(FitReader.Read(stream.ToArray()));

		Assert.IsTrue(facts.Data.HasFlag(TrackData.Altitude) && facts.Data.HasFlag(TrackData.Speed) && facts.Data.HasFlag(TrackData.Battery));
		Assert.IsFalse(facts.Data.HasFlag(TrackData.HeartRate));
		foreach (PhoneSensors sensor in new[] { PhoneSensors.Satellites, PhoneSensors.Accuracy, PhoneSensors.Pressure, PhoneSensors.Light, PhoneSensors.Acceleration, PhoneSensors.Charging })
			Assert.IsTrue(facts.Sensors.HasFlag(sensor), sensor.ToString());
		Assert.IsFalse(facts.Sensors.HasFlag(PhoneSensors.AngularVelocity));
		Assert.AreEqual(1, facts.PauseCount);
		Assert.AreEqual(TimeSpan.FromSeconds(10), facts.PausedTime);
		Assert.AreEqual(TimeSpan.FromSeconds(8), facts.GpsLost);
		Assert.AreEqual(5, facts.MedianAccuracyMeters!.Value, 1);
		Assert.AreEqual(10, facts.MinSatellites);
		Assert.AreEqual(14, facts.MaxSatellites);
		Assert.AreEqual(90, facts.BatteryStartPercent!.Value, 0.001);
		Assert.AreEqual(84, facts.BatteryEndPercent!.Value, 0.001);
	}
}
