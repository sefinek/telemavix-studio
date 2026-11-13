using System.Text;
using Telemavix.Core.Telemetry.External;
using Telemavix.Tracks;

namespace Telemavix.Tests.Tracks;

[TestClass]
public sealed class PhoneTelemetryTests
{
	private static readonly DateTime Start = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

	private static PhoneTelemetry Phone(int i) => new(
		18,
		4.4,
		90,
		1002.5 - i * 0.1,
		80 - i / 10,
		31.5,
		false,
		new Vector3(0.1, 0.2, 9.8),
		new Vector3(0.01, 0.02, 0.03),
		12000);

	private static Track Sample()
	{
		List<TrackPoint> points = [];
		for (int i = 0; i < 40; i++)
		{
			points.Add(new TrackPoint(Start.AddSeconds(i), 52.2297 + i * 0.0001, 21.0122, 110 + i * 0.5, 5, DistanceMeters: i * 5.0, Phone: Phone(i)));
		}

		return new Track(points, [], "Telemetry ride");
	}

	[TestMethod]
	public void FitWithDeveloperFieldsStillReadsInStudio()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(Sample(), stream);

		ExternalTrack read = FitReader.Read(stream.ToArray());

		Assert.AreEqual(40, read.Points.Count);
		Assert.AreEqual(52.2297, read.Points[0].Latitude, 1e-6);
		Assert.AreEqual(110, read.Points[0].AltitudeMeters!.Value, 0.2);
		Assert.AreEqual(5, read.Points[0].SpeedMs!.Value, 0.001);
		Assert.AreEqual(80, read.Batteries[0].Percent, 0.001);
		Assert.IsTrue(read.Batteries.Count >= 4, "Battery is written at least every 10 s.");
	}

	[TestMethod]
	public void FitCarriesTheDeveloperFieldNames()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(Sample(), stream);

		string text = Encoding.ASCII.GetString(stream.ToArray());
		foreach (string name in new[] { "satellites", "pressure", "battery_percent", "accel_x", "gyro_z", "light" }) StringAssert.Contains(text, name);
	}

	[TestMethod]
	public void GpxCarriesSatellitesAndTelemetryAndStillReadsInStudio()
	{
		using var stream = new MemoryStream();
		GpxWriter.Write(Sample(), stream);

		string xml = Encoding.UTF8.GetString(stream.ToArray());
		StringAssert.Contains(xml, "<sat>18</sat>");
		StringAssert.Contains(xml, "<tmx:pressure>1002.50</tmx:pressure>");
		StringAssert.Contains(xml, "<tmx:battery>80</tmx:battery>");
		StringAssert.Contains(xml, "<tmx:accel x=\"0.1\" y=\"0.2\" z=\"9.8\" />");

		ExternalTrack read = GpxReader.Read(new MemoryStream(stream.ToArray()));
		Assert.AreEqual(40, read.Points.Count);
	}
}
