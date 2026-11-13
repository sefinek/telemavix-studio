using System.Buffers.Binary;
using System.Text;

namespace Telemavix.Tests.Telemetry.External;

/// <summary>A FIT activity file for the tests: records (message 20) and timer events (message 21), with the header and checksum.</summary>
internal sealed class FitBuilder
{
	// 1989-12-31 00:00 UTC is FIT's time 0.
	private static readonly DateTime Epoch = new(1989, 12, 31, 0, 0, 0, DateTimeKind.Utc);

	public const int RecordLocal = 0;
	public const int EventLocal = 1;
	public const int CompressedLocal = 2;

	private readonly List<byte> _data = [];

	public static uint Seconds(DateTime utc)
	{
		return (uint)(utc - Epoch).TotalSeconds;
	}

	public FitBuilder DefineRecord(bool big = false, bool developerField = false)
	{
		Define(RecordLocal, 20, big, developerField, (253, 4, 6), (0, 4, 5), (1, 4, 5), (78, 4, 6), (73, 4, 6));
		return this;
	}

	public FitBuilder DefineEvent()
	{
		Define(EventLocal, 21, false, false, (253, 4, 6), (0, 1, 0), (1, 1, 0));
		return this;
	}

	public FitBuilder DefineSensorRecord()
	{
		Define(RecordLocal, 20, false, false, (253, 4, 6), (0, 4, 5), (1, 4, 5), (78, 4, 6), (73, 4, 6), (5, 4, 6), (13, 1, 1));
		return this;
	}

	public FitBuilder SensorRecord(DateTime utc, double lat, double lon, double? distanceMeters, sbyte? temperature)
	{
		Record(utc, lat, lon, 80, 2);
		Number(distanceMeters is { } meters ? (uint)Math.Round(meters * 100) : uint.MaxValue, 4, false);
		_data.Add(temperature is { } celsius ? unchecked((byte)celsius) : (byte)127);
		return this;
	}

	private const int PhoneDescriptionLocal = 4, PhoneRecordLocal = 5;

	// What Telemavix GPS writes as developer fields of a record: the number, the size and the base type, in the order of its definition.
	private static readonly (int Number, int Size, int Type, string Name)[] PhoneFields =
	[
		(0, 1, 0x02, "satellites"), (1, 4, 0x86, "pressure"), (4, 1, 0x02, "charging"), (3, 4, 0x88, "battery_temperature"),
		(5, 4, 0x88, "accel_x"), (6, 4, 0x88, "accel_y"), (7, 4, 0x88, "accel_z"), (11, 4, 0x88, "light"),
		(12, 4, 0x88, "bearing"), (13, 1, 0x02, "cell_level"), (14, 1, 0x01, "cell_dbm")
	];

	/// <summary>The field_description messages for the phone's developer fields, then the record definition that carries them (with heart_rate and gps_accuracy).</summary>
	public FitBuilder DefinePhoneRecord(int developerIndex = 0)
	{
		Define(PhoneDescriptionLocal, 206, false, false, (0, 1, 2), (1, 1, 2), (2, 1, 2), (3, 32, 7));
		foreach ((int number, _, int type, string name) in PhoneFields)
		{
			_data.Add(PhoneDescriptionLocal);
			_data.Add((byte)developerIndex);
			_data.Add((byte)number);
			_data.Add((byte)type);
			byte[] text = new byte[32];
			Encoding.UTF8.GetBytes(name).CopyTo(text, 0);
			_data.AddRange(text);
		}

		_data.Add(0x60 | PhoneRecordLocal);
		_data.Add(0);
		_data.Add(0);
		_data.AddRange(new byte[] { 20, 0 });
		_data.Add(5);
		foreach ((int number, int size, int type) in new[] { (253, 4, 6), (0, 4, 5), (1, 4, 5), (3, 1, 2), (31, 1, 2) }) _data.AddRange(new[] { (byte)number, (byte)size, (byte)type });
		_data.Add((byte)PhoneFields.Length);
		foreach ((int number, int size, _, _) in PhoneFields) _data.AddRange(new[] { (byte)number, (byte)size, (byte)developerIndex });
		return this;
	}

	/// <summary>A record in the phone layout; a null value is FIT's invalid one.</summary>
	public FitBuilder PhoneRecord(DateTime utc, double? lat, double? lon, int? heartRate = null, int? accuracy = null, int? satellites = null, double? pressureHectopascals = null,
		bool? charging = null, float? batteryTemperature = null, (float X, float Y, float Z)? acceleration = null, float? light = null, float? bearing = null, int? cellLevel = null, int? cellDbm = null)
	{
		_data.Add(PhoneRecordLocal);
		Number(Seconds(utc), 4, false);
		Number(lat is { } degrees ? (uint)(int)Math.Round(degrees / 180.0 * 2147483648.0) : 0x7FFFFFFF, 4, false);
		Number(lon is { } east ? (uint)(int)Math.Round(east / 180.0 * 2147483648.0) : 0x7FFFFFFF, 4, false);
		_data.Add(heartRate is { } bpm ? (byte)bpm : byte.MaxValue);
		_data.Add(accuracy is { } meters ? (byte)meters : byte.MaxValue);
		_data.Add(satellites is { } count ? (byte)count : byte.MaxValue);
		Number(pressureHectopascals is { } hpa ? (uint)Math.Round(hpa * 100) : uint.MaxValue, 4, false);
		_data.Add(charging is { } flag ? (byte)(flag ? 1 : 0) : byte.MaxValue);
		Float(batteryTemperature);
		Float(acceleration?.X);
		Float(acceleration?.Y);
		Float(acceleration?.Z);
		Float(light);
		Float(bearing);
		_data.Add(cellLevel is { } level ? (byte)level : byte.MaxValue);
		_data.Add(cellDbm is { } dbm ? unchecked((byte)(sbyte)dbm) : (byte)0x7F);
		return this;
	}

