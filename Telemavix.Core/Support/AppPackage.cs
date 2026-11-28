using System.Runtime.InteropServices;

namespace Telemavix.Core;

/// <summary>
///     Whether the app runs from an MSIX package - the Microsoft Store copy (Telemavix.Build/Store). The Store installs and
///     updates that copy, its folder is read-only, its registry writes stay inside the package and .ovproj is associated by
///     the package's manifest.
/// </summary>
public static partial class AppPackage
{
	// Partner Center's Store ID.
	public const string StorePageUrl = "https://apps.microsoft.com/detail/9NVTZXHRTNKD";

	private const int NoPackageError = 15700;

	public static bool IsPackaged { get; } = DetectPackage();

	private static bool DetectPackage()
	{
		if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return false;

		uint length = 0;
		return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackageError;
	}

	[LibraryImport("kernel32.dll")]
	private static partial int GetCurrentPackageFullName(ref uint length, IntPtr fullName);
}
