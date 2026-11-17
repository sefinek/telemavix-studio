using System.Globalization;
using Avalonia.Controls;
using Telemavix.Core;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Gui;

/// <summary>
///     What a track file says about itself, as rows of a label and a value: what wrote it, when and how long, how far, how well it was recorded and which
///     readings it carries. One list for the card of a track opened on its own and for the file step of the Track GPS window; a row with nothing to say is left out.
/// </summary>
internal static class TrackFileRows
{
	public static List<(string Label, string Value)> Of(ExternalTrack track, string? path = null)
	{
		CultureInfo culture = CultureInfo.CurrentCulture;
		ExternalTrackFacts facts = ExternalTrackFacts.Of(track);
		List<(string Label, string Value)> rows = [];

		void Add(string label, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value));
		}

		if (path is not null) Add(Strings.TrackFile_Name, Path.GetFileName(path));
		if (track.Creator is { } creator) Add(Strings.TrackFile_Source, creator.IsTelemavixGps ? string.Format(culture, Strings.TrackFile_PhoneApp, creator.Display) : creator.Display);
		Add(Strings.TrackFile_Build, track.Creator?.Build);
		Add(Strings.TrackFile_System, track.Creator?.SystemInfo);
		Add(Strings.TrackFile_Device, track.Manufacturer ?? track.DeviceName);

		DateTime start = track.StartUtc.ToLocalFromUtc(), end = track.EndUtc.ToLocalFromUtc();
		Add(Strings.TrackFile_Recorded, $"{start:yyyy-MM-dd HH:mm:ss} - {(end.Date == start.Date ? end.ToString("HH:mm:ss", culture) : end.ToString("yyyy-MM-dd HH:mm:ss", culture))}");
		Add(Strings.TrackFile_Utc, $"{track.StartUtc:yyyy-MM-dd HH:mm:ss} - {track.EndUtc:HH:mm:ss}");
		Add(Strings.TrackFile_Points, string.Format(culture, Strings.TrackFile_PointsValue, track.Points.Count, Clock(track.EndUtc - track.StartUtc),
			string.Format(culture, Strings.TrackFile_Interval, track.MedianIntervalSeconds)));

		double meters = 0;
		for (int i = 1; i < track.Points.Count; i++)
		{
			ExternalTrackPoint a = track.Points[i - 1], b = track.Points[i];
			if (!b.StartsAfterGap) meters += TelemetryMath.HaversineMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
		}

		Add(Strings.Main_Distance, string.Format(culture, "{0:0.00} km", meters / 1000));
		if (track.Points.Where(p => p.SpeedMs is not null).Select(p => p.SpeedMs!.Value).DefaultIfEmpty(-1).Max() is >= 0 and var speed)
			Add(Strings.Common_MaxSpeed, string.Format(culture, "{0:0.#} km/h", speed * 3.6));

		// A flight log's altitude is its barometer's reference plus the height, not the ground's: the height above take-off is what it knows.
		double[] heights = [.. track.Points.Where(p => p.HeightMeters is not null).Select(p => p.HeightMeters!.Value)];
		double[] altitudes = [.. track.Points.Where(p => p.AltitudeMeters is not null).Select(p => p.AltitudeMeters!.Value)];
		if (heights.Length > 0) Add(Strings.Main_MaxHeight, string.Format(culture, "{0:0} m", heights.Max()));
		else if (altitudes.Length > 0) Add(Strings.Main_Altitude, string.Format(culture, "{0:0} - {1:0} m", altitudes.Min(), altitudes.Max()));

		int[] heartRates = [.. track.Points.Where(p => p.HeartRateBpm is > 0).Select(p => p.HeartRateBpm!.Value)];
		if (heartRates.Length > 0) Add(Strings.Main_HeartRateRange, string.Format(culture, "{0} / {1} bpm", (int)Math.Round(heartRates.Average()), heartRates.Max()));

		if (facts.PauseCount > 0) Add(Strings.TrackFile_Pauses, string.Format(culture, Strings.TrackFile_PausesValue, facts.PauseCount, Clock(facts.PausedTime)));
		if (facts.GpsLost > TimeSpan.Zero) Add(Strings.TrackFile_GpsLost, Clock(facts.GpsLost));

		List<string> gps = [];
		if (facts.MedianAccuracyMeters is { } accuracy) gps.Add(string.Format(culture, Strings.TrackFile_Accuracy, accuracy));
		if (facts.MinSatellites is { } fewest && facts.MaxSatellites is { } most) gps.Add(string.Format(culture, Strings.TrackFile_SatellitesRange, fewest, most));
		Add(Strings.TrackFile_Gps, string.Join(", ", gps));
		if (facts.BatteryStartPercent is { } from && facts.BatteryEndPercent is { } to) Add(Strings.TrackFile_Battery, string.Format(culture, Strings.TrackFile_BatteryValue, from, to));

		List<string> data = [];

		void Has(bool present, string name)
		{
			if (present) data.Add(name);
		}

		Has(facts.Data.HasFlag(TrackData.Altitude), Strings.Main_Altitude);
		Has(facts.Data.HasFlag(TrackData.Speed), Strings.Common_Speed);
		Has(facts.Data.HasFlag(TrackData.Distance), Strings.Main_Distance);
		Has(facts.Data.HasFlag(TrackData.HeartRate), Strings.Widget_HeartRate);
		Has(facts.Data.HasFlag(TrackData.Cadence), Strings.Widget_Cadence);
		Has(facts.Data.HasFlag(TrackData.Temperature), Strings.Widget_AmbientTemperature);
		Has(facts.Data.HasFlag(TrackData.Attitude), Strings.Widget_Attitude);
		Has(facts.Data.HasFlag(TrackData.Battery), Strings.Widget_DeviceBattery);
		Add(Strings.TrackFile_Data, string.Join(", ", data));

		List<string> sensors = [];

		void Senses(PhoneSensors sensor, string name)
		{
			if (facts.Sensors.HasFlag(sensor)) sensors.Add(name);
		}

		Senses(PhoneSensors.Pressure, Strings.TrackFile_Sensor_Pressure);
		Senses(PhoneSensors.Light, Strings.TrackFile_Sensor_Light);
		Senses(PhoneSensors.Acceleration, Strings.TrackFile_Sensor_Acceleration);
		Senses(PhoneSensors.AngularVelocity, Strings.TrackFile_Sensor_AngularVelocity);
		Senses(PhoneSensors.Bearing, Strings.TrackFile_Sensor_Bearing);
		Senses(PhoneSensors.CellSignal, Strings.TrackFile_Sensor_CellSignal);
		Senses(PhoneSensors.BatteryTemperature, Strings.TrackFile_Sensor_BatteryTemperature);
		Senses(PhoneSensors.Charging, Strings.TrackFile_Sensor_Charging);
		Add(Strings.TrackFile_PhoneSensors, string.Join(", ", sensors));
		return rows;
	}

	/// <summary>The rows in a host panel, replacing what it held: each a label (a fixed width, so the values line up) and a wrapping value.</summary>
	public static void Fill(Panel host, IReadOnlyList<(string Label, string Value)> rows, double labelWidth, string labelClass, string valueClass)
	{
		host.Children.Clear();
		foreach ((string label, string value) in rows)
		{
			var row = new Grid { ColumnSpacing = 8 };
			row.ColumnDefinitions.Add(new ColumnDefinition(labelWidth, GridUnitType.Pixel));
			row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
			var name = new TextBlock { Text = label };
			name.Classes.Add(labelClass);
			var text = new TextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
			text.Classes.Add(valueClass);
			Grid.SetColumn(text, 1);
			row.Children.Add(name);
			row.Children.Add(text);
			host.Children.Add(row);
		}
	}

	private static string Clock(TimeSpan length) => length.ToString(length.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
