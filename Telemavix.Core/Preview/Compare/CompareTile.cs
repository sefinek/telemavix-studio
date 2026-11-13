using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;
using Telemavix.Core.Reframe;
using Telemavix.Core.Telemetry;

namespace Telemavix.Core.Preview;

/// <summary>
///     One video of the comparison: its own decoder (on the GPU where there is one) at the size its tile is shown, and the
///     frames waiting for the common clock (Frames). The video is one file - an exported render, not a recording with
///     telemetry of its own - or, for the smoothing comparison, a recording whose overlay the tile draws itself (Compositor).
/// </summary>
public sealed class CompareTile : IDisposable
{
	private CompareTile(string path, SourceInfo? source, long fileSizeBytes, double fps, int width, int height, FrameBufferPool pool,
		LibavVideoSource video, CompareFrameFeed frames, ExportMarkerInfo? marker, OverlayCompositor? compositor = null, bool? smoothed = null)
	{
		Path = path;
		Source = source;
		FileSizeBytes = fileSizeBytes;
		Fps = fps;
		Width = width;
		Height = height;
		Pool = pool;
		Video = video;
		Frames = frames;
		Marker = marker;
		Compositor = compositor;
		Smoothed = smoothed;
	}

	public string Path { get; }

	/// <summary>The file as ffprobe describes it (its size, frame rate and codec) - null for a tile of the smoothing comparison.</summary>
	public SourceInfo? Source { get; }

	/// <summary>Smoothing comparison only: whether this tile's overlay is drawn from GPS-smoothed telemetry. Null for an exported video.</summary>
	public bool? Smoothed { get; }

	/// <summary>Smoothing comparison only: draws this tile's overlay onto the recording's frames.</summary>
	internal OverlayCompositor? Compositor { get; }

	/// <summary>The file's exact size in bytes.</summary>
	public long FileSizeBytes { get; }

	public double Fps { get; }

	/// <summary>The size frames are decoded at - the file's own, or narrower to fit its tile.</summary>
	public int Width { get; }

	public int Height { get; }

	public CompareFrameFeed Frames { get; }

	/// <summary>What the file says about the Telemavix Studio that rendered it - null when it says nothing (another program's file, or the tag turned off).</summary>
	public ExportMarkerInfo? Marker { get; }

	/// <summary>Why this video stopped playing early, if it did.</summary>
	public string? Error { get; internal set; }

	public TimeSpan Duration => Video.Duration;

	internal FrameBufferPool Pool { get; }

	internal LibavVideoSource Video { get; }

	/// <param name="maxWidth">The widest the picture needs decoding - a file wider than its tile is scaled down while it is decoded.</param>
	internal static CompareTile Open(string path, int maxWidth, Func<double> clockSeconds)
	{
		SourceInfo source = SourceProbe.Probe(path, false);
		(int num, int den) = FfmpegPipeline.ParseFrameRate(source.Video.FrameRate);
		double fps = num / (double)den;
		long frameCount = source.Video.FrameCount ?? (long)Math.Round(source.DurationSeconds * fps);
		if (frameCount < 1) throw new InvalidOperationException($"No frames in {path}.");

		int width = Math.Max(2, Math.Min(source.Video.Width, maxWidth) & ~1);
		int height = Math.Max(2, (int)Math.Round(source.Video.Height * (double)width / source.Video.Width) & ~1);
		var pool = new FrameBufferPool(8);
		var video = new LibavVideoSource([new PlaybackSegment(path, source.DurationSeconds, frameCount)], fps, width, height, pool, null);
		try
		{
			return new CompareTile(path, source, new FileInfo(path).Length, fps, width, height, pool, video, new CompareFrameFeed(clockSeconds), ExportMarker.Read(path));
		}
		catch
		{
			video.Dispose();
			throw;
		}
	}

	/// <summary>
	///     The recording's frames with the overlay drawn from the given telemetry. Everything but that telemetry is the same
	///     for both tiles of the comparison - the overlay's layout, settings, map and the decoded picture.
	/// </summary>
	internal static CompareTile OpenRecording(FileSummary summary, bool smoothed, IReadOnlyList<DerivedFrame> frames, IReadOnlyList<OverlayElement> layout,
		OverlayAvailability availability, OverlaySettings settings, int maxWidth, Func<double> clockSeconds)
	{
		double fps = summary.Video.Fps;
		int width = Math.Max(2, Math.Min(summary.Video.Width, maxWidth) & ~1);
		int height = Math.Max(2, (int)Math.Round(summary.Video.Height * (double)width / summary.Video.Width) & ~1);
		var pool = new FrameBufferPool(8);
		var reframer = Reframer.For(summary.Fisheye, summary.TelemetryFrames, summary.CameraFormat, new ReframeView());
		var video = new LibavVideoSource(PlaybackSegment.Of(summary), fps, width, height, pool, reframer);
		OverlayCompositor? compositor = null;
		try
		{
			compositor = new OverlayCompositor(f => new OverlayRenderer(summary.Video.Width, summary.Video.Height, f[0].Raw.AltitudeMeters, layout, f,
					TelemetryProcessor.Summarize(f).MaxSpeedKmh, false, summary.CameraModel, summary.ContainerRecordingStartUtc, MapSources.From(settings),
					RouteIntroSettings.Disabled) { DrawShadows = settings.PreviewDrawsShadows },
				frames, summary.TotalFrameCount / fps, availability, null, true, pool, settings.SpeedCorrectionPercent) { SnapshotShadows = !settings.DisableShadows };
			return new CompareTile(summary.InputPaths[0], null, 0, fps, width, height, pool, video, new CompareFrameFeed(clockSeconds), null, compositor, smoothed);
		}
		catch
		{
			compositor?.Dispose();
			video.Dispose();
			throw;
		}
	}

	/// <summary>A decoded frame ready for the display: with the overlay drawn onto it for a recording's tile. Null (frame recycled) once the tile is disposed.</summary>
	internal ComposedPreviewFrame? Compose(VideoFrame frame, TimeSpan position)
	{
		return Compositor is { } compositor
			? compositor.ComposeInPlace(frame, position)
			: new ComposedPreviewFrame(position, frame.Bgra, frame.Width, frame.Height) { Owner = Pool };
	}

	public void Dispose()
	{
		Frames.SetPlaying(false);
		Frames.Clear();
		Video.Dispose();
		Compositor?.Dispose();
	}
}
