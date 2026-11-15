using Microsoft.Win32.SafeHandles;
using Telemavix.Core.Cameras;
using Telemavix.Core.Telemetry;

namespace Telemavix.Cameras.Dji;

/// <summary>
///     The djmd stream of a DJI drone with its own camera (Lito X1; the stream names it "dvtm_Lito_X1.proto"). Same container
///     as the Osmo Action's, another layout - one top-level field-3 message per video frame:
///     f3
///     ├── f2 (message)              - camera settings
///     │   ├── f9  (message) → f1 (float)  - ISO
///     │   ├── f10 (message) → f1 (bytes: two raw varints [numerator, denominator]) - shutter speed
///     │   └── f32 (message) → f1 (varint) - color temperature, Kelvin
///     ├── f3 (message)              - the aircraft
///     │   ├── f2 (message)          - velocity: f1/f2 (float) = north/east, m/s (a zero is left off the wire)
///     │   ├── f4 (message)          - position: f1 → f2/f3 (double) latitude/longitude in degrees (no unit field), f2 (varint) altitude in mm
///     │   └── f5 (message) → f1 (float) - height above the take-off point, mm
///     └── f4 (message)              - the gimbal's orientation quaternion (f4: f1..f4 = w/x/y/z; roll stays 0, so it is the camera's, not the aircraft's)
///     The first sample also carries the stream's header (top-level f1: f1 → f10 is the product name, "DJI Lito X1"). Checked against
///     the recording's SRT sidecar on one Lito X1 flight (position, height, ISO, color temperature, and the speed against the
///     distance flown); the sample has no UTC clock, the altitude is the take-off reference plus the height (so not sea level), and
///     gps_status-like f4.f4 is 1 on every sample (not read).
/// </summary>
internal static partial class DjiMetaTelemetryParser
{
	/// <summary>
	///     Whether the file's djmd stream is a drone's layout: its first sample has the aircraft message (f3 → f3), which the
	///     Osmo Action's samples don't. False for anything unreadable, so that format stays with the Osmo Action's.
	/// </summary>
	public static bool IsDroneLayout(string inputPath)
	{
		try
		{
			if (Mp4CameraMetadata.ReadSourceTracks(inputPath).FirstOrDefault(t => t.Format == "djmd") is not { Sizes.Length: > 0 } track) return false;

			byte[] first = new byte[track.Sizes[0]];
			using SafeFileHandle file = File.OpenHandle(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
			if (RandomAccess.Read(file, first, (long)track.Offsets[0]) != first.Length) return false;

			foreach ((int fieldNumber, int wireType, ReadOnlyMemory<byte> bytes, _) in IterFields(first))
			{
				if (fieldNumber == 3 && wireType == 2) return GetSubmessage(bytes, 3) is not null;
			}

			return false;
		}
		catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
		{
			return false;
		}
	}

	public static TelemetryExtractionResult ParseDrone(ReadOnlyMemory<byte> rawData, double fps)
	{
		var records = new List<DroneRecord>();
		string? product = null;
		foreach ((int fieldNumber, int wireType, ReadOnlyMemory<byte> bytes, _) in IterFields(rawData))
		{
			if (wireType != 2) continue;

			if (fieldNumber == 1 && product is null && GetSubmessage(bytes, 1) is { } header) product = GetStringField(header, 10);
			else if (fieldNumber == 3) records.Add(ParseDroneSample(bytes));
		}

		if (records.Count == 0) throw new InvalidOperationException("No djmd telemetry samples decoded from the raw stream.");

		var frames = new List<TelemetryFrame>(records.Count);
		var gpsFill = new GpsForwardFill();
		for (int i = 0; i < records.Count; i++)
		{
			DroneRecord r = records[i];
			(double lat, double lon, double altitudeMeters, bool hasFix) = gpsFill.Apply(r.Lat, r.Lon, r.AltitudeMeters);
			frames.Add(new TelemetryFrame(i, i / fps, lat, lon, altitudeMeters, null, 0, 0, 0, r.SpeedMs, r.Iso, r.ShutterSeconds, r.ColorTemperatureKelvin,
				hasFix, HeightAboveHomeMeters: r.HeightMeters));
		}

		string? model = product ?? records.Select(r => r.DeviceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
		return new TelemetryExtractionResult(WithVerticalSpeed(frames, fps), model);
	}

	/// <summary>The climb rate from the height over a second around each frame (the height comes in steps of 0.1 m, as in FlightLog).</summary>
	private static List<TelemetryFrame> WithVerticalSpeed(List<TelemetryFrame> frames, double fps)
	{
		int halfWindow = Math.Max((int)Math.Round(fps / 2), 1);
		var result = new List<TelemetryFrame>(frames.Count);
		for (int i = 0; i < frames.Count; i++)
		{
			TelemetryFrame from = frames[Math.Max(i - halfWindow, 0)], to = frames[Math.Min(i + halfWindow, frames.Count - 1)];
			double span = to.SampleTimeSeconds - from.SampleTimeSeconds;
			result.Add(frames[i].HeightAboveHomeMeters is not null && from.HeightAboveHomeMeters is { } h0 && to.HeightAboveHomeMeters is { } h1 && span > 0
				? frames[i] with { VerticalSpeedMs = (h1 - h0) / span }
				: frames[i]);
		}

		return result;
	}

	private static DroneRecord ParseDroneSample(ReadOnlyMemory<byte> sample)
	{
		float? iso = null;
		double? shutterSeconds = null;
		int? colorTemperatureKelvin = null;
		if (GetSubmessage(sample, 2) is { } settings)
		{
			if (GetSubmessage(settings, 9) is { } isoMsg) iso = GetFloatField(isoMsg, 1);

			if (GetSubmessage(settings, 10) is { } shutterMsg && GetSubmessage(shutterMsg, 1) is { Length: > 0 } fraction)
			{
				int position = 0;
				ulong numerator = ReadVarint(fraction.Span, ref position);
				if (position < fraction.Length && ReadVarint(fraction.Span, ref position) is var denominator and > 0) shutterSeconds = (double)numerator / denominator;
			}

			if (GetSubmessage(settings, 32) is { } temperatureMsg && GetVarintField(temperatureMsg, 1) is { } kelvin) colorTemperatureKelvin = (int)kelvin;
		}

		double? lat = null, lon = null, altitudeMeters = null, speedMs = null, heightMeters = null;
		string? deviceName = null;
		if (GetSubmessage(sample, 3) is { } aircraft)
		{
			if (GetSubmessage(aircraft, 1) is { } info) deviceName = GetStringField(info, 4);

			if (GetSubmessage(aircraft, 2) is { } velocity)
			{
				float north = GetFloatField(velocity, 1) ?? 0, east = GetFloatField(velocity, 2) ?? 0;
				speedMs = Math.Sqrt(north * north + east * east);
			}

			if (GetSubmessage(aircraft, 4) is { } position)
			{
				if (GetSubmessage(position, 1) is { } coordinates
				    && DjiGpsFix.Position(1, null, GetDoubleField(coordinates, 2), GetDoubleField(coordinates, 3)) is var (latVal, lonVal))
				{
					lat = latVal;
					lon = lonVal;
				}

				if (GetVarintField(position, 2) is { } altitudeMm) altitudeMeters = altitudeMm / 1000.0;
			}

			if (GetSubmessage(aircraft, 5) is { } height) heightMeters = (GetFloatField(height, 1) ?? 0) / 1000.0;
		}

		return new DroneRecord(lat, lon, altitudeMeters, speedMs, heightMeters, deviceName, iso, shutterSeconds, colorTemperatureKelvin);
	}

	private readonly record struct DroneRecord(
		double? Lat,
		double? Lon,
		double? AltitudeMeters,
		double? SpeedMs,
		double? HeightMeters,
		string? DeviceName,
		float? Iso,
		double? ShutterSeconds,
		int? ColorTemperatureKelvin);
}
