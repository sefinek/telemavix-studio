using System.Buffers.Binary;
using System.Text;
using Telemavix.Tracks;

namespace Telemavix.Core.Telemetry.External;

/// <summary>
///     The records of a Garmin FIT activity file (the format of bike computers and watches): each fix (message 20) with its
///     time, position, altitude and speed, and the timer's start and stop events (message 21), which mark recording pauses.
///     Read as the SDK describes it: a header, definition messages naming the fields of the data messages after
///     them (little or big endian), compressed timestamp headers, several files chained, CRC checked. A developer field is read only when a field_description
///     message names it with one of the names Telemavix GPS writes (ReadPhone); any other application's are skipped by size.
/// </summary>
public static class FitReader
{
	private const int RecordMessage = 20;
	private const int FieldDescriptionMessage = 206;
	private const int EventMessage = 21;
	private const int BatteryMessage = 104;
	private const int DeviceInfoMessage = 23;
	private const byte TimerEvent = 0;

	// FIT counts seconds from 1989-12-31 00:00 UTC.
	private static readonly DateTime Epoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

	private const double SemicirclesToDegrees = 180.0 / 2147483648.0;

	// The field numbers of the messages this reads.
	private const int Timestamp = 253;
	private const int Latitude = 0, Longitude = 1, Altitude = 2, HeartRate = 3, Distance = 5, Speed = 6, Temperature = 13, GpsAccuracy = 31, EnhancedSpeed = 73, EnhancedAltitude = 78;
	private const int DescriptionDeveloperIndex = 0, DescriptionNumber = 1, DescriptionName = 3;
	private const int EventNumber = 0, EventType = 1;
	private const int BatteryCapacity = 2, DeviceIndex = 0, DeviceBatteryLevel = 32;

	public static bool LooksLikeFit(ReadOnlySpan<byte> start)
	{
		return start.Length >= 12 && start[0] >= 12 && start[8] == '.' && start[9] == 'F' && start[10] == 'I' && start[11] == 'T';
	}

	public static ExternalTrack Read(string path)
	{
		return Read(File.ReadAllBytes(path));
	}

	public static ExternalTrack Read(byte[] file)
	{
		List<ExternalTrackPoint> points = [];
		List<(DateTime Utc, byte Type)> timer = [];
		List<ExternalBatteryReading> batteries = [];
		List<ExternalPhoneSample> samples = [];
		int at = 0;
		while (at + 12 <= file.Length && LooksLikeFit(file.AsSpan(at)))
			at = ReadFile(file, at, points, timer, batteries, samples);

		if (at == 0) throw new InvalidDataException("Not a FIT file.");
		return new ExternalTrack(points, Pauses(timer), batteries, phoneSamples: samples);
	}

	private sealed record Definition(bool BigEndian, int Message, (int Number, int Size, int BaseType)[] Fields, (int Number, int Size, int Index)[] Developer)
	{
		public int Size => Fields.Sum(f => f.Size) + Developer.Sum(f => f.Size);
	}

