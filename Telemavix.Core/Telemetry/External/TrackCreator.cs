using Telemavix.Tracks;

namespace Telemavix.Core.Telemetry.External;

/// <summary>
///     The program a track file says it was written by: Telemavix GPS (the phone app) is told by its developer data in a FIT and by its creator in a GPX, any other
///     GPX by the creator attribute as it stands. A FIT of another program has none (its file_creator names a device's firmware, not a program).
/// </summary>
public sealed record TrackCreator(string Name, string? Version = null)
{
	/// <summary>False for a version that says nothing (0.0 in a GPX, 0.0.0 in a FIT: a build with no version of its own).</summary>
	public bool HasVersion => System.Version.TryParse(Version, out Version? parsed) && (parsed.Major > 0 || parsed.Minor > 0 || Math.Max(parsed.Build, 0) > 0);

	public bool IsTelemavixGps => string.Equals(Name, TrackProducer.ApplicationName, StringComparison.OrdinalIgnoreCase);

	/// <summary>The creator attribute of a GPX ("Telemavix GPS 0.5.2", "Garmin Connect"): the name, and the version when the text ends with one. Null when blank.</summary>
	public static TrackCreator? FromGpx(string? creator)
	{
		if (string.IsNullOrWhiteSpace(creator)) return null;

		creator = creator.Trim();
		int space = creator.LastIndexOf(' ');
		return space > 0 && creator[(space + 1)..].Contains('.') && System.Version.TryParse(creator[(space + 1)..], out _)
			? new TrackCreator(creator[..space].TrimEnd(), creator[(space + 1)..])
			: new TrackCreator(creator);
	}
}
