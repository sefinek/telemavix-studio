namespace Telemavix.Tracks;

/// <summary>
///     The program that wrote a track, named the way Garmin names itself in a file: FIT carries it as file_creator, as the version of device_info and of the developer data
///     application, GPX as the creator attribute. Readers and import sites show it as "recorded with".
/// </summary>
public sealed record TrackProducer(string Name, Version Version)
{
	public static TrackProducer Default { get; } = new("Telemavix GPS", new Version(0, 0));

	/// <summary>FIT software_version: major and minor in hundredths, so 27.18 is 2718 (what Garmin devices write).</summary>
	internal ushort FitVersion => (ushort)(Math.Clamp(Version.Major, 0, 650) * 100 + Math.Clamp(Version.Minor, 0, 99));

	/// <summary>FIT application_version of the developer data: major, minor and build as one decimal number (0.5.2 is 502).</summary>
	internal uint ApplicationVersion => (uint)(Math.Clamp(Version.Major, 0, 9999) * 10000 + Math.Clamp(Version.Minor, 0, 99) * 100 + Math.Clamp(Version.Build, 0, 99));

	/// <summary>The creator attribute of a GPX file: the name and the version.</summary>
	internal string Creator => $"{Name} {Version.ToString(Version.Build >= 0 ? 3 : 2)}";
}