	private void Float(float? value)
	{
		Number(value is { } v ? BitConverter.SingleToUInt32Bits(v) : uint.MaxValue, 4, false);
	}

	public FitBuilder DefineBattery(bool deviceInfo = false)
	{
		if (deviceInfo) Define(3, 23, false, false, (253, 4, 6), (0, 1, 2), (32, 1, 2));
		else Define(3, 104, false, false, (253, 4, 6), (2, 1, 2));
		return this;
	}

	public FitBuilder Battery(DateTime utc, byte percent, byte? deviceIndex = null)
	{
		_data.Add(3);
		Number(Seconds(utc), 4, false);
		if (deviceIndex is { } index) _data.Add(index);
		_data.Add(percent);
		return this;
	}

	public FitBuilder DefineCompressedRecord(bool withTimestamp = false)
	{
		Define(CompressedLocal, 20, false, false, withTimestamp ? [(253, 4, 6), (0, 4, 5), (1, 4, 5)] : [(0, 4, 5), (1, 4, 5)]);
		return this;
	}

	public FitBuilder Record(DateTime utc, double lat, double lon, double altitudeMeters, double speedMs, bool big = false, bool developerField = false)
	{
		_data.Add(RecordLocal);
		Number(Seconds(utc), 4, big);
		Number((uint)(int)Math.Round(lat / 180.0 * 2147483648.0), 4, big);
		Number((uint)(int)Math.Round(lon / 180.0 * 2147483648.0), 4, big);
		Number((uint)Math.Round((altitudeMeters + 500) * 5), 4, big);
		Number((uint)Math.Round(speedMs * 1000), 4, big);
		if (developerField) _data.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });
		return this;
	}

	public FitBuilder RecordWithoutPosition(DateTime utc)
	{
		_data.Add(RecordLocal);
		Number(Seconds(utc), 4, false);
		Number(0x7FFFFFFF, 4, false);
		Number(0x7FFFFFFF, 4, false);
		Number(0xFFFFFFFF, 4, false);
		Number(0xFFFFFFFF, 4, false);
		return this;
	}

	public FitBuilder CompressedRecord(int timeOffset, double lat, double lon)
	{
		_data.Add((byte)(0x80 | (CompressedLocal << 5) | timeOffset));
		Number((uint)(int)Math.Round(lat / 180.0 * 2147483648.0), 4, false);
		Number((uint)(int)Math.Round(lon / 180.0 * 2147483648.0), 4, false);
		return this;
	}

	public FitBuilder TimerEvent(DateTime utc, byte eventType, byte eventNumber = 0)
	{
		_data.Add(EventLocal);
		Number(Seconds(utc), 4, false);
		_data.Add(eventNumber);
		_data.Add(eventType);
		return this;
	}

	public byte[] ToArray(bool breakChecksum = false)
	{
		byte[] header = new byte[14];
		header[0] = 14;
		header[1] = 0x20;
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)_data.Count);
		".FIT"u8.CopyTo(header.AsSpan(8));

		byte[] body = [.. header, .. _data];
		byte[] file = new byte[body.Length + 2];
		body.CopyTo(file, 0);
		BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(body.Length), (ushort)(Crc(body) ^ (breakChecksum ? 1 : 0)));
		return file;
	}

	private void Define(int local, int message, bool big, bool developerField, params (int Number, int Size, int Type)[] fields)
	{
		_data.Add((byte)(0x40 | (developerField ? 0x20 : 0) | local));
		_data.Add(0);
		_data.Add((byte)(big ? 1 : 0));
		byte[] id = new byte[2];
		if (big) BinaryPrimitives.WriteUInt16BigEndian(id, (ushort)message);
		else BinaryPrimitives.WriteUInt16LittleEndian(id, (ushort)message);
		_data.AddRange(id);
		_data.Add((byte)fields.Length);
		foreach ((int number, int size, int type) in fields) _data.AddRange(new[] { (byte)number, (byte)size, (byte)type });

		if (developerField)
		{
			_data.Add(1);
			_data.AddRange(new byte[] { 0, 3, 0 });
		}
	}

	private void Number(uint value, int size, bool big)
	{
		byte[] bytes = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
		if (big) Array.Reverse(bytes);
		_data.AddRange(big ? bytes.AsSpan(4 - size).ToArray() : bytes.AsSpan(0, size).ToArray());
	}

	private static ushort Crc(byte[] bytes)
	{
		ushort[] table = [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4400];
		ushort crc = 0;
		foreach (byte b in bytes)
		{
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[b & 0xF]);
			crc = (ushort)((crc >> 4) ^ table[crc & 0xF] ^ table[(b >> 4) & 0xF]);
		}

		return crc;
	}
}
