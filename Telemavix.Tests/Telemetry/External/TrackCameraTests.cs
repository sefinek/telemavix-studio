using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Tests.Telemetry.External;

[TestClass]
public sealed class TrackCameraTests
{
	// 10 Hz for a minute, moving east by 0.0001 degrees a frame.
	private static List<DerivedFrame> Frames(Func<int, double> speedKmh, Func<int, bool>? fix = null)
	{
		List<DerivedFrame> frames = [];
		for (int i = 0; i <= 600; i++)
		{
			var raw = new TelemetryFrame(i, i / 10.0, 50, 20 + i * 0.0001, 0, null, 0, 0, 1, HasGpsFix: fix?.Invoke(i) ?? true);
			frames.Add(new DerivedFrame(raw, speedKmh(i), 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	private const double MinMeters = 100, MaxMeters = 4000;

	private static TrackCamera.Shot? At(List<DerivedFrame> frames, double seconds)
	{
		return TrackCamera.Plan.Build(frames, 16 / 9.0, MinMeters, MaxMeters).At(seconds);
	}

	// 10 Hz for `seconds`, the position (meters east and north of a start) given by the time.
	private static List<DerivedFrame> Track(double seconds, Func<double, (double East, double North)> at, Func<double, bool>? gap = null)
	{
		List<DerivedFrame> frames = [];
		bool lastGap = false;
		for (int i = 0; i <= seconds * 10; i++)
		{
			double t = i / 10.0;
			(double east, double north) = at(t);
			bool startsAfterGap = gap?.Invoke(t) == true && !lastGap;
			lastGap = gap?.Invoke(t) == true;
			var raw = new TelemetryFrame(i, t, 50 + north / 111320, 20 + east / (111320 * Math.Cos(50 * Math.PI / 180)), 0, null, 0, 0, 1, HasGpsFix: true, StartsAfterGap: startsAfterGap);
			frames.Add(new DerivedFrame(raw, 0, 0, 0, 0, 0, 0, default, 0, 0, 1, 0, 0));
		}

		return frames;
	}

	[TestMethod]
	public void Width_IsClosestWhileStandingAndWidestOnAFastRun()
	{
		Assert.AreEqual(MinMeters, At(Track(300, _ => (0, 0)), 150)!.Value.WidthMeters, 1e-6);
		Assert.IsGreaterThan(MaxMeters * 0.5, At(Track(300, t => (t * 30, 0)), 150)!.Value.WidthMeters);
	}

	[TestMethod]
	public void Width_StaysCloseWhileCirclingRoundOneSpot()
	{
		List<DerivedFrame> frames = Track(600, t => (4 * Math.Cos(t / 8), 4 * Math.Sin(t / 8)));

		foreach (double t in new[] { 60.0, 300, 540 }) Assert.AreEqual(MinMeters, At(frames, t)!.Value.WidthMeters, 1e-6);
	}

	[TestMethod]
	public void Width_IsWiderOnALoopTheBiggerItIs()
	{
		double Loop(double radius) => At(Track(600, t => (radius * Math.Cos(t / 20), radius * Math.Sin(t / 20))), 300)!.Value.WidthMeters;

		Assert.IsTrue(Loop(60) < Loop(150) && Loop(150) < Loop(400), $"{Loop(60)} {Loop(150)} {Loop(400)}");
	}

	[TestMethod]
	public void Width_IgnoresAShortSlowdownInTheMiddleOfARun()
	{
		// 10 m/s with a 5 s stop at 100 s.
		List<DerivedFrame> frames = Track(200, t => (t < 100 ? t * 10 : t < 105 ? 1000 : 1000 + (t - 105) * 10, 0));

		double before = At(frames, 70)!.Value.WidthMeters, during = At(frames, 102.5)!.Value.WidthMeters;

		Assert.AreEqual(before, during, before * 0.15);
	}

	[TestMethod]
	public void Width_OpensBeforeAStandstillEndsInARun_AndChangesGradually()
	{
		List<DerivedFrame> frames = Track(600, t => (t < 400 ? 0 : (t - 400) * 10, 0));

		double[] widths = [.. new[] { 40.0, 300, 360, 400, 440, 500 }.Select(t => At(frames, t)!.Value.WidthMeters)];

		Assert.AreEqual(MinMeters, widths[0], 1e-6);
		for (int i = 1; i < widths.Length; i++) Assert.IsTrue(widths[i] >= widths[i - 1] - 1e-9, string.Join(" ", widths));
		Assert.IsGreaterThan(MinMeters * 1.5, widths[3]);
	}

	[TestMethod]
	public void Width_ClosesInOnALongSlowStretch_EvenWithAFastOneAhead()
	{
		List<DerivedFrame> frames = Track(700, t => t < 300 ? (t * 10, 0) : t < 420 ? (3000 + (t - 300), 0) : (3120 + (t - 420) * 10, 0));

		double fast = At(frames, 150)!.Value.WidthMeters, slow = At(frames, 360)!.Value.WidthMeters;

		Assert.IsLessThan(fast * 0.6, slow, $"{fast} {slow}");
	}

	[TestMethod]
	public void TheLastSeconds_ShowTheWholeRoute()
	{
		List<DerivedFrame> frames = Track(300, t => (t * 20, 0));
		var plan = TrackCamera.Plan.Build(frames, 16 / 9.0, MinMeters, MaxMeters);

		TrackCamera.Shot middle = plan.At(150)!.Value, end = plan.At(300)!.Value;

		Assert.AreEqual(6000 * 1.25, end.WidthMeters, 1);
		Assert.AreEqual(20 + 3000 / (111320 * Math.Cos(50 * Math.PI / 180)), end.Longitude, 1e-6);
		Assert.IsLessThan(end.WidthMeters, middle.WidthMeters);
		Assert.AreEqual(plan.At(295)!.Value.WidthMeters, end.WidthMeters, 1e-6);
	}

	[TestMethod]
	public void ATrackTooShortForAFinale_KeepsItsShot()
	{
		List<DerivedFrame> frames = Track(20, t => (t * 10, 0));

		TrackCamera.Shot end = At(frames, 20)!.Value;

		Assert.AreEqual(20 + 200 / (111320 * Math.Cos(50 * Math.PI / 180)), end.Longitude, 5e-4);
	}

	[TestMethod]
	public void Width_NeverFramesWhatLiesAcrossAGap()
	{
		List<DerivedFrame> frames = Track(300, t => t < 150 ? (4 * Math.Cos(t / 8), 4 * Math.Sin(t / 8)) : (100000 + 4 * Math.Cos(t / 8), 4 * Math.Sin(t / 8)), t => t >= 150);

		Assert.AreEqual(MinMeters, At(frames, 140)!.Value.WidthMeters, 1e-6);
		Assert.AreEqual(MinMeters, At(frames, 160)!.Value.WidthMeters, 1e-6);
	}

	[TestMethod]
	public void Width_IsSmooth()
	{
		List<DerivedFrame> frames = Track(300, t => (t < 150 ? 0 : (t - 150) * 30, 0));
		var plan = TrackCamera.Plan.Build(frames, 16 / 9.0, MinMeters, MaxMeters);

		double previous = plan.At(0)!.Value.WidthMeters;
		for (double t = 0.1; t <= 300; t += 0.1)
		{
			double width = plan.At(t)!.Value.WidthMeters;
			Assert.IsLessThan(1.06, Math.Max(width / previous, previous / width), $"{t}: {previous} -> {width}");
			previous = width;
		}
	}

	[TestMethod]
	public void Position_IsTheTrackAtThatTime()
	{
		List<DerivedFrame> frames = Frames(_ => 10);

		TrackCamera.Shot shot = At(frames, 30)!.Value;

		Assert.AreEqual(50, shot.Latitude, 1e-9);
		Assert.AreEqual(20 + 300 * 0.0001, shot.Longitude, 1e-6);
	}

	[TestMethod]
	public void TimeOutsideTheTrack_StaysOnItsEnds()
	{
		List<DerivedFrame> frames = Frames(_ => 10);

		Assert.AreEqual(20, At(frames, -5)!.Value.Longitude, 1e-3);
		Assert.AreEqual(20 + 600 * 0.0001, TrackCamera.PositionAt(frames, 500)!.Value.Longitude, 1e-3);
	}

	[TestMethod]
	public void WithoutAFixAtThatTime_TheNearestOneIsUsed()
	{
		List<DerivedFrame> frames = Frames(_ => 10, i => i < 100 || i > 400);

		TrackCamera.Shot shot = At(frames, 25)!.Value;

		Assert.AreEqual(20 + 99 * 0.0001, shot.Longitude, 2e-4);
	}

	[TestMethod]
	public void WithNoFixAtAll_ThereIsNoShot()
	{
		Assert.IsNull(At(Frames(_ => 10, _ => false), 10));
		Assert.IsNull(At([], 10));
	}

	[TestMethod]
	public void ReferenceSpeed_NeverFallsBelowAHover()
	{
		Assert.AreEqual(5, TrackCamera.ReferenceKmh(Frames(_ => 0.2)), 1e-9);
		Assert.AreEqual(5, TrackCamera.ReferenceKmh([]), 1e-9);
	}

	[TestMethod]
	public void TheSameTimeGivesTheSameShot_WhateverWasAskedBefore()
	{
		List<DerivedFrame> frames = Frames(i => i % 100);

		TrackCamera.Shot first = At(frames, 33.3)!.Value;
		At(frames, 5);
		At(frames, 58);

		Assert.AreEqual(first, At(frames, 33.3)!.Value);
	}

	[TestMethod]
	public void BetweenTwoFrames_ThePositionMovesSmoothly()
	{
		List<DerivedFrame> frames = Frames(_ => 10);

		double before = At(frames, 30.0)!.Value.Longitude;
		double quarter = At(frames, 30.025)!.Value.Longitude;
		double half = At(frames, 30.05)!.Value.Longitude;

		Assert.AreEqual(0.00005, half - before, 1e-9);
		Assert.AreEqual(0.000025, quarter - before, 1e-9);
		Assert.AreEqual(30.025 / 0.1 * 0.0001 + 20, TrackCamera.FixAt(frames, 30.025)!.Longitude, 1e-9);
	}

	[TestMethod]
	public void TheAircraftsYaw_TurnsTheShorterWayRound()
	{
		List<DerivedFrame> frames = Frames(_ => 10);
		frames[300] = frames[300] with { Raw = frames[300].Raw with { DroneAttitude = new DroneAttitude(0, 10, 350) } };
		frames[301] = frames[301] with { Raw = frames[301].Raw with { DroneAttitude = new DroneAttitude(4, 20, 10) } };

		DroneAttitude half = TrackCamera.FixAt(frames, 30.05)!.DroneAttitude!;

		double yaw = AngleMath.NormalizeDegrees(half.YawDegrees);
		Assert.IsLessThan(1e-9, Math.Min(yaw, 360 - yaw));
		Assert.AreEqual(2, half.PitchDegrees, 1e-9);
		Assert.AreEqual(15, half.RollDegrees, 1e-9);
	}

	[TestMethod]
	public void TheMarker_IsNotCarriedAcrossAGapOrWhereThereIsNoFix()
	{
		List<DerivedFrame> frames = Frames(_ => 10, i => i != 300);
		frames[301] = frames[301] with { Raw = frames[301].Raw with { StartsAfterGap = true } };

		Assert.IsNull(TrackCamera.FixAt(frames, 30.0));
		Assert.AreEqual(frames[301].Raw.Longitude, TrackCamera.FixAt(frames, 30.06)!.Longitude, 1e-9);
		Assert.AreEqual(frames[299].Raw.Longitude, TrackCamera.FixAt(frames, 29.94)!.Longitude, 1e-9);
		Assert.IsNull(TrackCamera.FixAt([], 1));
	}

	[TestMethod]
	public void TrackMapSettings_AreBroughtBackIntoRange()
	{
		Assert.AreEqual(RouteMapMosaic.MaxZoom, (new TrackMapSettings(MaxZoom: 99)).Sanitized().MaxZoom);
		Assert.AreEqual(RouteMapMosaic.MinZoom, (new TrackMapSettings(MaxZoom: -4)).Sanitized().MaxZoom);
		Assert.AreEqual(TrackMapSettings.Default, TrackMapSettings.From(new OverlaySettings()));
		Assert.AreEqual(12, TrackMapSettings.From(new OverlaySettings(TrackMap: new TrackMapSettings(MaxZoom: 12))).MaxZoom);
	}
}
