using Telemavix.Core.Telemetry;

namespace Telemavix.Tests.Telemetry;

[TestClass]
public sealed class TelemetryOverviewTests
{
	private static DerivedFrame Frame(int second, double speedKmh, double altitude, double distance, Func<TelemetryFrame, TelemetryFrame>? change = null)
	{
		var raw = new TelemetryFrame(second, second, 50, 20, altitude, null, 0, 0, -1);
		return new DerivedFrame(change?.Invoke(raw) ?? raw, speedKmh, 0, 0, distance, 0, 0, default, 0, 0, 1, 0, 0);
	}

	[TestMethod]
	public void Of_NoFrames_HasNothing()
	{
		TelemetryOverview overview = TelemetryOverview.Of([]);

		Assert.AreEqual(0, overview.MovingSeconds);
		Assert.IsNull(overview.MaxHeightMeters);
		Assert.IsNull(overview.AverageHeartRateBpm);
		Assert.IsNull(overview.BatteryStartPercent);
	}

	[TestMethod]
	public void Of_ARide_CountsMovingTimeAverageAndElevation()
	{
		List<DerivedFrame> frames =
		[
			Frame(0, 0, 100, 0), Frame(1, 36, 100, 10), Frame(2, 36, 106, 20), Frame(3, 0, 106, 20), Frame(4, 36, 100, 30)
		];

		TelemetryOverview overview = TelemetryOverview.Of(frames);

		Assert.AreEqual(3, overview.MovingSeconds, 1e-9);
		Assert.AreEqual(36, overview.AverageSpeedKmh, 1e-9);
		Assert.AreEqual(6, overview.AscentMeters, 1e-9);
		Assert.AreEqual(6, overview.DescentMeters, 1e-9);
		Assert.IsNull(overview.MaxHeightMeters, "a camera has no height above take-off");
		Assert.IsNull(overview.MaxClimbMs);
	}

	[TestMethod]
	public void Of_AFlight_HasHeightDistanceFromHomeClimbAndBattery()
	{
		static TelemetryFrame Flight(TelemetryFrame f, double height, double away, double vertical, double battery) =>
			f with { HeightAboveHomeMeters = height, DistanceFromHomeMeters = away, VerticalSpeedMs = vertical, DroneBattery = new DroneBattery(battery) };

		List<DerivedFrame> frames =
		[
			Frame(0, 0, 0, 0, f => Flight(f, 0, 0, 0, 98)),
			Frame(1, 20, 5, 5, f => Flight(f, 30, 80, 4.5, 90)),
			Frame(2, 20, 5, 10, f => Flight(f, 120, 400, -2.5, 71)),
			Frame(3, 0, 5, 10, f => Flight(f, 10, 20, -1, 64))
		];

		TelemetryOverview overview = TelemetryOverview.Of(frames);

		Assert.AreEqual(120, overview.MaxHeightMeters);
		Assert.AreEqual(400, overview.MaxHomeDistanceMeters);
		Assert.AreEqual(4.5, overview.MaxClimbMs);
		Assert.AreEqual(2.5, overview.MaxSinkMs);
		Assert.AreEqual(98, overview.BatteryStartPercent);
		Assert.AreEqual(64, overview.BatteryEndPercent);
	}

	[TestMethod]
	public void Of_HeartRate_IsAveragedOverTheFramesThatHaveIt()
	{
		List<DerivedFrame> frames =
		[
			Frame(0, 10, 100, 0), Frame(1, 10, 100, 3, f => f with { HeartRateBpm = 120 }), Frame(2, 10, 100, 6, f => f with { HeartRateBpm = 160 }), Frame(3, 10, 100, 9)
		];

		TelemetryOverview overview = TelemetryOverview.Of(frames);

		Assert.AreEqual(140, overview.AverageHeartRateBpm);
		Assert.AreEqual(160, overview.MaxHeartRateBpm);
	}
}
