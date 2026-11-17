namespace Telemavix.Tracks;

/// <summary>
///     The program that wrote a track, named the way Garmin names itself in a file: FIT carries it as file_creator, as the version of device_info and of the developer data
///     application, GPX as the creator attribute. Readers and import sites show it as "recorded with".
/// </summary>
/// <remarks>Build is what tells one build of the app from another (the version alone is the same for all of them); Device is the phone's model and SystemInfo what it says about itself
/// (Android version, power saving), so a file explains how it came out. Each is optional and left out of the file when null or blank.</remarks>
public sealed record TrackProducer(string Name, Version Version, string? Build = null, string? Device = null, string? SystemInfo = null)
{
	/// <summary>Sizes of the FIT text fields the build, the phone model and the system details go into (bytes with the closing zero); a longer text is cut.</summary>
	internal const int BuildSize = 48, DeviceSize = 48, SystemSize = 255;

	/// <summary>What the app calls itself: the name in GPX's creator and the one readers recognise a Telemavix GPS track by.</summary>
	public const string ApplicationName = "Telemavix GPS";

	public static TrackProducer Default { get; } = new(ApplicationName, new Version(0, 0));

	/// <summary>FIT software_version: major and minor in hundredths, so 27.18 is 2718 (what Garmin devices write).</summary>
	internal ushort FitVersion => (ushort)(Math.Clamp(Version.Major, 0, 650) * 100 + Math.Clamp(Version.Minor, 0, 99));

	/// <summary>FIT application_version of the developer data: major, minor and build as one decimal number (0.5.2 is 502).</summary>
	internal uint ApplicationVersion => (uint)(Math.Clamp(Version.Major, 0, 9999) * 10000 + Math.Clamp(Version.Minor, 0, 99) * 100 + Math.Clamp(Version.Build, 0, 99));

	/// <summary>The version a FIT application_version stands for (ApplicationVersion the other way round).</summary>
	public static Version DecodeApplicationVersion(uint applicationVersion) => new((int)(applicationVersion / 10000), (int)(applicationVersion / 100 % 100), (int)(applicationVersion % 100));

	/// <summary>The creator attribute of a GPX file: the name and the version.</summary>
	internal string Creator => $"{Name} {Version.ToString(Version.Build >= 0 ? 3 : 2)}";
}
