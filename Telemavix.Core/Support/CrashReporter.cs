using Bugsnag;
using Bugsnag.Payload;
using Telemavix.Core.Logging;
using Telemavix.Core.Overlay;
using Telemavix.Core.Updates;
using Exception = System.Exception;

namespace Telemavix.Core;

public static class CrashReporter
{
	private static readonly HashSet<string> DeviceFields = new(StringComparer.OrdinalIgnoreCase)
	{
		"osName", "osVersion", "runtimeVersions"
	};

	private static Client? _client;
	private static volatile bool _enabled = true;
	private static readonly bool Available =
#if DEBUG
		false;
#else
		true;
#endif

	public static bool Enabled
	{
		get => _enabled;
		set => _enabled = value;
	}

	public static void Start(OverlaySettings settings, string appType)
	{
		Enabled = settings.CrashReports;
		if (!Available || _client is not null || BuildInfo.BugsnagApiKey is not { Length: > 0 } apiKey) return;

		try
		{
			var configuration = new Configuration(apiKey)
			{
				AppVersion = AppUpdates.CurrentVersion.ToFullString(),
				AppType = appType,
				ReleaseStage = "production",
				NotifyReleaseStages = ["production"],
				ProjectNamespaces = ["Telemavix"],
				AutoNotify = true,
				AutoCaptureSessions = false,
				MaximumBreadcrumbs = 0
			};
			var client = new Client(configuration);
			client.BeforeNotify(Scrub);
			_client = client;
			OverlaySettingsStore.Saved += saved => Enabled = saved.CrashReports;
		}
		catch (Exception ex)
		{
			AppLogger.Info($"Crash reporting could not be initialized: {ex}");
		}
	}

	public static bool Notify(Exception exception)
	{
		if (!Enabled || _client is not { } client || exception is OperationCanceledException) return false;

		try
		{
			client.Notify(exception, Severity.Warning);
			return true;
		}
		catch (Exception ex)
		{
			AppLogger.Info($"Crash report could not be submitted: {ex}");
			return false;
		}
	}

	internal static void Scrub(Report report)
	{
		if (!Enabled)
		{
			report.Ignore();
			return;
		}

		Device device = report.Event.Device;
		foreach (string key in device.Keys.Where(key => !DeviceFields.Contains(key)).ToList())
			device.Remove(key);

		report.Event.Remove("user");
		report.Event.Remove("request");
		report.Event.Remove("breadcrumbs");
		report.Event.Metadata.Clear();
		foreach (Bugsnag.Payload.Exception exception in report.Event.Exceptions)
		{
			exception["message"] = AppLogger.Scrub(exception.ErrorMessage);
			foreach (StackTraceLine line in exception.StackTrace)
				if (line.FileName is { } file)
					line.FileName = AppLogger.Scrub(file);
		}
	}
}
