using Avalonia.Controls;

namespace Telemavix.Gui;

internal static class MapKeyNotice
{
	private static readonly HashSet<string> Told = new(StringComparer.Ordinal);

	/// <summary>One notice per rejected key and run, whoever finds out first (a map fetching its tiles, the picker, the welcome window). A null key always shows it.</summary>
	public static Task ShowAsync(Window owner, string? key)
	{
		if (key is not null && !Told.Add(key.Trim())) return Task.CompletedTask;

		return ConfirmDialog.ShowAsync(owner, Strings.MapKeyNotice_Title, Strings.MapKeyNotice_Message, kind: DialogKind.Warning);
	}
}
