using Avalonia.Controls;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;

namespace Telemavix.Gui;

internal static class MapKeyNotice
{
	private static readonly HashSet<string> Told = new(StringComparer.Ordinal);

	/// <summary>One notice per rejected key and run, whoever finds out first (startup, the picker, the welcome window). A null key always shows it.</summary>
	public static Task ShowAsync(Window owner, string? key)
	{
		if (key is not null && !Told.Add(key.Trim())) return Task.CompletedTask;

		return ConfirmDialog.ShowAsync(owner, Strings.MapKeyNotice_Title, Strings.MapKeyNotice_Message, kind: DialogKind.Warning);
	}

	/// <summary>The stored Esri key, asked about once per run (MapKeyCheck); the notice only if Esri says it doesn't know it.</summary>
	public static async Task CheckStoredAsync(Window owner)
	{
		string? key = OverlaySettingsStore.Load().MapApiKeys?.GetValueOrDefault(MapProviders.EsriKeyGroup)?.Trim();
		if (string.IsNullOrEmpty(key) || await MapKeyCheck.CheckEsriAsync(key) != MapKeyVerdict.Rejected) return;

		await ShowAsync(owner, key);
	}
}
