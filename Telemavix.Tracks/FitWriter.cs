using System.Buffers.Binary;
using System.Text;

namespace Telemavix.Tracks;

/// <summary>
///     A Garmin FIT activity file laid out the way a Garmin device writes one: file_id, the sport, timer events (start/stop mark pauses), one record per fix,
///     a lap, a session (with the start position and the bounding box) and an activity. Little endian, CRC in the header and at the end. Speed and altitude are the
///     enhanced fields (73, 78) with the SDK scales (altitude 5 and offset 500, speed 1000, distance 100), which is what Telemavix Studio, Garmin Connect and Strava read.
///     The phone battery goes out as device_info (what Studio shows as the device battery), the GPS accuracy as the standard gps_accuracy, and everything FIT has no field
///     for (satellites, pressure, motion, light, ...) as developer fields of index 0 that readers not knowing them skip by size.
/// </summary>
public static class FitWriter
{
	private const byte FileIdLocal = 0, EventLocal = 1, RecordLocal = 2, LapLocal = 3, SessionLocal = 4, ActivityLocal = 5, DeveloperIdLocal = 6, FieldDescriptionLocal = 7, DeviceInfoLocal = 8, SportLocal = 9;
	private const ushort FileIdMessage = 0, SportMessage = 12, SessionMessage = 18, LapMessage = 19, RecordMessage = 20, EventMessage = 21, DeviceInfoMessage = 23, ActivityMessage = 34, FieldDescriptionMessage = 206, DeveloperDataIdMessage = 207;

	private const byte EnumType = 0x00, SInt8 = 0x01, UInt8 = 0x02, UInt16Type = 0x84, SInt32 = 0x85, UInt32Type = 0x86, StringType = 0x07, Float32 = 0x88, UInt32Z = 0x8C, ByteArray = 0x0D;

	private const byte TimerStart = 0, TimerStopAll = 4;

	/// <summary>The enums a Garmin device writes around a finished activity: event (timer 0, lap 9, session 8, activity 26), event type stop (1), session trigger activity_end (0), lap trigger session_end (7), source type local (5).</summary>
	private const byte EventTimer = 0, EventSession = 8, EventLap = 9, EventActivity = 26, EventTypeStop = 1, SessionTriggerActivityEnd = 0, LapTriggerSessionEnd = 7, SourceLocal = 5;

	/// <summary>The size of the name of a sport (the ride's name), in bytes with the closing zero.</summary>
	private const int SportNameSize = 32;

	private const int InvalidSemicircles = int.MaxValue;

	/// <summary>FIT sport 2: cycling, the sport Strava and Garmin Connect import rides as (the format has no field for the phone's vehicle).</summary>
	private const byte SportCycling = 2, SubSportGeneric = 0;

	/// <summary>A change smaller than this is GPS noise and not climb. The same threshold as the recorder's default, so the file agrees with the ride list.</summary>
	private const double AscentThresholdMeters = 3;
	private const ushort DevelopmentManufacturer = 255;
	private const ushort ProfileVersion = 21158;

	private static readonly DateTime Epoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

	private static readonly TimeSpan DeviceInfoInterval = TimeSpan.FromSeconds(10);

	private readonly record struct DevField(byte Number, string Name, string Units, byte BaseType, byte Size);

	private static readonly DevField[] DevFields =
	[
		new(0, TrackFields.Fit.Satellites, "", UInt8, 1),
		new(1, TrackFields.Fit.Pressure, "Pa", UInt32Type, 4),
		new(2, TrackFields.Fit.BatteryPercent, "%", UInt8, 1),
		new(3, TrackFields.Fit.BatteryTemperature, "C", Float32, 4),
		new(4, TrackFields.Fit.Charging, "", UInt8, 1),
		new(5, TrackFields.Fit.AccelX, "m/s^2", Float32, 4),
		new(6, TrackFields.Fit.AccelY, "m/s^2", Float32, 4),
		new(7, TrackFields.Fit.AccelZ, "m/s^2", Float32, 4),
		new(8, TrackFields.Fit.GyroX, "rad/s", Float32, 4),
		new(9, TrackFields.Fit.GyroY, "rad/s", Float32, 4),
		new(10, TrackFields.Fit.GyroZ, "rad/s", Float32, 4),
		new(11, TrackFields.Fit.Light, "lx", Float32, 4),
		new(12, TrackFields.Fit.Bearing, "deg", Float32, 4),
		new(13, TrackFields.Fit.CellLevel, "", UInt8, 1),
		new(14, TrackFields.Fit.CellDbm, "dBm", SInt8, 1)
	];

