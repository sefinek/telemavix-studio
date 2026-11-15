using System.Text.Json;
using Bugsnag;
using Bugsnag.Payload;
using Telemavix.Core;
using Telemavix.Core.Overlay;

namespace Telemavix.Tests.Support;

[TestClass]
[DoNotParallelize]
public sealed class CrashReporterTests
{
	[TestMethod]
	public void ExistingSettingsEnableReportingAndOptOutSurvivesSerialization()
	{
		Assert.IsTrue(JsonSerializer.Deserialize<OverlaySettings>("{}")!.CrashReports);
		string json = JsonSerializer.Serialize(new OverlaySettings(CrashReports: false));
		Assert.IsFalse(JsonSerializer.Deserialize<OverlaySettings>(json)!.CrashReports);
	}

	[TestMethod]
	public void ScrubDropsReportsAfterOptOut()
	{
		bool previous = CrashReporter.Enabled;
		try
		{
			CrashReporter.Enabled = false;
			Report report = CreateReport();
			CrashReporter.Scrub(report);
			Assert.IsTrue(report.Ignored);
		}
		finally
		{
			CrashReporter.Enabled = previous;
		}
	}

	[TestMethod]
	public void ScrubRemovesIdentifyingDeviceDataAndAttachedContext()
	{
		bool previous = CrashReporter.Enabled;
		try
		{
			CrashReporter.Enabled = true;
			Report report = CreateReport();
			report.Event.Device["hostname"] = "private-host";
			report.Event.Device["id"] = "private-device-id";
			report.Event.Device["osName"] = "Test OS";
			report.Event["user"] = "private-user";
			report.Event["request"] = "private-request";
			report.Event["breadcrumbs"] = "private-breadcrumbs";
			report.Event.Metadata["settings"] = "private-settings";

			CrashReporter.Scrub(report);

			Assert.IsFalse(report.Ignored);
			Assert.AreEqual("Test OS", report.Event.Device["osName"]);
			Assert.IsFalse(report.Event.Device.ContainsKey("hostname"));
			Assert.IsFalse(report.Event.Device.ContainsKey("id"));
			Assert.IsFalse(report.Event.ContainsKey("user"));
			Assert.IsFalse(report.Event.ContainsKey("request"));
			Assert.IsFalse(report.Event.ContainsKey("breadcrumbs"));
			Assert.IsEmpty(report.Event.Metadata);
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (home.Length >= 4)
				Assert.DoesNotContain(home, report.Event.Exceptions[0].ErrorMessage);
		}
		finally
		{
			CrashReporter.Enabled = previous;
		}
	}

	private static Report CreateReport()
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "recording.mp4");
		return new Report(new Configuration("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") { AutoNotify = false, AutoCaptureSessions = false },
			new InvalidOperationException($"Cannot open {path}"), HandledState.ForHandledException(), [], null);
	}
}
