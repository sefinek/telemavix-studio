using System.Text;
using Telemavix.Core.Telemetry.External;
using Telemavix.Tracks;

namespace Telemavix.Tests.Tracks;

[TestClass]
public sealed class CellSignalTests
{
	private static Track Sample() => new([
		.. Enumerable.Range(0, 20).Select(i => new TrackPoint(
			new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc).AddSeconds(i), 52.0 + i * 1e-4, 21.0, 100, 5, DistanceMeters: i * 5.0,
			Phone: new PhoneTelemetry(CellSignalLevel: 3, CellSignalDbm: -95)))
	]);

	[TestMethod]
	public void GpxCarriesTheMobileSignal()
	{
		using var stream = new MemoryStream();
		GpxWriter.Write(Sample(), stream);

		string xml = Encoding.UTF8.GetString(stream.ToArray());
		StringAssert.Contains(xml, "<tmx:cellLevel>3</tmx:cellLevel>");
		StringAssert.Contains(xml, "<tmx:cellDbm>-95</tmx:cellDbm>");
		Assert.AreEqual(20, GpxReader.Read(new MemoryStream(stream.ToArray())).Points.Count);
	}

	[TestMethod]
	public void FitCarriesTheMobileSignalAndStillReadsInStudio()
	{
		using var stream = new MemoryStream();
		FitWriter.Write(Sample(), stream);

		StringAssert.Contains(Encoding.ASCII.GetString(stream.ToArray()), "cell_dbm");
		Assert.AreEqual(20, FitReader.Read(stream.ToArray()).Points.Count);
	}
}
