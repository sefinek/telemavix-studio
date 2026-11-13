namespace Telemavix.Tracks;

/// <summary>
///     The names Telemavix GPS writes its readings under, in FIT and in GPX. Writers (here) and readers (Telemavix.Core) share these constants, so a field is renamed in
///     one place. Studio finds a FIT developer field by the name its field_description gave it, never by number, and a GPX reading by the element's local name.
/// </summary>
public static class TrackFields
{
	/// <summary>Names of the FIT developer fields (field_description's field_name).</summary>
	public static class Fit
	{
		public const string Satellites = "satellites";
		public const string Pressure = "pressure";
		public const string BatteryPercent = "battery_percent";
		public const string BatteryTemperature = "battery_temperature";
		public const string Charging = "charging";
		public const string AccelX = "accel_x";
		public const string AccelY = "accel_y";
		public const string AccelZ = "accel_z";
		public const string GyroX = "gyro_x";
		public const string GyroY = "gyro_y";
		public const string GyroZ = "gyro_z";
		public const string Light = "light";
		public const string Bearing = "bearing";
		public const string CellLevel = "cell_level";
		public const string CellDbm = "cell_dbm";
	}

	/// <summary>The GPX extension namespace and the local names of its elements.</summary>
	public static class Gpx
	{
		public const string Namespace = "urn:telemavix:gps:telemetry:v1";
		public const string Prefix = "tmx";
		public const string Telemetry = "Telemetry";
		public const string Accuracy = "accuracy";
		public const string Bearing = "bearing";
		public const string Pressure = "pressure";
		public const string Battery = "battery";
		public const string BatteryTemperature = "batteryTemp";
		public const string Charging = "charging";
		public const string Acceleration = "accel";
		public const string AngularVelocity = "gyro";
		public const string Light = "light";
		public const string CellLevel = "cellLevel";
		public const string CellDbm = "cellDbm";
	}
}
