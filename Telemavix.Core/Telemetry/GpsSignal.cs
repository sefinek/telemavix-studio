namespace Telemavix.Core.Telemetry;

/// <summary>
///     The 0 to 5 bars of the signal widgets for readings that are not a drone's own GPS level (TelemetryFrame.GpsLevel). A phone's GPS has no such level, so the bars follow the
///     fix's accuracy: a heuristic of ours, not the phone's report. The mobile signal's bars are the phone's own 0 to 4 level (or, without it, its strength in dBm) on the same five bars.
/// </summary>
internal static class GpsSignal
{
	/// <summary>5 bars for an accuracy of 3 m or better, down to 1 for worse than 20 m; null without a reading.</summary>
	public static int? LevelFromAccuracy(double? meters)
	{
		return meters is not { } accuracy || !double.IsFinite(accuracy) || accuracy < 0
			? null
			: accuracy <= 3 ? 5
			: accuracy <= 5 ? 4
			: accuracy <= 10 ? 3
			: accuracy <= 20 ? 2
			: 1;
	}

	/// <summary>The phone's level (0 to 4) on five bars, or the bars a strength in dBm makes: -85 or stronger is full, every 10 dBm weaker is one bar less.</summary>
	public static int? CellBars(int? level, int? dbm)
	{
		if (level is { } reported) return (int)Math.Round(Math.Clamp(reported, 0, 4) * 5 / 4.0, MidpointRounding.AwayFromZero);
		if (dbm is not { } strength) return null;

		return strength >= -85 ? 5 : strength >= -95 ? 4 : strength >= -105 ? 3 : strength >= -115 ? 2 : strength >= -125 ? 1 : 0;
	}
}