	// Identifies the application that defined the developer fields.
	private static readonly byte[] ApplicationId = [0x54, 0x45, 0x4C, 0x45, 0x4D, 0x41, 0x56, 0x49, 0x58, 0x2D, 0x47, 0x50, 0x53, 0x00, 0x00, 0x01];

	public static void Write(Track track, Stream stream)
	{
		List<Entry> timeline = Timeline(track);
		if (timeline.Count == 0) throw new ArgumentException("A FIT activity needs at least one fix or sample.", nameof(track));

		using var body = new MemoryStream();
		DateTime first = timeline[0].Utc, last = timeline[^1].Utc;

		Define(body, FileIdLocal, FileIdMessage, null, (0, 1, EnumType), (1, 2, UInt16Type), (2, 2, UInt16Type), (3, 4, UInt32Z), (4, 4, UInt32Type));
		Data(body, FileIdLocal, w =>
		{
			w.Byte(4);
			w.U16(DevelopmentManufacturer);
			w.U16(0);
			w.U32(Seconds(first) | 1);
			w.U32(Seconds(first));
		});

		Define(body, SportLocal, SportMessage, null, (3, SportNameSize, StringType), (0, 1, EnumType), (1, 1, EnumType));
		Data(body, SportLocal, w =>
		{
			w.Text(track.Name ?? "", SportNameSize);
			w.Byte(SportCycling);
			w.Byte(SubSportGeneric);
		});

		Define(body, EventLocal, EventMessage, null, (253, 4, UInt32Type), (0, 1, EnumType), (1, 1, EnumType), (4, 1, UInt8));
		WriteDeveloperDescriptions(body);
		Define(body, RecordLocal, RecordMessage, DevFields, (253, 4, UInt32Type), (0, 4, SInt32), (1, 4, SInt32), (5, 4, UInt32Type), (73, 4, UInt32Type), (78, 4, UInt32Type), (13, 1, SInt8), (3, 1, UInt8), (31, 1, UInt8));
		Define(body, DeviceInfoLocal, DeviceInfoMessage, null, (253, 4, UInt32Type), (2, 2, UInt16Type), (4, 2, UInt16Type), (0, 1, UInt8), (32, 1, UInt8), (25, 1, EnumType));
		WriteDeviceInfo(body, first, null);

		var events = new List<(DateTime Utc, byte Type)> { (first, TimerStart) };
		foreach (TrackPause pause in track.Pauses.OrderBy(p => p.FromUtc))
		{
			if (pause.FromUtc <= first || pause.ToUtc >= last || pause.ToUtc <= pause.FromUtc) continue;
			events.Add((pause.FromUtc, TimerStopAll));
			events.Add((pause.ToUtc, TimerStart));
		}

		int nextEvent = 0;
		DateTime? lastDeviceInfo = null;
		int? lastBattery = null;
		foreach (Entry entry in timeline)
		{
			while (nextEvent < events.Count && events[nextEvent].Utc <= entry.Utc) WriteEvent(body, events[nextEvent++]);
			WriteRecord(body, entry);

			if (entry.Phone?.BatteryPercent is { } battery && (lastBattery != battery || entry.Utc - lastDeviceInfo >= DeviceInfoInterval))
			{
				WriteDeviceInfo(body, entry.Utc, battery);
				lastBattery = battery;
				lastDeviceInfo = entry.Utc;
			}
		}

		while (nextEvent < events.Count) WriteEvent(body, events[nextEvent++]);
		WriteEvent(body, (last, TimerStopAll));
		WriteSummary(body, Summarize(track, first, last), first, last);

		ReadOnlySpan<byte> data = body.GetBuffer().AsSpan(0, (int)body.Length);
		Span<byte> header = stackalloc byte[14];
		header[0] = 14;
		header[1] = 0x20;
		BinaryPrimitives.WriteUInt16LittleEndian(header[2..], ProfileVersion);
		BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)data.Length);
		".FIT"u8.CopyTo(header[8..]);
		BinaryPrimitives.WriteUInt16LittleEndian(header[12..], Crc(0, header[..12]));

		stream.Write(header);
		stream.Write(data);
		Span<byte> crc = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(crc, Crc(Crc(0, header), data));
		stream.Write(crc);
	}

	/// <summary>One line of the record stream: a fix with its telemetry, or telemetry alone (no position, GPS had no signal).</summary>
	private readonly record struct Entry(DateTime Utc, TrackPoint? Point, PhoneTelemetry? Phone);

	private static List<Entry> Timeline(Track track)
	{
		List<Entry> entries = [.. track.Points.Select(p => new Entry(p.Utc, p, p.Phone)), .. track.Samples.Select(s => new Entry(s.Utc, null, s.Phone))];
		return [.. entries.OrderBy(e => e.Utc)];
	}

	private static void WriteEvent(MemoryStream body, (DateTime Utc, byte Type) e)
	{
		Data(body, EventLocal, w =>
		{
			w.U32(Seconds(e.Utc));
			w.Byte(EventTimer);
			w.Byte(e.Type);
			w.Byte(0);
		});
	}

	private static void WriteRecord(MemoryStream body, Entry entry)
	{
		Data(body, RecordLocal, entry, static (w, entry) =>
		{
			TrackPoint? p = entry.Point;
			PhoneTelemetry? phone = entry.Phone;

			// No position is FIT's own invalid value (all ones), which readers read as no fix.
			w.U32(Seconds(entry.Utc));
			w.S32(p is null ? int.MaxValue : Semicircles(p.Latitude));
			w.S32(p is null ? int.MaxValue : Semicircles(p.Longitude));
			w.U32(p?.DistanceMeters is { } d ? ScaledU32(d * 100) : uint.MaxValue);
			w.U32(p?.SpeedMetersPerSecond is { } s ? ScaledU32(s * 1000) : uint.MaxValue);
			w.U32(p?.AltitudeMeters is { } a ? ScaledU32((a + 500) * 5) : uint.MaxValue);
			w.Byte(p?.TemperatureCelsius is { } t ? (byte)(sbyte)Math.Clamp(Math.Round(t), sbyte.MinValue, 126) : (byte)0x7F);
			w.Byte(p?.HeartRateBpm is { } h ? (byte)Math.Clamp(h, 1, 254) : byte.MaxValue);
			w.Byte(phone?.AccuracyMeters is { } accuracy && accuracy >= 0 ? (byte)Math.Min(254, Math.Round(accuracy)) : byte.MaxValue);

			w.Byte(phone?.Satellites is { } satellites ? (byte)Math.Clamp(satellites, 0, 254) : byte.MaxValue);
			w.U32(phone?.PressureHectopascals is { } hpa && hpa > 0 ? (uint)Math.Round(hpa * 100) : uint.MaxValue);
			w.Byte(phone?.BatteryPercent is { } battery ? (byte)Math.Clamp(battery, 0, 100) : byte.MaxValue);
			w.F32(phone?.BatteryTemperatureCelsius);
			w.Byte(phone?.IsCharging is { } charging ? (byte)(charging ? 1 : 0) : byte.MaxValue);
			w.F32(phone?.AccelerationMetersPerSecondSquared?.X);
			w.F32(phone?.AccelerationMetersPerSecondSquared?.Y);
			w.F32(phone?.AccelerationMetersPerSecondSquared?.Z);
			w.F32(phone?.AngularVelocityRadiansPerSecond?.X);
			w.F32(phone?.AngularVelocityRadiansPerSecond?.Y);
			w.F32(phone?.AngularVelocityRadiansPerSecond?.Z);
			w.F32(phone?.LightLux);
			w.F32(phone?.BearingDegrees);
			w.Byte(phone?.CellSignalLevel is { } cellLevel ? (byte)Math.Clamp(cellLevel, 0, 254) : byte.MaxValue);
			w.Byte(phone?.CellSignalDbm is { } cellDbm ? (byte)(sbyte)Math.Clamp(cellDbm, -126, 0) : (byte)0x7F);
		});
	}

	/// <summary>The device the activity was recorded on (index 0, the creator); with a battery level it is the phone's reading at that moment, without it the first message that only names the device.</summary>
	private static void WriteDeviceInfo(MemoryStream body, DateTime utc, int? battery)
	{
		Data(body, DeviceInfoLocal, w =>
		{
			w.U32(Seconds(utc));
			w.U16(DevelopmentManufacturer);
			w.U16(0);
			w.Byte(0);
			w.Byte(battery is { } level ? (byte)Math.Clamp(level, 0, 100) : byte.MaxValue);
			w.Byte(SourceLocal);
		});
	}

	private static void WriteDeveloperDescriptions(MemoryStream body)
	{
		Define(body, DeveloperIdLocal, DeveloperDataIdMessage, null, (1, 16, ByteArray), (3, 1, UInt8));
		Data(body, DeveloperIdLocal, w =>
		{
			foreach (byte b in ApplicationId) w.Byte(b);
			w.Byte(0);
		});

		Define(body, FieldDescriptionLocal, FieldDescriptionMessage, null, (0, 1, UInt8), (1, 1, UInt8), (2, 1, UInt8), (3, 32, StringType), (8, 16, StringType));
		foreach (DevField field in DevFields)
		{
			Data(body, FieldDescriptionLocal, w =>
			{
				w.Byte(0);
				w.Byte(field.Number);
				w.Byte(field.BaseType);
				w.Text(field.Name, 32);
				w.Text(field.Units, 16);
			});
		}
	}

	private readonly record struct Place(double Latitude, double Longitude);

	/// <summary>Where the ride started and ended and the box around it (north-east and south-west corners); all null for a ride without a position.</summary>
	private readonly record struct Area(Place Start, Place End, Place NorthEast, Place SouthWest);

	private readonly record struct Summary(double ElapsedSeconds, double TimerSeconds, double DistanceMeters, double AverageSpeed, double MaxSpeed, double? Ascent, double? Descent, Area? Area, string? Name);

	private static Area? AreaOf(Track track)
	{
		if (track.Points.Count == 0) return null;

		double north = double.MinValue, south = double.MaxValue, east = double.MinValue, west = double.MaxValue;
		foreach (TrackPoint point in track.Points)
		{
			north = Math.Max(north, point.Latitude);
			south = Math.Min(south, point.Latitude);
			east = Math.Max(east, point.Longitude);
			west = Math.Min(west, point.Longitude);
		}

		TrackPoint first = track.Points[0], last = track.Points[^1];
		return new Area(new Place(first.Latitude, first.Longitude), new Place(last.Latitude, last.Longitude), new Place(north, east), new Place(south, west));
	}

	/// <summary>Total climb and descent over the points that have an altitude, counting a change only once it passes the noise threshold. Null when no point has an altitude.</summary>
	private static (double Ascent, double Descent)? Elevation(Track track)
	{
		double? reference = null;
		double ascent = 0, descent = 0;
		bool any = false;
		foreach (TrackPoint point in track.Points)
		{
			if (point.AltitudeMeters is not { } altitude) continue;

			any = true;
			if (reference is not { } from)
			{
				reference = altitude;
				continue;
			}

			double delta = altitude - from;
			if (Math.Abs(delta) < AscentThresholdMeters) continue;

			if (delta > 0) ascent += delta;
			else descent -= delta;
			reference = altitude;
		}

		return any ? (ascent, descent) : null;
	}

	private static Summary Summarize(Track track, DateTime first, DateTime last)
	{
		double elapsed = (last - first).TotalSeconds;
		double paused = track.Pauses.Sum(p => (p.ToUtc - p.FromUtc).TotalSeconds);
		double timer = Math.Max(0, elapsed - paused);
		double distance = track.Points.Count > 0 ? track.Points[^1].DistanceMeters ?? 0 : 0;
		double max = track.Points.Select(p => p.SpeedMetersPerSecond ?? 0).DefaultIfEmpty(0).Max();
		(double Ascent, double Descent)? elevation = Elevation(track);
		return new Summary(elapsed, timer, distance, timer > 0 ? distance / timer : 0, max, elevation?.Ascent, elevation?.Descent, AreaOf(track), track.Name);
	}

	private static void WriteSummary(MemoryStream body, Summary s, DateTime first, DateTime last)
	{
		Define(body, LapLocal, LapMessage, null,
			(253, 4, UInt32Type), (2, 4, UInt32Type), (3, 4, SInt32), (4, 4, SInt32), (5, 4, SInt32), (6, 4, SInt32), (7, 4, UInt32Type), (8, 4, UInt32Type), (9, 4, UInt32Type),
			(27, 4, SInt32), (28, 4, SInt32), (29, 4, SInt32), (30, 4, SInt32), (110, 4, UInt32Type), (111, 4, UInt32Type), (254, 2, UInt16Type), (21, 2, UInt16Type), (22, 2, UInt16Type),
			(0, 1, EnumType), (1, 1, EnumType), (24, 1, EnumType), (25, 1, EnumType), (39, 1, EnumType));
		Data(body, LapLocal, w =>
		{
			WriteTimes(w, s, first, last, includeEnd: true);
			WriteBox(w, s.Area);
			w.U32(ScaledU32(s.AverageSpeed * 1000));
			w.U32(ScaledU32(s.MaxSpeed * 1000));
			w.U16(0);
			w.U16(s.Ascent is { } ascent ? Scaled(ascent) : ushort.MaxValue);
			w.U16(s.Descent is { } descent ? Scaled(descent) : ushort.MaxValue);
			w.Byte(EventLap);
			w.Byte(EventTypeStop);
			w.Byte(LapTriggerSessionEnd);
			w.Byte(SportCycling);
			w.Byte(SubSportGeneric);
		});

		Define(body, SessionLocal, SessionMessage, null,
			(253, 4, UInt32Type), (2, 4, UInt32Type), (3, 4, SInt32), (4, 4, SInt32), (7, 4, UInt32Type), (8, 4, UInt32Type), (9, 4, UInt32Type),
			(29, 4, SInt32), (30, 4, SInt32), (31, 4, SInt32), (32, 4, SInt32), (110, SportNameSize, StringType), (124, 4, UInt32Type), (125, 4, UInt32Type),
			(254, 2, UInt16Type), (22, 2, UInt16Type), (23, 2, UInt16Type), (25, 2, UInt16Type), (26, 2, UInt16Type),
			(0, 1, EnumType), (1, 1, EnumType), (5, 1, EnumType), (6, 1, EnumType), (28, 1, EnumType));
		Data(body, SessionLocal, w =>
		{
			WriteTimes(w, s, first, last, includeEnd: false);
			WriteBox(w, s.Area);
			w.Text(s.Name ?? "", SportNameSize);
			w.U32(ScaledU32(s.AverageSpeed * 1000));
			w.U32(ScaledU32(s.MaxSpeed * 1000));
			w.U16(0);
			w.U16(s.Ascent is { } ascent ? Scaled(ascent) : ushort.MaxValue);
			w.U16(s.Descent is { } descent ? Scaled(descent) : ushort.MaxValue);
			w.U16(0);
			w.U16(1);
			w.Byte(EventSession);
			w.Byte(EventTypeStop);
			w.Byte(SportCycling);
			w.Byte(SubSportGeneric);
			w.Byte(SessionTriggerActivityEnd);
		});

		Define(body, ActivityLocal, ActivityMessage, null, (253, 4, UInt32Type), (0, 4, UInt32Type), (5, 4, UInt32Type), (1, 2, UInt16Type), (2, 1, EnumType), (3, 1, EnumType), (4, 1, EnumType));
		Data(body, ActivityLocal, w =>
		{
			w.U32(Seconds(last));
			w.U32((uint)Math.Round(s.TimerSeconds * 1000));
			w.U32((uint)Math.Max(0, Seconds(last) + (long)TimeZoneInfo.Local.GetUtcOffset(last).TotalSeconds));
			w.U16(1);
			w.Byte(0);
			w.Byte(EventActivity);
			w.Byte(EventTypeStop);
		});
	}

	/// <summary>The fields a lap and a session start with: the end and the start time, where it started (and, for a lap, where it ended), and the elapsed and timer time and the distance.</summary>
	private static void WriteTimes(FitData w, Summary s, DateTime first, DateTime last, bool includeEnd)
	{
		w.U32(Seconds(last));
		w.U32(Seconds(first));
		WritePlace(w, s.Area?.Start);
		if (includeEnd) WritePlace(w, s.Area?.End);
		w.U32((uint)Math.Round(s.ElapsedSeconds * 1000));
		w.U32((uint)Math.Round(s.TimerSeconds * 1000));
		w.U32((uint)Math.Round(s.DistanceMeters * 100));
	}

	/// <summary>The north-east and south-west corners of the box around the ride, each as latitude and longitude.</summary>
	private static void WriteBox(FitData w, Area? area)
	{
		WritePlace(w, area?.NorthEast);
		WritePlace(w, area?.SouthWest);
	}

	private static void WritePlace(FitData w, Place? place)
	{
		w.S32(place is { } p ? Semicircles(p.Latitude) : InvalidSemicircles);
		w.S32(place is { } q ? Semicircles(q.Longitude) : InvalidSemicircles);
	}

	private static void Define(MemoryStream body, byte local, ushort message, DevField[]? developer, params (int Number, int Size, byte BaseType)[] fields)
	{
		body.WriteByte((byte)(0x40 | (developer is null ? 0 : 0x20) | local));
		body.WriteByte(0);
		body.WriteByte(0);
		Span<byte> id = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(id, message);
		body.Write(id);
		body.WriteByte((byte)fields.Length);
		foreach ((int number, int size, byte baseType) in fields)
		{
			body.WriteByte((byte)number);
			body.WriteByte((byte)size);
			body.WriteByte(baseType);
		}

		if (developer is null) return;

		body.WriteByte((byte)developer.Length);
		foreach (DevField field in developer)
		{
			body.WriteByte(field.Number);
			body.WriteByte(field.Size);
			body.WriteByte(0);
		}
	}

	private static void Data(MemoryStream body, byte local, Action<FitData> fill)
	{
		body.WriteByte(local);
		fill(new FitData(body));
	}

	/// <summary>The hot path (one record per fix): the state is passed in, so the callback can be static and nothing is allocated for it per record.</summary>
	private static void Data<T>(MemoryStream body, byte local, T state, Action<FitData, T> fill)
	{
		body.WriteByte(local);
		fill(new FitData(body), state);
	}

	private readonly struct FitData(MemoryStream stream)
	{
		public void Byte(byte value) => stream.WriteByte(value);

		public void U16(ushort value)
		{
			Span<byte> b = stackalloc byte[2];
			BinaryPrimitives.WriteUInt16LittleEndian(b, value);
			stream.Write(b);
		}

		public void U32(uint value)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(b, value);
			stream.Write(b);
		}

		public void S32(int value)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteInt32LittleEndian(b, value);
			stream.Write(b);
		}

		/// <summary>FIT float32; the invalid value is all ones.</summary>
		public void F32(double? value)
		{
			Span<byte> b = stackalloc byte[4];
			if (value is { } v && double.IsFinite(v)) BinaryPrimitives.WriteSingleLittleEndian(b, (float)v);
			else BinaryPrimitives.WriteUInt32LittleEndian(b, uint.MaxValue);
			stream.Write(b);
		}

		/// <summary>A fixed-size, zero-padded UTF-8 string field (the last byte stays zero). A longer text is cut between characters, never in the middle of one.</summary>
		public void Text(string value, int size)
		{
			Span<byte> b = stackalloc byte[size];
			b.Clear();
			int written = 0;
			foreach (Rune rune in value.EnumerateRunes())
			{
				int length = rune.Utf8SequenceLength;
				if (written + length > size - 1) break;

				rune.EncodeToUtf8(b[written..]);
				written += length;
			}

			stream.Write(b);
		}
	}

	private static ushort Scaled(double value) => value < 0 || value >= ushort.MaxValue ? ushort.MaxValue : (ushort)Math.Round(value);

	private static uint ScaledU32(double value) => value < 0 || value >= uint.MaxValue ? uint.MaxValue : (uint)Math.Round(value);

	private static int Semicircles(double degrees) => (int)Math.Round(degrees * (2147483648.0 / 180.0));

	private static uint Seconds(DateTime utc) => (uint)Math.Max(0, (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - Epoch).TotalSeconds);

	private static ushort Crc(ushort crc, ReadOnlySpan<byte> bytes)
	{
		ReadOnlySpan<ushort> table = [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];
		foreach (byte b in bytes)
		{
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[b & 0xF]);
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[(b >> 4) & 0xF]);
		}

		return crc;
	}
}
