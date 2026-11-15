using System.Globalization;
using System.Reflection;

namespace Telemavix.Core;

/// <summary>
///     When (and from which commit) this build was made. The project stamps the assembly at compile time (BuildDate; the release
///     workflow passes BUILD_DATE and GITHUB_SHA for Commit). A file time is not used: on Android the assembly has no file.
///     Telemavix GPS has the same class and the same project stamp, keep them in step.
/// </summary>
public static class BuildInfo
{
	private static readonly Dictionary<string, string?> Metadata = typeof(BuildInfo).Assembly
		.GetCustomAttributes<AssemblyMetadataAttribute>()
		.ToDictionary(a => a.Key, a => a.Value);

	public static DateTime? BuiltAtUtc =>
		Metadata.TryGetValue("BuildDate", out string? stamp) &&
		DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime date)
			? date
			: null;

	public static string? Commit => Metadata.GetValueOrDefault("Commit");

	/// <summary>The date in the local time zone, with the commit when the build has one: "2026-10-23 14:05 (a1b2c3d)".</summary>
	public static string Describe()
	{
		string date = BuiltAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";
		return Commit is { Length: > 0 } commit ? $"{date} ({commit})" : date;
	}
}
