using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Telemavix.Core;
using Telemavix.Core.Dependencies;
using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Localization;
using Telemavix.Core.Logging;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;
using Telemavix.Core.Preview;
using Telemavix.Core.Telemetry.External;
using Telemavix.Core.Updates;
using Telemavix.Gui.Native;

namespace Telemavix.Gui;

/// <summary>
///     Ctrl+Shift+D in the main window: every dialog and notification with sample text, to check how they look. Nothing here
///     changes settings or files - the welcome window opened from here doesn't save either (it's the real one, so Finish does).
/// </summary>
public partial class DeveloperWindow : Window
{
	private const string SampleFile = "DJI_20260101_0001_D_overlay.MP4";
	private const string SampleError = "The process cannot access the file because it is being used by another process.";

	private static readonly Dictionary<string, Func<DeveloperWindow, Task<object?>>> Dialogs = new()
	{
		["new-project"] = w => w.Ask(Strings.Project_NewTitle, Strings.Project_NewMessage, Strings.Main_NewProject, DialogKind.Warning),
		["project-overlay"] = w => w.Ask(Strings.Project_OverlayTitle, "Preset \"Default\" has changed since the project was saved.\n" + Strings.Project_UseOverlayChanged,
			Strings.Project_UseProjects, DialogKind.Info),
		["project-save-failed"] = w => w.Notice(Strings.Project_SaveFailedTitle, SampleError, DialogKind.Warning),
		["project-open-failed"] = w => w.Notice(Strings.Project_OpenFailedTitle, SampleError, DialogKind.Warning),
		["close-rendering"] = w => w.Ask(Strings.Close_RenderInProgressTitle, Strings.Close_RenderInProgressMessage, Strings.Close_CloseAndCancel, DialogKind.Warning),
		["render-busy"] = w => w.Notice(Strings.Queue_Title, Strings.Queue_Busy, DialogKind.Warning),
		["queue-busy"] = w => w.Notice(Strings.Queue_Title, Strings.Queue_RenderBusy, DialogKind.Warning),
		["queue-finished"] = w => w.Notice(Strings.Queue_FinishedTitle, string.Format(Strings.Queue_FinishedMessage, 3, 0), DialogKind.Success),
		["queue-finished-partly"] = w => w.Notice(Strings.Queue_FinishedTitle, string.Format(Strings.Queue_FinishedMessage, 2, 1), DialogKind.Warning),
		["queue-recordings"] = async w => await ConfirmDialog.AskOrSkipAsync(w, Strings.Queue_RecordingsTitle, string.Format(Strings.Queue_RecordingsMessage, 3),
			Strings.Queue_RecordingsSeparate, Strings.Queue_RecordingsJoined),
		["telemetry-export-failed"] = w => w.Notice(Strings.Telemetry_ExportFailedTitle, SampleError, DialogKind.Warning),
		["track-failed"] = w => w.Notice(Strings.Track_Title, string.Format(Strings.TrackOnly_Failed, SampleError), DialogKind.Warning),
		["music-cant-use"] = w => w.Notice(Strings.Music_CantUseTitle, string.Format(Strings.Music_CantUse, "song.mp3", SampleError), DialogKind.Warning),
		["preset-delete"] = w => w.Ask(Strings.Presets_DeleteTitle, string.Format(Strings.Presets_DeleteMessage, "My layout"), Strings.Main_Delete, DialogKind.Danger),
		["preset-reset"] = w => w.Ask(Strings.Presets_ResetTitle, string.Format(Strings.Presets_ResetMessage, "My layout"), Strings.Presets_Reset, DialogKind.Danger),
		["layer-delete"] = w => w.Ask(Strings.Layers_Delete, Plural.Format(Strings.Layers_DeleteMessage, 3), Strings.Main_Delete, DialogKind.Danger),
		["update-available"] = async w => await ConfirmDialog.AskOrSkipAsync(w, Strings.Main_UpdateAvailableTitle,
			string.Format(Strings.Main_UpdateAvailableInstall, "9.9.9", AppUpdates.CurrentVersionLabel), Strings.Main_Update, Strings.Main_SkipUpdate, DialogKind.Info),
		["update-available-download"] = async w => await ConfirmDialog.AskOrSkipAsync(w, Strings.Main_UpdateAvailableTitle,
			string.Format(Strings.Main_UpdateAvailableDownload, "9.9.9", AppUpdates.CurrentVersionLabel), Strings.Main_OpenGitHub, Strings.Main_SkipUpdate, DialogKind.Info),
		["update-confirm"] = w => w.Ask(Strings.AppUpdate_Title, string.Format(Strings.AppUpdate_Confirm, "9.9.9"), Strings.Main_Update, DialogKind.Info),
		["update-rendering"] = w => w.Notice(Strings.AppUpdate_Title, Strings.AppUpdate_WhileRendering, DialogKind.Warning),
		["update-failed"] = w => w.Notice(Strings.AppUpdate_Failed, SampleError, DialogKind.Danger),
		["remote-access"] = w => w.Notice(Strings.RemoteAccess_Title, string.Format(Strings.RemoteAccess_Message, AppUpdates.CurrentVersionLabel, "9.9.9"), DialogKind.Danger),
		["deps-update"] = w => w.Ask(string.Format(Strings.Deps_UpdateTitle, "FFmpeg"), string.Format(Strings.Deps_UpdateAfterRestart, "FFmpeg", "9.0.1"),
			Strings.Deps_CloseAndUpdate, DialogKind.Warning),
		["deps-rendering"] = w => w.Notice(string.Format(Strings.Deps_UpdateTitle, "FFmpeg"), string.Format(Strings.Deps_UpdateWhileRendering, "FFmpeg"), DialogKind.Warning),
		["test-recording"] = w => w.Ask(Strings.Tools_TestRecording, Strings.Tools_ReplaceRecording, Strings.Tools_LoadTestRecording, DialogKind.Neutral),
		["test-recording-rendering"] = w => w.Notice(Strings.Tools_TestRecording, Strings.Benchmark_WhileRendering, DialogKind.Warning),
		["tools-fix-ask"] = w => w.Ask(Strings.Tools_FixAskTitle, string.Format(Strings.Tools_FixAskMessage, SampleFile, "bt709"), Strings.Tools_Fix, DialogKind.Warning,
			Strings.Tools_FixColorTags),
		["tools-fix-nothing"] = w => w.Notice(Strings.Tools_NothingToDoTitle, string.Format(Strings.Tools_NothingToDoMessage, SampleFile, "bt709"), DialogKind.Success,
			Strings.Tools_FixColorTags),
		["tools-fix-done"] = w => w.Notice(Strings.Tools_FixedTitle, string.Format(Strings.Tools_FixedMessage, SampleFile, "no tags (tv)", "bt709 (tv)", "DJI_fixed.MP4"),
			DialogKind.Success, Strings.Tools_FixColorTags),
		["tools-fix-failed"] = w => w.Notice(Strings.Tools_FixFailedTitle, string.Format(Strings.Tools_FixFailedMessage, SampleFile, SampleError), DialogKind.Danger,
			Strings.Tools_FixColorTags),
		["tools-metadata-done"] = w => w.Notice(Strings.Tools_MetadataRemovedTitle, string.Format(Strings.Tools_MetadataRemovedMessage, "DJI_clean.MP4", "GPS, serial number"),
			DialogKind.Success, Strings.Tools_RemoveMetadata),
		["tools-metadata-failed"] = w => w.Notice(Strings.Tools_StripFailedTitle, string.Format(Strings.Tools_StripFailedMessage, SampleError), DialogKind.Danger,
			Strings.Tools_RemoveMetadata),
		["tools-audio-replace"] = w => w.Ask(Strings.Tools_ReplaceExistingTitle, string.Format(Strings.Tools_ReplaceExistingMessage, "    DJI_0001.wav"), Strings.Tools_Replace,
			DialogKind.Warning, Strings.Tools_CameraMicrophoneAudio),
		["tools-audio-done"] = w => w.Notice(Strings.Tools_ConvertedTitle, string.Format(Strings.Tools_ConvertedMessage, "    DJI_0001.wav"), DialogKind.Success,
			Strings.Tools_CameraMicrophoneAudio),
		["tools-audio-failed"] = w => w.Notice(Strings.Tools_ConversionFailedTitle, string.Format(Strings.Tools_ConversionFailedMessage, SampleError), DialogKind.Danger,
			Strings.Tools_CameraMicrophoneAudio),
		["compare-limit"] = w => w.Notice(Strings.Compare_LimitTitle, string.Format(Strings.Compare_LimitMessage, 6), DialogKind.Info, Strings.Common_CompareVideos),
		["compare-open-failed"] = w => w.Notice(Strings.ComparePlay_OpenFailedTitle, string.Format(Strings.ComparePlay_OpenFailed, "DJI_0001.MP4: " + SampleError),
			DialogKind.Warning, Strings.ComparePlay_Title)
	};

