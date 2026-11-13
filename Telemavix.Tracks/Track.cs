namespace Telemavix.Tracks;

/// <summary>A 3-axis reading in the sensor's own axes (the phone's), not rotated to the world.</summary>
public readonly record struct Vector3(double X, double Y, double Z);

/// <summary>What the phone's battery reports about its own condition. Only the problems are named; a battery that says nothing has no condition at all.</summary>
public enum BatteryCondition
{
	Good,
	Overheated,
	Cold,
	Overvoltage,
	Dead,
	Failed
}

/// <summary>What the phone itself measured next to a fix (CellSignalLevel is 0 to 4 bars, CellSignalDbm the strength of the mobile signal). Everything is optional: a phone without a barometer simply has no pressure. The battery health is for the app's own warnings and is not written to FIT or GPX.</summary>
public sealed record PhoneTelemetry(
	int? Satellites = null,
	double? AccuracyMeters = null,
	double? BearingDegrees = null,
	double? PressureHectopascals = null,
	int? BatteryPercent = null,
	double? BatteryTemperatureCelsius = null,
	bool? IsCharging = null,
	Vector3? AccelerationMetersPerSecondSquared = null,
	Vector3? AngularVelocityRadiansPerSecond = null,
	double? LightLux = null,
	int? CellSignalLevel = null,
	int? CellSignalDbm = null,
	BatteryCondition? BatteryHealth = null);

/// <summary>One fix: UTC time, position and whatever the device measured at that moment.</summary>
public sealed record TrackPoint(
	DateTime Utc,
	double Latitude,
	double Longitude,
	double? AltitudeMeters = null,
	double? SpeedMetersPerSecond = null,
	double? TemperatureCelsius = null,
	int? HeartRateBpm = null,
	double? DistanceMeters = null,
	bool StartsAfterGap = false,
	PhoneTelemetry? Phone = null);

/// <summary>The phone's telemetry at a moment when there was no GPS fix to hang it on (no position, only what the phone itself measured).</summary>
public sealed record PhoneSample(DateTime Utc, PhoneTelemetry Phone);

/// <summary>A stretch of time the timer was stopped (manual pause or auto-pause).</summary>
public readonly record struct TrackPause(DateTime FromUtc, DateTime ToUtc);

public sealed class Track(IReadOnlyList<TrackPoint> points, IReadOnlyList<TrackPause>? pauses = null, string? name = null, IReadOnlyList<PhoneSample>? samples = null)
{
	public IReadOnlyList<PhoneSample> Samples { get; } = samples ?? [];
	public IReadOnlyList<TrackPoint> Points { get; } = points;
	public IReadOnlyList<TrackPause> Pauses { get; } = pauses ?? [];
	public string? Name { get; } = name;
}
