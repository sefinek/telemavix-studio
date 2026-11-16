namespace Telemavix.Core.Telemetry.External;

/// <summary>What a track file carries along with its route (the readings of the position).</summary>
[Flags]
public enum TrackData
{
	None = 0,
	Altitude = 1,
	Speed = 2,
	Distance = 4,
	HeartRate = 8,
	Cadence = 16,
	Temperature = 32,
	Attitude = 64,
	Battery = 128
}

/// <summary>What the phone that recorded the track (Telemavix GPS) measured besides the position.</summary>
[Flags]
public enum PhoneSensors
{
	None = 0,
	Satellites = 1,
	Accuracy = 2,
	Pressure = 4,
	Light = 8,
	Acceleration = 16,
	AngularVelocity = 32,
	Bearing = 64,
	CellSignal = 128,
	BatteryTemperature = 256,
	Charging = 512
}

/// <summary>
///     A summary of a track file for the card that describes it: what it holds and how well it was recorded (pauses, the time with no position, the GPS accuracy, the
///     phone's battery). Only what the file says - nothing is estimated.
/// </summary>
public sealed record ExternalTrackFacts(
	TrackData Data,
	PhoneSensors Sensors,
	int PauseCount,
	TimeSpan PausedTime,
	TimeSpan GpsLost,
	double? MedianAccuracyMeters,
	int? MinSatellites,
	int? MaxSatellites,
	double? BatteryStartPercent,
	double? BatteryEndPercent)
{
	public static ExternalTrackFacts Of(ExternalTrack track)
	{
		IReadOnlyList<ExternalTrackPoint> points = track.Points;
		var data = TrackData.None;
		if (points.Any(p => p.AltitudeMeters is not null)) data |= TrackData.Altitude;
		if (points.Any(p => p.SpeedMs is not null)) data |= TrackData.Speed;
		if (points.Any(p => p.DistanceMeters is not null)) data |= TrackData.Distance;
		if (points.Any(p => p.HeartRateBpm is > 0)) data |= TrackData.HeartRate;
		if (points.Any(p => p.CadencePerMinute is > 0)) data |= TrackData.Cadence;
		if (points.Any(p => p.TemperatureCelsius is not null)) data |= TrackData.Temperature;
		if (points.Any(p => p.Attitude is not null)) data |= TrackData.Attitude;
		if (track.Batteries.Count > 0) data |= TrackData.Battery;

		var sensors = PhoneSensors.None;
		List<double> accuracies = [];
		List<int> satellites = [.. points.Where(p => p.Satellites is not null).Select(p => p.Satellites!.Value)];
		foreach (PhoneReading reading in track.PhoneReadings)
		{
			if (reading.Satellites is { } count) satellites.Add(count);
			if (reading.AccuracyMeters is { } accuracy && double.IsFinite(accuracy)) accuracies.Add(accuracy);
			if (reading.PressureHectopascals is not null) sensors |= PhoneSensors.Pressure;
			if (reading.LightLux is not null) sensors |= PhoneSensors.Light;
			if (reading.Acceleration is not null) sensors |= PhoneSensors.Acceleration;
			if (reading.AngularVelocity is not null) sensors |= PhoneSensors.AngularVelocity;
			if (reading.BearingDegrees is not null) sensors |= PhoneSensors.Bearing;
			if (reading.CellSignalLevel is not null || reading.CellSignalDbm is not null) sensors |= PhoneSensors.CellSignal;
			if (reading.BatteryTemperatureCelsius is not null) sensors |= PhoneSensors.BatteryTemperature;
			if (reading.IsCharging is not null) sensors |= PhoneSensors.Charging;
		}

		if (satellites.Count > 0) sensors |= PhoneSensors.Satellites;
		if (accuracies.Count > 0) sensors |= PhoneSensors.Accuracy;

		double? median = null;
		if (accuracies.Count > 0)
		{
			accuracies.Sort();
			median = accuracies[accuracies.Count / 2];
		}

		IReadOnlyList<ExternalBatteryReading> batteries = track.Batteries;
		return new ExternalTrackFacts(data, sensors, track.Pauses.Count, TimeSpan.FromTicks(track.Pauses.Sum(p => (p.ToUtc - p.FromUtc).Ticks)),
			track.TimeWithoutFix(), median,
			satellites.Count > 0 ? satellites.Min() : null, satellites.Count > 0 ? satellites.Max() : null,
			batteries.Count >= 2 ? batteries[0].Percent : null, batteries.Count >= 2 ? batteries[^1].Percent : null);
	}
}
