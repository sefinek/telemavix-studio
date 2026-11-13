using Telemavix.Core.Logging;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;
using Telemavix.Core.Telemetry;

namespace Telemavix.Core.Preview;

/// <summary>A file the comparison couldn't open, and why.</summary>
public sealed record CompareFailure(string Path, string Reason);

/// <summary>
///     Several exported videos played side by side against one clock (CompareClock). Each tile has a decoder of its own
///     and decodes just ahead of the clock, skipping frames it can't keep up with; the display takes the frame due for
///     every tile at each refresh (CompareFrameFeed), so all pictures change on the same refresh. Play starts only once
///     every tile has its first frame, so they begin together. The sound is not played.
///     Public members are called from the UI thread; StillReady, Ended and TileFailed can come from any thread.
/// </summary>
public sealed class ComparePlayer : IDisposable
{
	// Frames a tile decodes ahead of the clock - enough to ride out a slow frame, few enough to stay at the clock's side.
	private const int MaxQueuedFrames = 4;

	// A tile this far behind the clock decodes past frames without converting them instead of showing each one late.
	private const double LateSeconds = 0.05;

	private readonly CompareClock _clock;
	private readonly List<CompareTile> _tiles;
	private readonly SemaphoreSlim _operation = new(1, 1);
	private CancellationTokenSource _run = new();
	private CancellationTokenSource _seek = new();
	private List<Task> _workers = [];
	private int _generation;
	private volatile bool _playing;
	private volatile bool _loop;
	private readonly CancellationTokenSource _maps = new();
	private bool _disposed;

	private ComparePlayer(CompareClock clock, List<CompareTile> tiles)
	{
		_clock = clock;
		_tiles = tiles;
		Duration = TimeSpan.FromSeconds(tiles.Max(t => t.Duration.TotalSeconds));
	}

	public IReadOnlyList<CompareTile> Tiles => _tiles;

	/// <summary>The longest video's - a shorter one holds its last frame.</summary>
	public TimeSpan Duration { get; }

	public bool IsPlaying => _playing;

	/// <summary>Where the comparison is, on the videos' common timeline.</summary>
	public TimeSpan Position => TimeSpan.FromSeconds(Math.Clamp(_clock.Position, 0, Duration.TotalSeconds));

	public double Rate
	{
		get => _clock.Rate;
		set => _clock.Rate = value;
	}

	/// <summary>At the end the comparison starts again from the beginning instead of stopping.</summary>
	public bool Loop
	{
		get => _loop;
		set => _loop = value;
	}

	/// <summary>A tile's picture at a standstill (a seek, a pause) - lent for the call: copy it, don't keep it.</summary>
	public event Action<int, ComposedPreviewFrame>? StillReady;

	/// <summary>Playback (re)started: from Play, from a seek while playing, or round a loop - the display attaches the tiles' frames again.</summary>
	public event Action? PlaybackStarted;

	/// <summary>Playback reached the end (and did not loop).</summary>
	public event Action? Ended;

	/// <summary>A tile's decoder failed - its Error says why; it keeps its last picture.</summary>
	public event Action<int>? TileFailed;

	/// <summary>
	///     Opens every file at the width its tile needs; the ones that can't be opened come back as failures and are left out.
	///     Player is null when none could.
	/// </summary>
	public static async Task<(ComparePlayer? Player, IReadOnlyList<CompareFailure> Failures)> OpenAsync(IReadOnlyList<string> paths, int tileWidth,
		CancellationToken ct = default)
	{
		var clock = new CompareClock();
		(string Path, CompareTile? Tile, string? Error)[] opened = await Task.WhenAll(paths.Select(path => Task.Run<(string, CompareTile?, string?)>(() =>
		{
			try
			{
				return (path, CompareTile.Open(path, tileWidth, () => clock.Position), null);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return (path, null, ex.Message);
			}
		}, ct)));

		List<CompareTile> tiles = [.. opened.Where(o => o.Tile is not null).Select(o => o.Tile!)];
		List<CompareFailure> failures = [.. opened.Where(o => o.Tile is null).Select(o => new CompareFailure(o.Path, o.Error!))];
		if (ct.IsCancellationRequested)
		{
			foreach (CompareTile tile in tiles) tile.Dispose();
			ct.ThrowIfCancellationRequested();
		}

		return (tiles.Count == 0 ? null : new ComparePlayer(clock, tiles), failures);
	}

