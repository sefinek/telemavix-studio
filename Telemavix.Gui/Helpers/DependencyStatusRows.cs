using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Telemavix.Core.Dependencies;
using Telemavix.Core.Logging;

namespace Telemavix.Gui;

/// <summary>Shared "Name | installed | latest | [Update]" row layout used by SettingsWindow's About tab.</summary>
internal static class DependencyStatusRows
{
	/// <summary>The same rows before the first check is back, so the layout doesn't jump when the versions arrive.</summary>
	public static void ShowChecking(Grid grid, IReadOnlyList<ExternalTool> tools)
	{
		grid.RowDefinitions.Clear();
		grid.Children.Clear();

		for (int i = 0; i < tools.Count; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			AddCell(grid, new TextBlock { Text = tools[i].DisplayName }, i, 0);
			AddCell(grid, new TextBlock { Text = Strings.Deps_InstalledChecking, Opacity = 0.6 }, i, 1);
			AddCell(grid, new TextBlock { Text = Strings.Deps_LatestChecking, Opacity = 0.6 }, i, 2);
		}
	}

	/// <param name="isRendering">An FFmpeg update that restarts the app waits until this is false.</param>
	/// <param name="runWithConsole">Runs an install or an update with the main window's log in view (see MainWindow.RunWithConsoleAsync), so its output is where the user can see it.</param>
	public static void Populate(Window owner, Grid grid, IReadOnlyList<ToolVersionInfo> statuses, Func<bool> isRendering, Func<Func<Task>, Task> runWithConsole)
	{
		grid.RowDefinitions.Clear();
		grid.Children.Clear();

		for (int i = 0; i < statuses.Count; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			ToolVersionInfo status = statuses[i];

			AddCell(grid, new TextBlock { Text = status.Tool.DisplayName }, i, 0);
			bool upToDate = status.InstalledVersion is not null && status.InstalledVersion == status.LatestVersion && status.UnsupportedVersion is null;
			TextBlock installed = new()
			{
				Text = status.InstalledVersion is null ? Strings.Deps_NotFound : string.Format(Strings.Deps_Installed, status.InstalledVersion),
				Opacity = 0.6
			};
			if (upToDate)
			{
				AddCell(grid, installed, i, 2);
			}
			else
			{
				AddCell(grid, installed, i, 1);
				AddCell(grid, new TextBlock { Text = LatestText(status), Opacity = 0.6 }, i, 2);
			}

			if (status.InstalledVersion is null && DependencyInstaller.CanAttemptAutoInstall())
			{
				var installButton = new Button { Content = Strings.DependencyPrompt_Install };
				installButton.Click += async (_, _) => await OnInstallClickAsync(status.Tool, installButton, runWithConsole);
				AddCell(grid, installButton, i, 3);
			}
			else if (status.UpdateAvailable)
			{
				var updateButton = new Button { Content = Strings.Main_Update };
				updateButton.Click += async (_, _) => await OnUpdateClickAsync(owner, status, updateButton, isRendering, runWithConsole);
				AddCell(grid, updateButton, i, 3);
			}
		}
	}

	// The optional tools aren't asked about at startup, so a missing one is installed from here.
	private static async Task OnInstallClickAsync(ExternalTool tool, Button button, Func<Func<Task>, Task> runWithConsole)
	{
		button.IsEnabled = false;
		await runWithConsole(async () =>
		{
			AppLogger.Notify(string.Format(Strings.Welcome_InstallingTool, tool.DisplayName));

			InstallResult result;
			try
			{
				result = await DependencyInstaller.InstallAsync(tool, AppLogger.Notify, CancellationToken.None);
			}
			catch (Exception ex)
			{
				result = new InstallResult(false, ex.Message);
			}

			AppLogger.Notify(result.Message);
			button.Content = result.Success ? Strings.Deps_InstalledDone : Strings.Welcome_Retry;
			button.IsEnabled = !result.Success;
		});
	}

	private static string LatestText(ToolVersionInfo status)
	{
		return (status.LatestVersion, status.UnsupportedVersion) switch
		{
			(null, null) => Strings.Deps_LatestUnknown,
			({ } latest, null) => string.Format(Strings.Deps_Latest, latest),
			(null, { } unsupported) => string.Format(Strings.Deps_LatestUnsupported, unsupported),
			({ } latest, { } unsupported) => string.Format(Strings.Deps_LatestWithUnsupported, latest, unsupported)
		};
	}

	// AppLogger.Notify (not a local log panel) so the update's line-by-line output always lands in the same place.
	private static async Task OnUpdateClickAsync(Window owner, ToolVersionInfo status, Button button, Func<bool> isRendering, Func<Func<Task>, Task> runWithConsole)
	{
		if (DependencyInstaller.UpgradeNeedsRestart(status.Tool))
		{
			await UpdateAfterRestartAsync(owner, status, button, isRendering);
			return;
		}

		button.IsEnabled = false;
		await runWithConsole(async () =>
		{
			AppLogger.Notify(string.Format(Strings.Deps_Updating, status.Tool.DisplayName));

			InstallResult result;
			try
			{
				result = await DependencyInstaller.UpgradeAsync(status.Tool, AppLogger.Notify, CancellationToken.None);
			}
			catch (Exception ex)
			{
				result = new InstallResult(false, ex.Message);
			}

			AppLogger.Notify(result.Message);

			button.Content = result.Success ? Strings.Deps_UpdatedDone : Strings.Welcome_Retry;
			button.IsEnabled = !result.Success;
		});
	}

	/// <summary>The preview has FFmpeg's DLLs loaded, and Windows won't let winget replace them - see DependencyInstaller.UpgradeNeedsRestart.</summary>
	private static async Task UpdateAfterRestartAsync(Window owner, ToolVersionInfo status, Button button, Func<bool> isRendering)
	{
		string name = status.Tool.DisplayName;
		if (isRendering())
		{
			await ConfirmDialog.ShowAsync(owner, string.Format(Strings.Deps_UpdateTitle, name),
				string.Format(Strings.Deps_UpdateWhileRendering, name), kind: DialogKind.Warning);
			return;
		}

		bool confirmed = await ConfirmDialog.AskAsync(owner, string.Format(Strings.Deps_UpdateTitle, name),
			string.Format(Strings.Deps_UpdateAfterRestart, name, status.LatestVersion), Strings.Deps_CloseAndUpdate, DialogKind.Warning);
		if (!confirmed) return;

		button.IsEnabled = false;
		InstallResult result;
		try
		{
			result = await DependencyInstaller.StartUpgradeAfterExitAsync(status.Tool, CancellationToken.None);
		}
		catch (Exception ex)
		{
			result = new InstallResult(false, ex.Message);
		}

		AppLogger.Notify(result.Message);
		if (!result.Success)
		{
			button.Content = Strings.Welcome_Retry;
			button.IsEnabled = true;
			return;
		}

		(Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
	}

	private static void AddCell(Grid grid, Control control, int row, int column)
	{
		control.VerticalAlignment = VerticalAlignment.Center;
		Grid.SetRow(control, row);
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}
}
