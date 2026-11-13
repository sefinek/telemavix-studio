namespace Telemavix.Core.Telemetry.External;

/// <summary>A 3-axis reading in the sensor's own axes (a phone's), not turned to the world.</summary>
public readonly record struct PhoneVector(double X, double Y, double Z);

/// <summary>
///     What a phone measured next to a fix (Telemavix GPS): everything optional, since a phone without a barometer has no pressure. Motion and bearing are read
///     and kept on the frame but no widget draws them - the phone's orientation on a bike is unknown, so Core takes no direction from its axes.
/// </summary>
public sealed record PhoneReading(
	double? AccuracyMeters = null,
	double? BearingDegrees = null,
	double? PressureHectopascals = null,
	double? BatteryTemperatureCelsius = null,
	bool? IsCharging = null,
	PhoneVector? Acceleration = null,
	PhoneVector? AngularVelocity = null,
	double? LightLux = null,
	int? CellSignalLevel = null,
	int? CellSignalDbm = null,
	int? Satellites = null)
{
	public bool IsEmpty => Satellites is null && AccuracyMeters is null && BearingDegrees is null && PressureHectopascals is null && BatteryTemperatureCelsius is null && IsCharging is null &&
	                       Acceleration is null && AngularVelocity is null && LightLux is null && CellSignalLevel is null && CellSignalDbm is null;
}

/// <summary>
///     What the phone measured at a moment that has no fix: Telemavix GPS writes a record of its own for every second the GPS has no signal, with no position, so the sensors
///     keep a timeline of their own. Held between two readings like the ones on a fix (ExternalTrack.PhoneAt).
/// </summary>
public sealed record ExternalPhoneSample(DateTime Utc, PhoneReading Reading);