	/// <summary>
	///     One recording twice, on one clock: the overlay drawn from telemetry processed without GPS smoothing (first tile) and with it
	///     (second). The picture, layout, settings and the speed correction are the same, so smoothing is the only difference.
	///     Error is why the recording can't be compared; both null when it has no GPS fix.
	/// </summary>
	public static async Task<(ComparePlayer? Player, string? Error)> OpenSmoothingAsync(FileSummary summary, int tileWidth, CancellationToken ct = default)
	{
		if (summary.TelemetryFrames is not { Count: > 0 } raw || !OverlayAvailability.Of(raw, false, summary.CameraFormat).GpsFix)
			return (null, null);

		OverlaySettings settings = OverlaySettingsStore.Load() with { ShowRouteIntro = false };
		var availability = OverlayAvailability.Of(raw, summary.ContainerRecordingStartUtc is not null, summary.CameraFormat);
		(List<OverlayPreset> presets, string activeId) = OverlayPresetStore.Load();
		IReadOnlyList<OverlayElement> layout = availability.Apply(presets.First(p => p.Id == activeId).Elements);

		var clock = new CompareClock();
		var tiles = new List<CompareTile>(2);
		try
		{
			foreach (bool smoothed in new[] { false, true })
			{
				IReadOnlyList<DerivedFrame> frames = await Task.Run(() =>
					TelemetryProcessor.Process(raw, summary.CameraFormat, smoothed, settings.SpeedCorrectionPercent, settings.OrientationSource), ct);
				tiles.Add(await Task.Run(() => CompareTile.OpenRecording(summary, smoothed, frames, layout, availability, settings, tileWidth, () => clock.Position), ct));
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			foreach (CompareTile tile in tiles) tile.Dispose();
			return (null, ex.Message);
		}
		catch
		{
			foreach (CompareTile tile in tiles) tile.Dispose();
			throw;
		}

		var player = new ComparePlayer(clock, tiles);
		bool mapShown = layout.Any(e => e is MapWidgetElement { Visible: true });
		for (int i = 0; i < tiles.Count; i++) _ = player.PrepareMapAsync(i, mapShown);
		return (player, null);
	}

	/// <summary>The Map widget's tiles for a recording's tile, fetched in the background like the preview does - the picture is shown meanwhile.</summary>
	private async Task PrepareMapAsync(int index, bool mapShown)
	{
		if (!mapShown || _tiles[index].Compositor is not { } compositor) return;

		CancellationToken ct = _maps.Token;
		try
		{
			OverlayRenderer renderer = compositor.Renderer;
			if (!renderer.NeedsMapPrepare(renderer.Layout)) return;

			(MapMosaicSet? mosaics, MapAssignments? key) = await renderer.BuildMapMosaicAsync((_, _) => { }, ct);
			if (ct.IsCancellationRequested)
			{
				mosaics?.Dispose();
				return;
			}

			ComposedPreviewFrame? still = compositor.Change(r => r.ApplyMapMosaic(mosaics, key));
			if (still is null) return;

			try
			{
				if (!_playing && !ct.IsCancellationRequested) StillReady?.Invoke(index, still);
			}
			finally
			{
				PlaybackFrameSource.Release(still);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			AppLogger.Warn(ex, "The map of the smoothing comparison could not be prepared.");
		}
	}

	/// <summary>Every tile starts together from the current position (from the beginning when it is at the end).</summary>
	public async Task PlayAsync()
	{
		await _operation.WaitAsync();
		try
		{
			if (_disposed || _playing) return;

			await StartLockedAsync();
		}
		finally
		{
			_operation.Release();
		}
	}

	/// <summary>Stops at the picture now due, and shows each tile's exact frame for it.</summary>
	public async Task PauseAsync()
	{
		await _operation.WaitAsync();
		try
		{
			if (_disposed || !_playing) return;

			await StopLockedAsync();
		}
		finally
		{
			_operation.Release();
		}

		await ShowStillsAsync(_clock.Position, SeekAccuracy.Exact, _seek.Token);
	}

	/// <summary>
	///     Moves every tile to a position. Playing carries on from there; paused shows the frames at a standstill. A newer
	///     seek takes over from one still decoding, and one that was superseded before its turn does nothing at all.
	/// </summary>
	public async Task SeekAsync(TimeSpan position, SeekAccuracy accuracy)
	{
		double seconds = Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds);
		var mine = new CancellationTokenSource();
		CancellationToken ct = mine.Token;
		CancellationTokenSource previous = Interlocked.Exchange(ref _seek, mine);
		await previous.CancelAsync();

		bool resume;
		await _operation.WaitAsync();
		try
		{
			if (_disposed || ct.IsCancellationRequested) return;

			resume = _playing;
			if (resume) await StopLockedAsync();
			_clock.Seek(seconds);
			if (resume) await StartLockedAsync();
		}
		finally
		{
			_operation.Release();
		}

		if (!resume) await ShowStillsAsync(seconds, accuracy, ct);
	}

	/// <summary>One frame forward or back, paused - the frame step of the first tile (the others follow its time).</summary>
	public async Task StepAsync(int frames)
	{
		if (_playing) await PauseAsync();

		double fps = _tiles[0].Fps;
		long index = Math.Max(0, PreviewFrames.IndexAt(_clock.Position, fps) + frames);
		// A position a quarter frame before its frame, as the preview's frame stepping sets it (PreviewFrames.IndexAt).
		await SeekAsync(TimeSpan.FromSeconds(Math.Max(0, (index - 0.25) / fps)), SeekAccuracy.Exact);
	}

	public void Dispose()
	{
		if (_disposed) return;

		_disposed = true;
		_playing = false;
		_seek.Cancel();
		_run.Cancel();
		_maps.Cancel();
		try
		{
			Task.WhenAll(_workers).Wait(TimeSpan.FromSeconds(2));
		}
		catch (AggregateException)
		{
		}

		foreach (CompareTile tile in _tiles) tile.Dispose();
		_run.Dispose();
		_seek.Dispose();
		_maps.Dispose();
	}

	private async Task StartLockedAsync()
	{
		double start = _clock.Position;
		if (start >= Duration.TotalSeconds - 1e-3) start = 0;
		_clock.Seek(start);

		_run = new CancellationTokenSource();
		CancellationToken ct = _run.Token;
		int generation = ++_generation;

		// Every tile's first frame before the clock moves: they start on the same frame whatever each decoder's start-up costs.
		var streams = new LibavVideoSource.PlaybackStream?[_tiles.Count];
		VideoFrame?[] first = await Task.WhenAll(_tiles.Select((tile, i) => Task.Run(() =>
		{
			tile.Frames.Clear();
			tile.Compositor?.ClearStill();
			tile.Error = null;
			try
			{
				streams[i] = tile.Video.OpenPlaybackStream(TimeSpan.FromSeconds(start), ct);
				return streams[i]!.TryReadNextFrame();
			}
			catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
			{
				Fail(i, ex.Message);
				return null;
			}
		}, ct)));

		for (int i = 0; i < _tiles.Count; i++)
		{
			if (first[i] is { } frame && streams[i] is { } stream && _tiles[i].Compose(frame, stream.Position) is { } composed)
				_tiles[i].Frames.Enqueue(composed);
			_tiles[i].Frames.SetPlaying(true);
		}

		_playing = true;
		_clock.Start(start);
		_workers =
		[
			.. _tiles.Select((tile, i) => streams[i] is { } stream
				? Task.Factory.StartNew(() => Decode(i, stream, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
				: Task.CompletedTask)
		];
		_ = Task.Run(() => WatchEndAsync(generation, ct), ct);
		PlaybackStarted?.Invoke();
	}

	/// <summary>Stops the decoders and the clock; the frames waiting go. Under the operation lock.</summary>
	private async Task StopLockedAsync()
	{
		_playing = false;
		await _run.CancelAsync();
		await Task.WhenAll(_workers.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default)));
		_workers = [];
		_clock.Pause();
		foreach (CompareTile tile in _tiles)
		{
			tile.Frames.SetPlaying(false);
			tile.Frames.Clear();
		}

		_generation++;
	}

	private void Decode(int index, LibavVideoSource.PlaybackStream stream, CancellationToken ct)
	{
		CompareTile tile = _tiles[index];
		try
		{
			while (!ct.IsCancellationRequested)
			{
				if (tile.Frames.Count >= MaxQueuedFrames)
				{
					ct.WaitHandle.WaitOne(2);
					continue;
				}

				double behind = _clock.Position - stream.NextPosition.TotalSeconds;
				if (behind > LateSeconds && !stream.Skip((int)(behind * tile.Fps))) return;

				if (stream.TryReadNextFrame() is not { } frame)
				{
					if (stream.Error is { } error) Fail(index, error);
					return;
				}

				if (tile.Compose(frame, stream.Position) is not { } composed) return;

				tile.Frames.Enqueue(composed);
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
		{
			Fail(index, ex.Message);
		}
	}

	private void Fail(int index, string reason)
	{
		_tiles[index].Error = reason;
		TileFailed?.Invoke(index);
	}

	/// <summary>The clock reaching the end of the longest video: stop there, or start over.</summary>
	private async Task WatchEndAsync(int generation, CancellationToken ct)
	{
		try
		{
			while (_clock.Position < Duration.TotalSeconds)
				await Task.Delay(10, ct);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		bool loop;
		await _operation.WaitAsync();
		try
		{
			if (_disposed || generation != _generation || !_playing) return;

			await StopLockedAsync();
			loop = _loop;
			_clock.Seek(loop ? 0 : Duration.TotalSeconds);
			if (loop) await StartLockedAsync();
		}
		finally
		{
			_operation.Release();
		}

		if (loop) return;

		await ShowStillsAsync(Duration.TotalSeconds, SeekAccuracy.Exact, _seek.Token);
		Ended?.Invoke();
	}

	/// <summary>Every tile's picture at a position, decoded together - latest seek wins.</summary>
	private async Task ShowStillsAsync(double seconds, SeekAccuracy accuracy, CancellationToken ct)
	{
		try
		{
			await Task.WhenAll(_tiles.Select((tile, i) => Task.Run(() =>
			{
				VideoFrame? frame;
				try
				{
					frame = tile.Video.GetFrame(TimeSpan.FromSeconds(seconds), accuracy, ct);
				}
				catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
				{
					Fail(i, ex.Message);
					return;
				}

				if (frame is null) return;

				if (tile.Compositor is { } compositor)
				{
					// The compositor keeps the clean frame for a redraw (a map arriving) and draws into a copy.
					ComposedPreviewFrame? composed = compositor.ShowStill(frame, TimeSpan.FromSeconds(PreviewFrames.IndexAt(seconds, tile.Fps) / tile.Fps));
					if (composed is null) return;

					try
					{
						if (!ct.IsCancellationRequested) StillReady?.Invoke(i, composed);
					}
					finally
					{
						PlaybackFrameSource.Release(composed);
					}

					return;
				}

				try
				{
					var still = new ComposedPreviewFrame(TimeSpan.FromSeconds(PreviewFrames.IndexAt(seconds, tile.Fps) / tile.Fps), frame.Bgra,
						frame.Width, frame.Height);
					if (!ct.IsCancellationRequested) StillReady?.Invoke(i, still);
				}
				finally
				{
					tile.Video.Recycle(frame);
				}
			}, ct)));
		}
		catch (OperationCanceledException)
		{
		}
	}
}
