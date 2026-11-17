namespace Telemavix.Core.Telemetry;

/// <summary>
///     What the telemetry card says about a whole recording beyond the cached summary: the figures a ride, a flight or a track with a heart
///     rate each have their own of (null where the recording has no such reading). Speeds are the uncorrected ones, as the frames hold them.
/// </summary>
public sealed record TelemetryOverview(
	double MovingSeconds,
	double AverageSpeedKmh,
	double AscentMeters,
	double DescentMeters,
	double? MaxHeightMeters,
	double? MaxHomeDistanceMeters,
	double? MaxClimbMs,
	double? MaxSinkMs,
	int? AverageHeartRateBpm,
	int? MaxHeartRateBpm,
	double? BatteryStartPercent,
	double? BatteryEndPercent)
{
	public static TelemetryOverview Of(IReadOnlyList<DerivedFrame> frames)
	{
		if (frames.Count == 0) return new TelemetryOverview(0, 0, 0, 0, null, null, null, null, null, null, null, null);

		var stats = TripStats.Compute(frames);
		int last = frames.Count - 1;
		double? climb = null, sink = null, batteryStart = null, batteryEnd = null;
		long heartSum = 0;
		int heartCount = 0, heartMax = 0;
		foreach (DerivedFrame frame in frames)
		{
			TelemetryFrame raw = frame.Raw;
			if (raw.VerticalSpeedMs is { } vertical && double.IsFinite(vertical))
			{
				climb = Math.Max(climb ?? vertical, vertical);
				sink = Math.Min(sink ?? vertical, vertical);
			}

			if (raw.HeartRateBpm is > 0 and var bpm)
			{
				heartSum += bpm;
				heartCount++;
				heartMax = Math.Max(heartMax, bpm);
			}

			if (raw.DroneBattery is { } battery && double.IsFinite(battery.Percent))
			{
				batteryStart ??= battery.Percent;
				batteryEnd = battery.Percent;
			}
		}

		return new TelemetryOverview(
			stats.MovingSeconds(last), stats.AverageSpeedKmh(last), stats.ElevationGainMeters(last), stats.ElevationLossMeters(last),
			stats.MaxHeightMeters(last) is > 0 and var height ? height : null,
			stats.MaxHomeDistanceMeters(last) is > 0 and var away ? away : null,
			climb is > 0 ? climb : null,
			sink is < 0 ? -sink : null,
			heartCount > 0 ? (int)Math.Round((double)heartSum / heartCount) : null,
			heartCount > 0 ? heartMax : null,
			batteryStart, batteryEnd);
	}
}