	private static int ReadFile(byte[] file, int start, List<ExternalTrackPoint> points, List<(DateTime, byte)> timer, List<ExternalBatteryReading> batteries, List<ExternalPhoneSample> samples)
	{
		int headerSize = file[start];
		long dataSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(start + 4));
		long end = start + headerSize + dataSize;
		if (end + 2 > file.Length) throw new InvalidDataException("The FIT file is cut short.");
		if (Crc(file.AsSpan(start, (int)(end - start))) != BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)end)))
			throw new InvalidDataException("The FIT file's checksum doesn't match - it is damaged.");

		var definitions = new Definition?[16];
		Dictionary<(int Index, int Number), string> descriptions = [];
		uint? lastTimestamp = null;
		int at = start + headerSize;
		while (at < end)
		{
			byte header = file[at++];
			int local;
			uint? compressed = null;
			if ((header & 0x80) != 0)
			{
				local = (header >> 5) & 0x03;
				compressed = header & 0x1Fu;
			}
			else if ((header & 0x40) != 0)
			{
				at = ReadDefinition(file, at, header, definitions);
				continue;
			}
			else
			{
				local = header & 0x0F;
			}

			Definition definition = definitions[local] ?? throw new InvalidDataException("A FIT data message has no definition.");
			int timestampBytes = compressed is not null ? definition.Fields.Where(f => f.Number == Timestamp).Sum(f => f.Size) : 0;
			int size = definition.Size - timestampBytes;
			if (at + size > end) throw new InvalidDataException("A FIT message runs past the end of the file.");

			ReadOnlySpan<byte> data = file.AsSpan(at, size);
			at += size;
			if (timestampBytes > 0) definition = definition with { Fields = [.. definition.Fields.Where(f => f.Number != Timestamp)] };

			uint? stamp = null;
			if (compressed is { } offset) stamp = lastTimestamp = Roll(lastTimestamp ?? throw new InvalidDataException("A compressed FIT timestamp has no time reference."), offset);
			else if (Field(definition, data, Timestamp) is { } full) stamp = lastTimestamp = (uint)full;

			if (definition.Message == FieldDescriptionMessage)
			{
				if (Field(definition, data, DescriptionDeveloperIndex) is { } index && Field(definition, data, DescriptionNumber) is { } number && Text(definition, data, DescriptionName) is { Length: > 0 } name)
					descriptions[((int)index, (int)number)] = name;
				continue;
			}

			if (definition.Message is not (RecordMessage or EventMessage or BatteryMessage or DeviceInfoMessage) || stamp is not { } seconds) continue;

			DateTime utc = Epoch.AddSeconds(seconds);
			if (definition.Message == BatteryMessage)
			{
				if (Field(definition, data, BatteryCapacity) is { } capacity && capacity is >= 0 and <= 100) batteries.Add(new ExternalBatteryReading(utc, capacity));
				continue;
			}

			if (definition.Message == DeviceInfoMessage)
			{
				if (Field(definition, data, DeviceIndex) == 0 && Field(definition, data, DeviceBatteryLevel) is { } level && level is >= 0 and <= 100)
					batteries.Add(new ExternalBatteryReading(utc, level));
				continue;
			}

			if (definition.Message == EventMessage)
			{
				if (Field(definition, data, EventNumber) == TimerEvent && Field(definition, data, EventType) is { } type) timer.Add((utc, (byte)type));
				continue;
			}

			(PhoneReading reading, int? satellites) = ReadPhone(definition, data, descriptions, Field(definition, data, GpsAccuracy));
			if (Field(definition, data, Latitude) is not { } lat || Field(definition, data, Longitude) is not { } lon)
			{
				// No position (the GPS had no signal): what the phone measured is still a reading, kept for the widgets that need no fix.
				if (!reading.IsEmpty) samples.Add(new ExternalPhoneSample(utc, reading));
				continue;
			}

			double? altitude = (Field(definition, data, EnhancedAltitude) ?? Field(definition, data, Altitude)) is { } a ? a / 5.0 - 500 : null;
			double? speed = (Field(definition, data, EnhancedSpeed) ?? Field(definition, data, Speed)) is { } s ? s / 1000.0 : null;
			double? temperature = Field(definition, data, Temperature);
			double? distance = Field(definition, data, Distance) is { } d ? d / 100.0 : null;
			int? heartRate = Field(definition, data, HeartRate) is { } bpm and > 0 ? (int)bpm : null;
			points.Add(new ExternalTrackPoint(utc, lat * SemicirclesToDegrees, lon * SemicirclesToDegrees, altitude, speed,
				TemperatureCelsius: temperature, DistanceMeters: distance, Satellites: satellites, HeartRateBpm: heartRate, Phone: reading.IsEmpty ? null : reading));
		}

		return (int)end + 2;
	}

	private static int ReadDefinition(byte[] file, int at, byte header, Definition?[] definitions)
	{
		if (at + 5 > file.Length) throw new InvalidDataException("A FIT definition runs past the end of the file.");

		bool big = file[at + 1] == 1;
		int message = big ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at + 2)) : BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2));
		int count = file[at + 4];
		at += 5;
		if (at + count * 3 > file.Length) throw new InvalidDataException("A FIT definition runs past the end of the file.");

		var fields = new (int, int, int)[count];
		for (int i = 0; i < count; i++, at += 3) fields[i] = (file[at], file[at + 1], file[at + 2]);

		List<(int Number, int Size, int Index)> developer = [];
		if ((header & 0x20) != 0)
		{
			if (at >= file.Length) throw new InvalidDataException("A FIT definition runs past the end of the file.");
			int developers = file[at++];
			if (at + developers * 3 > file.Length) throw new InvalidDataException("A FIT definition runs past the end of the file.");

			for (int i = 0; i < developers; i++, at += 3) developer.Add((file[at], file[at + 1], file[at + 2]));
		}

		definitions[header & 0x0F] = new Definition(big, message, fields, [.. developer]);
		return at;
	}

	/// <summary>A compressed timestamp header carries the low five bits of the time: the last full time moved on to them.</summary>
	private static uint Roll(uint last, uint offset)
	{
		uint time = (last & ~0x1Fu) + offset;
		return offset < (last & 0x1F) ? time + 0x20 : time;
	}

	/// <summary>The number in a field of a data message, null when the message doesn't have it or it holds FIT's "invalid" value.</summary>
	private static long? Field(Definition definition, ReadOnlySpan<byte> data, int number)
	{
		int at = 0;
		foreach ((int fieldNumber, int size, int baseType) in definition.Fields)
		{
			if (fieldNumber == number) return Number(data.Slice(at, size), baseType, definition.BigEndian);
			at += size;
		}

		return null;
	}

	private static readonly PhoneReading NoReading = new();

	/// <summary>A string field (field_description's name): the bytes up to the first zero, null when the message doesn't have it.</summary>
	private static string? Text(Definition definition, ReadOnlySpan<byte> data, int number)
	{
		int at = 0;
		foreach ((int fieldNumber, int size, _) in definition.Fields)
		{
			if (fieldNumber == number)
			{
				ReadOnlySpan<byte> text = data.Slice(at, size);
				int end = text.IndexOf((byte)0);
				return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
			}

			at += size;
		}

		return null;
	}

	/// <summary>
	///     What Telemavix GPS writes as developer fields of a record, found by the name its field_description gave them (the field numbers are the application's own).
	///     `accuracyMeters` is the standard gps_accuracy field, which the phone's accuracy goes into.
	/// </summary>
	private static (PhoneReading Reading, int? Satellites) ReadPhone(Definition definition, ReadOnlySpan<byte> data, Dictionary<(int Index, int Number), string> descriptions, long? accuracyMeters)
	{
		// A recording of another device has no developer fields: nothing to read, nothing to allocate for every one of its records.
		if (definition.Developer.Length == 0 && accuracyMeters is null) return (NoReading, null);

		double? pressure = null, batteryTemperature = null, light = null, bearing = null;
		double? accelX = null, accelY = null, accelZ = null, gyroX = null, gyroY = null, gyroZ = null;
		long? satellites = null, charging = null, cellLevel = null, cellDbm = null;

		int at = definition.Fields.Sum(f => f.Size);
		foreach ((int number, int size, int index) in definition.Developer)
		{
			ReadOnlySpan<byte> bytes = data.Slice(at, size);
			at += size;
			if (!descriptions.TryGetValue((index, number), out string? name)) continue;

			bool big = definition.BigEndian;
			switch (name)
			{
				case TrackFields.Fit.Satellites: satellites = Number(bytes, 0x02, big); break;
				case TrackFields.Fit.Pressure: pressure = Number(bytes, 0x86, big) is { } pascals ? pascals / 100.0 : null; break;
				case TrackFields.Fit.BatteryTemperature: batteryTemperature = Float(bytes, big); break;
				case TrackFields.Fit.Charging: charging = Number(bytes, 0x02, big); break;
				case TrackFields.Fit.AccelX: accelX = Float(bytes, big); break;
				case TrackFields.Fit.AccelY: accelY = Float(bytes, big); break;
				case TrackFields.Fit.AccelZ: accelZ = Float(bytes, big); break;
				case TrackFields.Fit.GyroX: gyroX = Float(bytes, big); break;
				case TrackFields.Fit.GyroY: gyroY = Float(bytes, big); break;
				case TrackFields.Fit.GyroZ: gyroZ = Float(bytes, big); break;
				case TrackFields.Fit.Light: light = Float(bytes, big); break;
				case TrackFields.Fit.Bearing: bearing = Float(bytes, big); break;
				case TrackFields.Fit.CellLevel: cellLevel = Number(bytes, 0x02, big); break;
				case TrackFields.Fit.CellDbm: cellDbm = Number(bytes, 0x01, big); break;
			}
		}

		var reading = new PhoneReading(
			accuracyMeters,
			bearing is >= 0 and <= 360 ? bearing : null,
			pressure is > 0 ? pressure : null,
			batteryTemperature,
			charging is <= 1 ? charging == 1 : null,
			accelX is { } ax && accelY is { } ay && accelZ is { } az ? new PhoneVector(ax, ay, az) : null,
			gyroX is { } gx && gyroY is { } gy && gyroZ is { } gz ? new PhoneVector(gx, gy, gz) : null,
			light is >= 0 ? light : null,
			cellLevel is <= 4 ? (int?)cellLevel : null,
			cellDbm is <= 0 and >= -140 ? (int?)cellDbm : null,
			satellites is <= 250 ? (int?)satellites : null);
		return (reading, reading.Satellites);
	}

	/// <summary>A FIT float32; its invalid value is all ones.</summary>
	private static double? Float(ReadOnlySpan<byte> bytes, bool big)
	{
		if (bytes.Length != 4) return null;

		uint raw = big ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
		if (raw == uint.MaxValue) return null;

		float value = BitConverter.UInt32BitsToSingle(raw);
		return float.IsFinite(value) ? value : null;
	}

	private static long? Number(ReadOnlySpan<byte> bytes, int baseType, bool big)
	{
		(int Width, bool Signed, long Invalid) type = (baseType & 0x1F) switch
		{
			0 or 2 or 13 => (1, false, 0xFFL),
			1 => (1, true, 0x7FL),
			3 => (2, true, 0x7FFFL),
			4 => (2, false, 0xFFFFL),
			5 => (4, true, 0x7FFFFFFFL),
			6 => (4, false, 0xFFFFFFFFL),
			10 => (1, false, 0L),
			11 => (2, false, 0L),
			12 => (4, false, 0L),
			_ => (0, false, 0L)
		};
		if (type.Width == 0 || bytes.Length != type.Width) return null;

		long value = type.Width switch
		{
			1 => type.Signed ? (sbyte)bytes[0] : bytes[0],
			2 => big
				? type.Signed ? BinaryPrimitives.ReadInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes)
				: type.Signed
					? BinaryPrimitives.ReadInt16LittleEndian(bytes)
					: BinaryPrimitives.ReadUInt16LittleEndian(bytes),
			_ => big
				? type.Signed ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes)
				: type.Signed
					? BinaryPrimitives.ReadInt32LittleEndian(bytes)
					: BinaryPrimitives.ReadUInt32LittleEndian(bytes)
		};
		return value == type.Invalid ? null : value;
	}

	/// <summary>
	///     The stretches the timer was stopped: from a stop (stop, stop_all, stop_disable, stop_disable_all) to the next start.
	///     A stop with no start after it ends the activity - that isn't a pause.
	/// </summary>
	private static List<ExternalTrackPause> Pauses(List<(DateTime Utc, byte Type)> timer)
	{
		List<ExternalTrackPause> pauses = [];
		DateTime? stoppedAt = null;
		foreach ((DateTime utc, byte type) in timer.OrderBy(t => t.Utc))
		{
			if (type is 1 or 4 or 8 or 9)
			{
				stoppedAt ??= utc;
			}
			else if (type == 0 && stoppedAt is { } from)
			{
				if (utc > from) pauses.Add(new ExternalTrackPause(from, utc));
				stoppedAt = null;
			}
		}

		return pauses;
	}

	private static ushort Crc(ReadOnlySpan<byte> bytes)
	{
		ReadOnlySpan<ushort> table = [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];
		ushort crc = 0;
		foreach (byte b in bytes)
		{
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[b & 0xF]);
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[(b >> 4) & 0xF]);
		}

		return crc;
	}
}