	private readonly MainWindow? _main;

	public DeveloperWindow()
	{
		InitializeComponent();
	}

	public DeveloperWindow(MainWindow main) : this()
	{
		_main = main;
	}

	private async void OnActionClick(object? sender, RoutedEventArgs e)
	{
		if ((sender as Control)?.Tag is not string action) return;

		if (Dialogs.TryGetValue(action, out Func<DeveloperWindow, Task<object?>>? dialog))
		{
			object? answer = await dialog(this);
			SetStatus(answer is null ? $"Shown: {action}" : $"Shown: {action} - returned {answer}");
			return;
		}

		string[] parts = action.Split(':', 2);
		string argument = parts.Length > 1 ? parts[1] : "";
		object? result = null;
		switch (parts[0])
		{
			case "config-preset-picker":
				List<SettingsPreset> samples = SettingsPresetStore.Load().Presets;
				result = await SettingsPresetPickerWindow.PickAsync(this, Strings.SettingsPresets_PickNew, samples, samples[0].Id);
				break;
			case "track-format":
				result = await TrackFormatWindow.AskAsync(this, TrackOnlyFormat.Default);
				break;
			case "export-notice":
				await ExportNotice.AskAsync(this, ExportMaps.Used([new MapWidgetElement { X = 0, Y = 0, MapProviderId = "thunderforest-cycle" }, new MapWidgetElement { X = 0, Y = 0, MapProviderId = MapProviders.CustomId }], OverlaySettingsStore.Load()));
				break;
			case "dji-api-notice":
				await DjiApiNotice.ShowAsync(this);
				break;
			case "render-done" when _main is not null:
				await _main.NotifyRenderFinishedAsync(Strings.Render_CompleteTitle, SampleRenderDone(), DialogKind.Success,
					Path.Combine(Path.GetTempPath(), SampleFile));
				break;
			case "render-failed" when _main is not null:
				await _main.NotifyRenderFinishedAsync(Strings.Render_FailedTitle, "ffmpeg exited with code 1: " + SampleError, DialogKind.Danger);
				break;
			case "disk-space" when _main is not null:
				result = await _main.AskLowDiskSpaceAsync(Path.Combine(Path.GetTempPath(), "render.mp4"), 4_200_000_000, 8_400_000_000,
					3_100_000_000);
				break;
			case "no-telemetry" when _main is not null:
				await _main.ShowNoTelemetryNoticeAsync();
				break;
			case "busy" when _main is not null:
				result = await _main.AskComputerBusyAsync(argument == "gpu-cpu" ? new BenchmarkLoad(0.82, 0.12, 0, 0) : new BenchmarkLoad(0.63, 0.97, 0.42, 2),
					argument is "gpu" or "gpu-cpu");
				break;
			case "unsaved":
				result = await ConfirmDialog.AskOrSkipAsync(this, Strings.Unsaved_Title, Strings.Unsaved_Message, Strings.Unsaved_Save, Strings.Unsaved_Discard,
					DialogKind.Warning);
				break;
			case "queue" when _main is not null:
				_main.OpenQueueWindow();
				break;
			case "no-gps" when _main is not null:
				await _main.ShowNoGpsNoticeAsync();
				break;
			case "balloon":
				BalloonNotifier.Show(this, Strings.Render_CompleteTitle, SampleRenderDone());
				break;
			case "balloon-later":
				SetStatus("The balloon shows in 3 s - switch to another window.");
				await Task.Delay(3000);
				BalloonNotifier.Show(this, Strings.Render_CompleteTitle, SampleRenderDone());
				break;
			case "sound":
				AppSound.Play(Enum.Parse<UiSound>(argument));
				break;
			case "log":
				if (argument == "warn") AppLogger.Warn("Sample warning from the developer tools");
				else if (argument == "error") AppLogger.Error("Sample error from the developer tools");
				else AppLogger.Notify("Sample message from the developer tools");
				break;
			case "taskbar" when _main is not null:
				TaskbarProgress.State state = Enum.Parse<TaskbarProgress.State>(argument);
				TaskbarProgress.SetState(_main, state);
				if (state is TaskbarProgress.State.Normal or TaskbarProgress.State.Paused or TaskbarProgress.State.Error)
					TaskbarProgress.SetValue(_main, 40, 100);
				break;
			case "welcome":
				await new WelcomeWindow(true).ShowDialog(this);
				break;
			case "dependencies":
				await new DependencyPromptWindow(RequiredTools.All).ShowDialog(this);
				break;
			case "external-track":
				await new ExternalTrackWindow().ShowDialog(this);
				break;
			case "benchmark":
				var benchmark = new BenchmarkWindow();
				var source = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv", 73_000_000);
				benchmark.ShowSample(new BenchmarkResult(
						new BenchmarkSystem("Microsoft Windows 10.0.26200", "Intel(R) Core(TM) i9-14900HX", 32, 32L << 30,
							["NVIDIA GeForce RTX 4070 Laptop GPU (32.0.15.7714)"], "9.0.2", "1.0.0"),
						new BenchmarkLoad(0.31, 0.64), source, source,
						new BenchmarkStat(142, 139, 147, 3), new BenchmarkStat(118, 112, 121, 3),
						new FrameTimes(16.4, 19.8), new FrameTimes(4.4, 5.1), 1920, 1080, new FrameTimes(4.4, 5.6), new FrameTimes(2.0, 2.4),
						new BenchmarkStat(59, 57, 60, 2), new BenchmarkStat(36, 35, 37, 2),
						[
							new EncoderSpeed("hevc_nvenc", "p1", new BenchmarkStat(121, 118, 124, 2), false),
							new EncoderSpeed("hevc_nvenc", "p4", new BenchmarkStat(114, 99, 120, 2), false),
							new EncoderSpeed("hevc_nvenc", "p7", new BenchmarkStat(46, 45, 47, 2), false),
							new EncoderSpeed("libx265", null, new BenchmarkStat(5, 5, 5, 2), false)
						],
						new BenchmarkStat(31.5, 30.9, 32.0, 2), new BenchmarkStat(26.0, 25.1, 26.4, 2), TimeSpan.FromSeconds(212),
						new SourceSpeeds(source, new BenchmarkStat(131, 124, 138, 3), new BenchmarkStat(41, 40, 43, 3), new BenchmarkStat(29.2, 28.4, 30.1, 2),
							new BenchmarkStat(24.1, 23.8, 24.5, 2))),
					[
						(Strings.Benchmark_PreviewInWindow, SamplePreview(Strings.Benchmark_TestRecording, Strings.Benchmark_ModeTestWindow, 1120, 630, 0, 2)),
						(string.Format(Strings.Benchmark_PreviewInApp, "DJI_20261004123456_0001_D.MP4"),
							SamplePreview("DJI_20261004123456_0001_D.MP4", Strings.PreviewBenchmark_ModeWindow, 1664, 936, 3, 1))
					], 54_000);
				await benchmark.ShowDialog(this);
				break;
			default:
				return;
		}

		SetStatus(result is null ? $"Shown: {action}" : $"Shown: {action} - returned {result}");
	}

