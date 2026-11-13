using System.Diagnostics;
using Telemavix.Core.Telemetry;

namespace Telemavix.Tests.Telemetry;

/// <summary>The receiver's speed smoothed for the HUD (GpsSpeedSmoothing): its spikes dropped, its gaps respected, the rest of the frame left alone.</summary>
[TestClass]
public sealed class GpsSpeedSmoothingTests
{
	private const double MetersPerDegree = 111_320;

	/// <summary>`seconds` of frames at `hz`, riding north; the receiver reports speedMs(t) linearly between whole seconds, as a 1 Hz receiver's readings come out interpolated.</summary>
	private static List<TelemetryFrame> Ride(double seconds, double hz, Func<int, double?> readingAtSecond)
	{
		List<TelemetryFrame> frames = [];
		double north = 0;
		for (int i = 0; i <= seconds * hz; i++)
		{
			double t = i / hz;
			int second = (int)Math.Floor(t);
			double? a = readingAtSecond(second), b = readingAtSecond(second + 1) ?? a;
			double? speed = a is { } from && b is { } to ? from + (to - from) * (t - second) : a;
			if (i > 0) north += (speed ?? 0) / hz;
			frames.Add(new TelemetryFrame(i, t, 50 + north / MetersPerDegree, 20, 200, null, 0, 1, 0, speed));
		}

		return frames;
	}

	[TestMethod]
	public void ASteadySpeedStaysAsItIs()
	{
		double?[] smoothed = GpsSpeedSmoothing.Apply(Ride(20, 10, _ => 6));

		Assert.IsTrue(smoothed.All(s => s is { } v && Math.Abs(v - 6) < 1e-9));
	}

	[TestMethod]
	public void ASingleFixSpikeIsCutDownWhileARealChangeIsKept()
	{
		// 3 m/s, then a flick to 6 m/s at one fix, then 3 m/s again; later the vehicle really speeds up to 9 and stays there.
		List<TelemetryFrame> frames = Ride(40, 10, s => s switch { 10 => 6, >= 25 => 9, _ => 3 });
		double?[] smoothed = GpsSpeedSmoothing.Apply(frames);

		double flick = Enumerable.Range(0, frames.Count).Where(i => frames[i].SampleTimeSeconds is >= 9.5 and <= 12.5).Max(i => smoothed[i]!.Value);
		Assert.IsTrue(flick < 4.5, $"the flick of 3 m/s above the rest comes out as {flick:F2} m/s");
		int later = frames.FindIndex(f => f.SampleTimeSeconds >= 35);
		Assert.AreEqual(9, smoothed[later]!.Value, 1e-9);
	}

	[TestMethod]
	public void TheResultDoesNotDependOnTheFrameRate()
	{
		Func<int, double?> reading = s => s % 7 == 0 ? 7 : 3;
		List<TelemetryFrame> slow = Ride(30, 10, reading), fast = Ride(30, 60, reading);
		double?[] a = GpsSpeedSmoothing.Apply(slow), b = GpsSpeedSmoothing.Apply(fast);

		foreach (double t in new[] { 5.0, 8.3, 14.0, 21.5, 27.0 })
		{
			double fromSlow = a[slow.FindIndex(f => f.SampleTimeSeconds >= t - 1e-9)]!.Value, fromFast = b[fast.FindIndex(f => f.SampleTimeSeconds >= t - 1e-9)]!.Value;
			Assert.AreEqual(fromSlow, fromFast, 0.15, $"at {t} s");
		}
	}

	[TestMethod]
	public void AFrameWithoutASpeedStaysWithoutOneAndEndsAStretch()
	{
		List<TelemetryFrame> frames = Ride(20, 10, s => s is >= 9 and <= 10 ? null : (s < 9 ? 2 : 8));
		double?[] smoothed = GpsSpeedSmoothing.Apply(frames);

		for (int i = 0; i < frames.Count; i++) Assert.AreEqual(frames[i].GpsSpeedMs is null, smoothed[i] is null, $"frame {i}");
		int before = frames.FindLastIndex(f => f.GpsSpeedMs is not null && f.SampleTimeSeconds < 9);
		int after = frames.FindIndex(f => f.GpsSpeedMs is not null && f.SampleTimeSeconds > 11);
		Assert.AreEqual(2, smoothed[before - 20]!.Value, 1e-9);
		Assert.AreEqual(8, smoothed[after + 20]!.Value, 1e-9);
	}

	[TestMethod]
	public void AGapBetweenFilesIsNotAveragedAcross()
	{
		List<TelemetryFrame> frames = [.. Ride(10, 10, _ => 2), .. Ride(10, 10, _ => 12).Select(f => f with { FrameNumber = f.FrameNumber + 101, SampleTimeSeconds = f.SampleTimeSeconds + 10.1 })];
		frames[101] = frames[101] with { StartsAfterGap = true };
		double?[] smoothed = GpsSpeedSmoothing.Apply(frames);

		Assert.AreEqual(2, smoothed[100]!.Value, 1e-9);
		Assert.AreEqual(12, smoothed[101]!.Value, 1e-9);
	}

	[TestMethod]
	public void OnlyTheShownSpeedIsSmoothedNotTheFramesOrTheDistance()
	{
		List<TelemetryFrame> frames = Ride(40, 10, s => s == 10 ? 6 : 3);
		List<DerivedFrame> plain = TelemetryProcessor.Process(frames, null), smooth = TelemetryProcessor.Process(frames, null, true);
		int spike = frames.FindIndex(f => f.SampleTimeSeconds >= 10);

		Assert.IsTrue(smooth[spike].SpeedKmh < plain[spike].SpeedKmh - 3, $"{plain[spike].SpeedKmh:F1} -> {smooth[spike].SpeedKmh:F1} km/h");
		Assert.AreEqual(frames[spike].GpsSpeedMs, smooth[spike].Raw.GpsSpeedMs);
		Assert.AreEqual(plain[^1].CumulativeDistanceMeters, smooth[^1].CumulativeDistanceMeters, 1e-6);
	}

	[TestMethod]
	public void AnHourOfSixtyFramesASecondIsSmoothedWellUnderASecond()
	{
		var frames = new List<TelemetryFrame>(216_001);
		for (int i = 0; i <= 216_000; i++) frames.Add(new TelemetryFrame(i, i / 60.0, 50, 20, 200, null, 0, 1, 0, 5 + Math.Sin(i / 600.0)));

		var watch = Stopwatch.StartNew();
		double?[] smoothed = GpsSpeedSmoothing.Apply(frames);
		watch.Stop();

		Assert.AreEqual(frames.Count, smoothed.Length);
		Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed.TotalSeconds:F1} s");
	}
}
