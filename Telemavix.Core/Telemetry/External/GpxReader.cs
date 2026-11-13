using System.Globalization;
using System.Xml;
using Telemavix.Core.Localization;

namespace Telemavix.Core.Telemetry.External;

/// <summary>
///     The track points of a GPX file (trkpt, in every segment of every track): position, elevation and time, and where a point has them the satellite count (sat),
///     Garmin's TrackPointExtension (atemp, hr) and Telemavix GPS's own readings (TelemetryNamespace). GPX has no
///     timer events; speed is derived from consecutive fixes. Gaps have unknown motion.
///     Points without a time can't be put on the video's clock and are dropped.
///     The XML is read without resolving DTDs or external entities.
/// </summary>
public static class GpxReader
{
	private const string TrackPointExtensionNamespace = "http://www.garmin.com/xmlschemas/TrackPointExtension/v1";
	private const string TelemetryNamespace = "urn:telemavix:gps:telemetry:v1";

	public static ExternalTrack Read(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ExternalTrack Read(Stream stream)
	{
		var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
		using var reader = XmlReader.Create(stream, settings);

		List<ExternalTrackPoint> points = [];
		bool startsSegment = false, hasTrackPoints = false, hasRoute = false;
		while (reader.Read())
		{
			if (reader.NodeType != XmlNodeType.Element) continue;
			if (reader.LocalName == "trkseg") startsSegment = true;
			if (reader.LocalName is "rte" or "rtept") hasRoute = true;
			if (reader.LocalName != "trkpt") continue;

			hasTrackPoints = true;
			if (ReadPoint(reader) is not { } point) continue;
			points.Add(point with { StartsAfterGap = startsSegment && points.Count > 0 });
			startsSegment = false;
		}

		// A planned route (rte) is where to go, not when the camera was there; its points' times, when it has any, are not a ride's.
		return points.Count < 2 ? throw new InvalidDataException(hasTrackPoints ? CoreStrings.ExternalTrack_GpxNoTimes : hasRoute ? CoreStrings.ExternalTrack_GpxRouteOnly : CoreStrings.ExternalTrack_GpxEmpty) : new ExternalTrack(points);
	}

	private static ExternalTrackPoint? ReadPoint(XmlReader reader)
	{
		if (!double.TryParse(reader.GetAttribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
		    !double.TryParse(reader.GetAttribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
			return null;

		double? elevation = null, temperature = null;
		DateTime? time = null;
		int? satellites = null, heartRate = null;
		Dictionary<string, string> readings = [];
		PhoneVector? acceleration = null, angularVelocity = null;
		using (XmlReader point = reader.ReadSubtree())
		{
			point.Read();
			point.Read();
			while (!point.EOF)
			{
				if (point.NodeType == XmlNodeType.Element && point.NamespaceURI == TelemetryNamespace)
				{
					switch (point.LocalName)
					{
						case "Telemetry":
							point.Read();
							break;
						case "accel":
							acceleration = ReadVector(point);
							point.Skip();
							break;
						case "gyro":
							angularVelocity = ReadVector(point);
							point.Skip();
							break;
						default:
							readings[point.LocalName] = point.ReadElementContentAsString();
							break;
					}

					continue;
				}

				if (point.NodeType == XmlNodeType.Element && point.LocalName == "hr" && point.NamespaceURI == TrackPointExtensionNamespace)
				{
					heartRate = int.TryParse(point.ReadElementContentAsString(), NumberStyles.None, CultureInfo.InvariantCulture, out int bpm) && bpm > 0 ? bpm : null;
					continue;
				}

				if (point.NodeType == XmlNodeType.Element && point.LocalName == "atemp" && point.NamespaceURI == TrackPointExtensionNamespace)
				{
					temperature = double.TryParse(point.ReadElementContentAsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double celsius) && double.IsFinite(celsius) && celsius >= -273.15 ? celsius : null;
					continue;
				}

				if (point.NodeType != XmlNodeType.Element || point.Depth != 1)
				{
					point.Read();
					continue;
				}

				// Both of these move the reader on to the next node, so no Read after them.
				switch (point.LocalName)
				{
					case "ele":
						elevation = double.TryParse(point.ReadElementContentAsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ele) ? ele : null;
						break;
					case "time":
						time = DateTimeOffset.TryParse(point.ReadElementContentAsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset utc)
							? utc.UtcDateTime
							: null;
						break;
					case "sat":
						satellites = int.TryParse(point.ReadElementContentAsString(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count <= 250 ? count : null;
						break;
					case "extensions":
						point.Read();
						break;
					default:
						point.Skip();
						break;
				}
			}
		}

		if (time is not { } at) return null;

		var phone = new PhoneReading(
			Reading(readings, "accuracy") is >= 0 and var accuracy ? accuracy : null,
			Reading(readings, "bearing") is >= 0 and <= 360 and var bearing ? bearing : null,
			Reading(readings, "pressure") is > 0 and var pressure ? pressure : null,
			Reading(readings, "batteryTemp"),
			readings.TryGetValue("charging", out string? charging) && bool.TryParse(charging, out bool isCharging) ? isCharging : null,
			acceleration,
			angularVelocity,
			Reading(readings, "light") is >= 0 and var light ? light : null,
			Reading(readings, "cellLevel") is >= 0 and <= 4 and var level ? (int)level : null,
			Reading(readings, "cellDbm") is <= 0 and >= -140 and var dbm ? (int)dbm : null,
			satellites);
		return new ExternalTrackPoint(at, lat, lon, elevation, TemperatureCelsius: temperature, Satellites: satellites, HeartRateBpm: heartRate, Phone: phone.IsEmpty ? null : phone);
	}

	private static double? Reading(Dictionary<string, string> readings, string name)
	{
		return readings.TryGetValue(name, out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
	}

	private static PhoneVector? ReadVector(XmlReader reader)
	{
		return double.TryParse(reader.GetAttribute("x"), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
		       double.TryParse(reader.GetAttribute("y"), NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
		       double.TryParse(reader.GetAttribute("z"), NumberStyles.Float, CultureInfo.InvariantCulture, out double z) &&
		       double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
			? new PhoneVector(x, y, z)
			: null;
	}
}