	private static string SampleRenderDone()
	{
		var video = new VideoInfo("hevc", "Main 10", 3840, 2160, "60000/1001", "yuv420p10le", "bt709", "bt709", "bt709", "tv", 73_000_000);
		return MainWindow.RenderDoneMessage(Path.Combine(Path.GetTempPath(), SampleFile), "00:04:12", "1.92 GB", new SourceInfo(video, null, 956, null), "hevc_nvenc");
	}

	private async Task<object?> Ask(string title, string message, string confirm, DialogKind kind, string? windowTitle = null)
	{
		return await ConfirmDialog.AskAsync(this, title, message, confirm, kind, windowTitle);
	}

	private async Task<object?> Notice(string title, string message, DialogKind kind, string? windowTitle = null)
	{
		await ConfirmDialog.ShowAsync(this, title, message, kind: kind, windowTitle: windowTitle);
		return null;
	}

	private static PreviewRun SamplePreview(string source, string mode, int shownWidth, int shownHeight, int skipped, int dropped)
	{
		var result = new PreviewMeasurementResult(20.01, 1196, 59.94, new PreviewTiming(1195, 16.68, 17.42), 41.7, 0, skipped, dropped, 2,
			new PreviewTiming(1201, 4.12, 6.85), new PreviewTiming(1199, 3.9, 5.4), new PreviewTiming(1196, 0.61, 1.12), 165.0);
		var context = new PreviewReportContext(source, mode, "DELL U2723QE (2560x1440, 165 Hz)", new PixelSize(1280, 720), shownWidth, shownHeight, 1.25,
			1, "hevc (d3d11va)", true, false);
		return new PreviewRun(result, context, [new BenchmarkLoad(0.24, 0.41, Decoder: 0.18), new BenchmarkLoad(0.27, 0.43, Decoder: 0.19)], 1_412L << 20);
	}

	private void SetStatus(string text)
	{
		StatusText.Text = text;
	}
}
