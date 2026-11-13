using System.Globalization;
using System.Text;
using System.Xml;

namespace Telemavix.Tracks;

/// <summary>
///     GPX 1.1 with Garmin's TrackPointExtension v1 (<c>atemp</c>, <c>hr</c>), the dialect bike computers, Strava and Telemavix Studio read.
///     The standard <c>sat</c> carries the satellite count. What GPX has no field for (pressure, battery, motion, light) goes into
///     the extension <see cref="TrackFields.Gpx.Namespace" />, which readers that do not know it skip. A new <c>trkseg</c> starts after every pause or gap.
///     Speed is not written: GPX has no field for it, readers derive it from the fixes.
/// </summary>
public static class GpxWriter
{
	private const string GpxNamespace = "http://www.topografix.com/GPX/1/1";
	private const string TrackPointExtensionNamespace = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
	private const string SchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";

	public static void Write(Track track, Stream stream, string creator = "Telemavix GPS")
	{
		var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false };
		using var writer = XmlWriter.Create(stream, settings);

		writer.WriteStartDocument();
		writer.WriteStartElement("gpx", GpxNamespace);
		writer.WriteAttributeString("version", "1.1");
		writer.WriteAttributeString("creator", creator);
		writer.WriteAttributeString("xmlns", "gpxtpx", null, TrackPointExtensionNamespace);
		writer.WriteAttributeString("xmlns", TrackFields.Gpx.Prefix, null, TrackFields.Gpx.Namespace);
		writer.WriteAttributeString("xmlns", "xsi", null, SchemaInstanceNamespace);
		writer.WriteAttributeString("schemaLocation", SchemaInstanceNamespace, $"{GpxNamespace} http://www.topografix.com/GPX/1/1/gpx.xsd");

		writer.WriteStartElement("trk");
		if (!string.IsNullOrWhiteSpace(track.Name)) writer.WriteElementString("name", track.Name);

		bool open = false;
		foreach (TrackPoint point in track.Points)
		{
			if (open && point.StartsAfterGap)
			{
				writer.WriteEndElement();
				open = false;
			}

			if (!open)
			{
				writer.WriteStartElement("trkseg");
				open = true;
			}

			WritePoint(writer, point);
		}

		if (open) writer.WriteEndElement();
		writer.WriteEndElement();
		writer.WriteEndElement();
		writer.WriteEndDocument();
	}

	private static void WritePoint(XmlWriter writer, TrackPoint point)
	{
		PhoneTelemetry? phone = point.Phone;

		writer.WriteStartElement("trkpt");
		writer.WriteAttributeString("lat", Number(point.Latitude, "0.00000000"));
		writer.WriteAttributeString("lon", Number(point.Longitude, "0.00000000"));
		if (point.AltitudeMeters is { } altitude) writer.WriteElementString("ele", Number(altitude, "0.0#"));
		writer.WriteElementString("time", DateTime.SpecifyKind(point.Utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
		if (phone?.Satellites is { } satellites) writer.WriteElementString("sat", satellites.ToString(CultureInfo.InvariantCulture));

		bool garmin = point.TemperatureCelsius is not null || point.HeartRateBpm is not null;
		if (!garmin && !HasTelemetry(phone))
		{
			writer.WriteEndElement();
			return;
		}

		writer.WriteStartElement("extensions");
		if (garmin)
		{
			writer.WriteStartElement("TrackPointExtension", TrackPointExtensionNamespace);
			if (point.TemperatureCelsius is { } temperature) writer.WriteElementString("atemp", TrackPointExtensionNamespace, Number(temperature, "0.#"));
			if (point.HeartRateBpm is { } heartRate) writer.WriteElementString("hr", TrackPointExtensionNamespace, heartRate.ToString(CultureInfo.InvariantCulture));
			writer.WriteEndElement();
		}

		if (HasTelemetry(phone)) WriteTelemetry(writer, phone!);
		writer.WriteEndElement();
		writer.WriteEndElement();
	}

	private static bool HasTelemetry(PhoneTelemetry? p) => p is not null &&
	                                                       (p.AccuracyMeters is not null || p.BearingDegrees is not null || p.PressureHectopascals is not null || p.BatteryPercent is not null ||
	                                                        p.BatteryTemperatureCelsius is not null || p.IsCharging is not null || p.AccelerationMetersPerSecondSquared is not null ||
	                                                        p.AngularVelocityRadiansPerSecond is not null || p.LightLux is not null || p.CellSignalLevel is not null || p.CellSignalDbm is not null);

	private static void WriteTelemetry(XmlWriter writer, PhoneTelemetry p)
	{
		writer.WriteStartElement(TrackFields.Gpx.Prefix, TrackFields.Gpx.Telemetry, TrackFields.Gpx.Namespace);
		Element(writer, TrackFields.Gpx.Accuracy, p.AccuracyMeters, "0.#");
		Element(writer, TrackFields.Gpx.Bearing, p.BearingDegrees, "0.#");
		Element(writer, TrackFields.Gpx.Pressure, p.PressureHectopascals, "0.00");
		if (p.BatteryPercent is { } battery) writer.WriteElementString(TrackFields.Gpx.Prefix, TrackFields.Gpx.Battery, TrackFields.Gpx.Namespace, battery.ToString(CultureInfo.InvariantCulture));
		Element(writer, TrackFields.Gpx.BatteryTemperature, p.BatteryTemperatureCelsius, "0.#");
		if (p.IsCharging is { } charging) writer.WriteElementString(TrackFields.Gpx.Prefix, TrackFields.Gpx.Charging, TrackFields.Gpx.Namespace, charging ? "true" : "false");
		Vector(writer, TrackFields.Gpx.Acceleration, p.AccelerationMetersPerSecondSquared);
		Vector(writer, TrackFields.Gpx.AngularVelocity, p.AngularVelocityRadiansPerSecond);
		Element(writer, TrackFields.Gpx.Light, p.LightLux, "0.#");
		if (p.CellSignalLevel is { } level) writer.WriteElementString(TrackFields.Gpx.Prefix, TrackFields.Gpx.CellLevel, TrackFields.Gpx.Namespace, level.ToString(CultureInfo.InvariantCulture));
		if (p.CellSignalDbm is { } dbm) writer.WriteElementString(TrackFields.Gpx.Prefix, TrackFields.Gpx.CellDbm, TrackFields.Gpx.Namespace, dbm.ToString(CultureInfo.InvariantCulture));
		writer.WriteEndElement();
	}

	private static void Element(XmlWriter writer, string name, double? value, string format)
	{
		if (value is { } v && double.IsFinite(v)) writer.WriteElementString(TrackFields.Gpx.Prefix, name, TrackFields.Gpx.Namespace, Number(v, format));
	}

	private static void Vector(XmlWriter writer, string name, Vector3? value)
	{
		if (value is not { } v) return;

		writer.WriteStartElement(TrackFields.Gpx.Prefix, name, TrackFields.Gpx.Namespace);
		writer.WriteAttributeString("x", Number(v.X, "0.###"));
		writer.WriteAttributeString("y", Number(v.Y, "0.###"));
		writer.WriteAttributeString("z", Number(v.Z, "0.###"));
		writer.WriteEndElement();
	}

	private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
