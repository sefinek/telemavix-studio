using System.Runtime.InteropServices;
using SkiaSharp;
using Telemavix.Core.Localization;
using Telemavix.Core.Logging;
using Telemavix.Core.Mapping;
using Telemavix.Core.Overlay;

namespace Telemavix.Core.Telemetry.External;

/// <summary>
///     The picture behind a track viewed with no recording (TrackOnlyRecording): the chosen map (TrackMapSettings) over the
///     track, in memory only (Esri's terms forbid storing it). Following, its view stays on the position and is as wide as the
///     stretch around it needs (TrackCamera.Plan); else it shows the whole track. The track, the aircraft or the heading arrow for the position are drawn onto it,
///     as the picture is all there is for it to be seen on. The provider's credit is the overlay's (OverlayRenderer.BackdropMap).
///     It holds the map and the route as it was built; the frames come with each draw, so a change of smoothing or speed
///     correction reaches the marker and the camera without a new map, but not the line (a new backdrop is needed for it).
/// </summary>
public sealed class TrackBackdrop : IDisposable
{
	// Tiles around the track's own, so a view following its edge still has map in it.
	private const int FollowPaddingTiles = 3;
	// A following view has to be able to close in, so its map may be sharper than the shared budget allows (about 160 MB at most): a 23 km ride is one level
	// finer than at 300 tiles. Closing in is limited by what the map resolves.
	private const int FollowMaxTiles = 640;
	private const int MaxTrailPoints = 4000;
	// No mipmaps: Skia builds them for a raster image by the hundreds of MB and drops them from its small cache again, so every frame
	// paid for it (33 ms against 6 at 1080p). Half and quarter size copies (_levels) do the same job once.
	private static readonly SKSamplingOptions MapSampling = new(SKFilterMode.Linear, SKMipmapMode.None);
	// The levels of a following map: enough that the widest view (the closing shot) is never sampled from more than a half-size step away,
	// else the zoom out shimmers. A level is a quarter of the one before, so the deepest ones cost next to nothing.
	private const int MinFollowLevels = 3, MaxFollowLevels = 8;
	private static readonly SKColor MarkerFill = SKColor.Parse(OverlayRenderer.DefaultAccentColorHex);
	// The line is a soft dark edge, the color (a gradient from one point's speed to the next, so it fades instead of stepping) and a thin light core.
	private static readonly SKColor TrailEdge = new(0, 0, 0, 110);
	private static readonly SKColor TrailCore = new(255, 255, 255, 70);
	private const float EdgeWidthFactor = 1.55f, CoreWidthFactor = 0.3f;
	private const byte TrailPassedAlpha = 255, TrailAheadAlpha = 120;

	private readonly Lock _lock = new();
	// Drawn once per frame at 30 fps, so kept - only what depends on the picture's size is set at each draw.
	private readonly SKPaint _imagePaint = new() { IsAntialias = true };
	// The next, coarser level of the map drawn over a level in the share the zoom is between the two, so a zoom has no step where the level changes.
	private readonly SKPaint _blendPaint = new() { IsAntialias = true };
	private readonly SKPaint _trailOutline = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _trailLine = new() { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
	private readonly SKPaint _layerPaint = new();
	// The dashes of the bridges at the stroke width they were made for (the width changes with the zoom).
	private SKPathEffect? _dashEffect;
	private float _dashStroke;
	private readonly TrailMarker _marker = new();
	private readonly DroneModel _drone = new();
	private readonly RouteMapMosaic _mosaic;
	// The same map one level or more coarser and in the picture's shape, only for the closing shot of a tall route (wider than the map there is):
	// it fills what the sharp one doesn't reach, so the sides aren't black. A point on both gives how one lies on the other.
	private readonly RouteMapMosaic? _wide;
	private readonly SKImage[] _wideLevels = [];
	private readonly SKPoint _anchorOnMap, _anchorOnWide;
	private readonly float _wideScale = 1;
	// The map at full, half and quarter size, for a following view that is zoomed out (see MapSampling); only the first is the mosaic's own.
	private readonly SKImage[] _levels;
	private readonly TrackMapSettings _options;
	private readonly int _baseWidth;
	// The route as the map's pixels, its stretches (a break is where a gap starts) and when each point was; the whole of it is the
	// dim line ahead, the part up to the time asked for is drawn over it.
	private readonly SKPoint[] _trailPoints = [];
	private readonly bool[] _trailBreaks = [];
	// A break the line crosses dashed (GpsLossJoin): the stretch from the point before to this one is a bridge of its own, not part of the line.
	private readonly bool[] _trailDashed = [];
	private readonly double[] _trailTimes = [];
	// The color of the line at each point (by its smoothed speed); the stretches blend from one point's to the next's.
	private readonly SKColor[] _trailColors = [];
	private readonly SKColor _baseColor;
	private readonly SKColor[] _gradientColors = new SKColor[2];
	// All of the route - for a fixed view, where it is the same in every frame.
	private readonly TrailShape? _trail;
	// With the view fixed, the map and the line ahead are the same in every frame of a size: drawn once, copied after.
	private byte[]? _still;
	private int _stillWidth, _stillHeight;
	private IReadOnlyList<DerivedFrame>? _referenceFrames;
	private double _referenceKmh;
	private double[,]? _rotorPhases;
	// The camera's width over the whole track, planned on the first draw of a set of frames and picture shape (see TrackCamera.Plan).
	private TrackCamera.Plan? _plan;
	private double _planAspect;
	// The map's pixels in a meter of the ground, so the camera's widths (meters) become the window's (pixels).
	private readonly double _pixelsPerMeter = 1;
	private bool _disposed;
	private bool _surfaceFailureLogged;

	internal TrackBackdrop(RouteMapMosaic mosaic, TrackMapSettings options, int baseWidth, IReadOnlyList<DerivedFrame> route, RouteMapMosaic? wide = null)
	{
		_mosaic = mosaic;
		_wide = wide;
		_wideLevels = wide is null ? [] : HalvedImages(wide.Image, LevelsFor(wide.Image.Width, baseWidth));
		if (wide is not null && route.Count > 0)
		{
			(_anchorOnMap, _anchorOnWide) = (mosaic.GetPixel(route[0].Raw.Latitude, route[0].Raw.Longitude), wide.GetPixel(route[0].Raw.Latitude, route[0].Raw.Longitude));
			_wideScale = (float)Math.Pow(2, wide.Zoom - mosaic.Zoom);
		}

		_baseWidth = baseWidth;
		_options = options;
		_levels = options.Follow ? HalvedImages(mosaic.Image, LevelsFor(mosaic.Image.Width, baseWidth)) : [mosaic.Image];
		if (route.Count > 0)
		{
			GeoBounds bounds = mosaic.Route;
			double lat = (bounds.MinLat + bounds.MaxLat) / 2, lon = (bounds.MinLon + bounds.MaxLon) / 2;
			double metersAlong = 0.001 * 111320 * Math.Cos(AngleMath.DegToRad(lat));
			double pixelsAlong = Math.Abs(mosaic.GetPixel(lat, lon + 0.001).X - mosaic.GetPixel(lat, lon).X);
			if (metersAlong > 0 && pixelsAlong > 0) _pixelsPerMeter = pixelsAlong / metersAlong;
			if (options.Follow)
				AppLogger.Info($"Track map: zoom {mosaic.Zoom}, {_pixelsPerMeter:0.00} px/m, picture {baseWidth} px = {baseWidth / _pixelsPerMeter:0} m, view {mosaic.View.Width:0} x {mosaic.View.Height:0} px");
		}

		_baseColor = SKColor.TryParse(options.TrailColor, out SKColor parsed) ? parsed : SKColor.Parse(OverlayRenderer.DefaultTrailColorHex);
		if (!options.ShowTrail) return;

		// A stretch with no fix is crossed as GpsLossJoin says (the Map widget's setting): a break, a dashed bridge or a straight line.
		int step = Math.Max(1, route.Count / MaxTrailPoints);
		List<SKPoint> points = [];
		List<bool> breaks = [];
		List<bool> dashed = [];
		List<double> times = [];
		List<double> speeds = [];
		for (int i = 0; i < route.Count; i++)
		{
			bool afterLoss = i > 0 && route[i].Raw.StartsAfterGap;
			bool restart = i == 0 || (afterLoss && options.GpsLossJoin != RouteJoin.Straight);
			// The point before a loss is kept too, so a bridge starts where the line really stopped.
			if (i % step != 0 && !restart && !afterLoss && !(i + 1 < route.Count && route[i + 1].Raw.StartsAfterGap)) continue;

			points.Add(mosaic.GetPixel(route[i].Raw.Latitude, route[i].Raw.Longitude));
			breaks.Add(restart);
			dashed.Add(afterLoss && options.GpsLossJoin == RouteJoin.Dashed);
			times.Add(route[i].Raw.SampleTimeSeconds);
			speeds.Add(double.IsFinite(route[i].SpeedKmh) ? route[i].SpeedKmh : 0);
		}

		_trailPoints = [.. points];
		_trailBreaks = [.. breaks];
		_trailDashed = [.. dashed];
		_trailTimes = [.. times];
		if (options.ColorBySpeed)
		{
			double scaleKmh = TrackCamera.ReferenceKmh(route);
			_trailColors = [.. SmoothedSpeeds(speeds, breaks).Select(v => SpeedColorScale.At(_baseColor, v / scaleKmh))];
		}

		_trail = TrailPaths(_trailPoints.Length, null);
	}

	/// <summary>A moving average over the neighbouring points of a stretch, so GPS speed noise doesn't flip the line between colors every few points.</summary>
	private static double[] SmoothedSpeeds(List<double> speeds, List<bool> breaks)
	{
		const int Radius = 3;
		double[] result = new double[speeds.Count];
		for (int i = 0; i < speeds.Count; i++)
		{
			double sum = speeds[i];
			int count = 1;
			for (int j = i - 1; j >= Math.Max(0, i - Radius) && !breaks[j + 1]; j--) (sum, count) = (sum + speeds[j], count + 1);
			for (int j = i + 1; j <= Math.Min(speeds.Count - 1, i + Radius) && !breaks[j]; j++) (sum, count) = (sum + speeds[j], count + 1);
			result[i] = sum / count;
		}

		return result;
	}

	/// <summary>The levels to the widest a view of an image can be (about twice its width, in the closing shot) in a picture `outputWidth` wide.</summary>
	private static int LevelsFor(int imageWidth, int outputWidth)
	{
		return Math.Clamp(1 + (int)Math.Ceiling(Math.Log2(Math.Max(1, 2.0 * imageWidth / Math.Max(1, outputWidth)))), MinFollowLevels, MaxFollowLevels);
	}

	private static SKImage[] HalvedImages(SKImage full, int count)
	{
		var levels = new SKImage[count];
		levels[0] = full;
		for (int i = 1; i < count; i++)
		{
			SKImage previous = levels[i - 1];
			var info = new SKImageInfo(Math.Max(1, previous.Width / 2), Math.Max(1, previous.Height / 2), SKColorType.Bgra8888, SKAlphaType.Premul);
			using var surface = SKSurface.Create(info);
			surface.Canvas.DrawImage(previous, new SKRect(0, 0, info.Width, info.Height), MapSampling);
			levels[i] = surface.Snapshot();
		}

		return levels;
	}

	private readonly record struct TrailSegment(SKPoint From, SKPoint To, SKColor FromColor, SKColor ToColor);

	/// <summary>A route to stroke: the whole of it as one path (the edge, and the line when it has one color) and, colored by speed, its stretches with the colors at their ends.</summary>
	private sealed class TrailShape(SKPath path, List<TrailSegment> segments, List<TrailSegment> bridges) : IDisposable
	{
		public SKPath Path { get; } = path;
		public List<TrailSegment> Segments { get; } = segments;

		/// <summary>The stretches across a loss that are drawn dashed, each with the colors at its ends.</summary>
		public List<TrailSegment> Bridges { get; } = bridges;

		public void Dispose()
		{
			Path.Dispose();
		}
	}

	/// <summary>
	///     The line through the first `count` points of the route from `from`, carried on to `tip` (the position now) when there is one. With
	///     `visible`, only the stretches that touch it are in the shape: a following view shows a small part of a long route, and
	///     stroking all of it in every frame was most of the drawing time. What shows is the same - a stretch is cut only where
	///     it is wholly outside, and the rest keeps its joins.
	/// </summary>
	private TrailShape TrailPaths(int count, SKPoint? tip, SKRect? visible = null, int from = 0)
	{
		bool bySpeed = _trailColors.Length > 0;
		List<TrailSegment> segments = [], bridges = [];
		using var builder = new SKPathBuilder();
		// Where the stretch being drawn ends (a point's index), so one that carries on isn't started again.
		int end = -1;
		SKPoint last = default;
		SKColor lastColor = default;

		for (int i = Math.Max(from, 1); i < count; i++)
		{
			SKPoint point = _trailPoints[i], previous = _trailPoints[i - 1];
			if (visible is { } window && !window.IntersectsWith(SKRect.Create(Math.Min(previous.X, point.X), Math.Min(previous.Y, point.Y),
				    Math.Abs(point.X - previous.X), Math.Abs(point.Y - previous.Y))))
				continue;

			if (_trailBreaks[i])
			{
				if (_trailDashed[i]) bridges.Add(new TrailSegment(previous, point, bySpeed ? _trailColors[i - 1] : default, bySpeed ? _trailColors[i] : default));
				continue;
			}

			if (end != i - 1)
			{
				builder.MoveTo(previous);
				(last, lastColor) = (previous, bySpeed ? _trailColors[i - 1] : default);
			}

			end = i;
			builder.LineTo(point);
			if (bySpeed) segments.Add(new TrailSegment(last, point, lastColor, _trailColors[i]));
			(last, lastColor) = (point, bySpeed ? _trailColors[i] : default);
		}

		if (tip is { } now && count > 0)
		{
			SKPoint start = _trailPoints[count - 1];
			if (end != count - 1) builder.MoveTo(start);
			builder.LineTo(now);
			if (bySpeed) segments.Add(new TrailSegment(start, now, _trailColors[count - 1], _trailColors[count - 1]));
		}

		return new TrailShape(builder.Detach(), segments, bridges);
	}

	/// <summary>How many points of the route are at or before a time.</summary>
	private int PassedPoints(double seconds)
	{
		int low = 0, high = _trailTimes.Length;
		while (low < high)
		{
			int middle = (low + high) / 2;
			if (_trailTimes[middle] <= seconds) low = middle + 1;
			else high = middle;
		}

		return low;
	}

	/// <summary>Null when there is nothing to show: no fix in the track, no key for the provider, or no tile could be fetched.</summary>
	public static async Task<TrackBackdrop?> BuildAsync(IReadOnlyList<DerivedFrame> frames, OverlaySettings settings, TrackOnlyFormat format, CancellationToken ct = default)
	{
		var options = TrackMapSettings.From(settings);
		var sources = MapSources.From(settings);
		if (sources.SetupProblem(options.ProviderId) is { } problem)
		{
			AppLogger.Warn(problem);
			return null;
		}

		// The processed frames, not the file's points: the repaired ones, so a GPS spike neither stretches the map nor draws into the line.
		List<DerivedFrame> route = [.. frames.Where(f => f.Raw.HasGpsFix && double.IsFinite(f.Raw.Latitude) && double.IsFinite(f.Raw.Longitude))];
		if (route.Count == 0) return null;
		List<(double Lat, double Lon)> points = [.. route.Select(f => (f.Raw.Latitude, f.Raw.Longitude))];

		try
		{
			string? labels = sources.LabelsUrlTemplate(options.ProviderId, showLabels: options.ShowLabels);
			RouteMapMosaic? mosaic = await RouteMapMosaic.BuildAsync(points, sources.UrlTemplate(options.ProviderId), options.MaxZoom,
				options.Follow ? FollowPaddingTiles : 0, ct, null, options.Follow ? null : format.Width / (double)format.Height,
				sources.CacheMaxAge(options.ProviderId), labels, options.Follow ? null : (format.Width / 2.0, format.Height / 2.0), 1, options.Follow ? FollowMaxTiles : null);
			if (mosaic is null) return null;

			RouteMapMosaic? wide = null;
			try
			{
				if (options.Follow)
				{
					// Does the closing shot (the whole route in the picture's shape) reach past the map? Then a coarser one in that shape goes under it.
					double aspect = format.Width / (double)format.Height;
					SKPoint corner = mosaic.GetPixel(mosaic.Route.MaxLat, mosaic.Route.MinLon), opposite = mosaic.GetPixel(mosaic.Route.MinLat, mosaic.Route.MaxLon);
					double needed = Math.Max(Math.Abs(opposite.X - corner.X), Math.Abs(opposite.Y - corner.Y) * aspect) * TrackCamera.Plan.FinaleMargin;
					if (needed > mosaic.View.Width)
						wide = await RouteMapMosaic.BuildAsync(points, sources.UrlTemplate(options.ProviderId), Math.Max(RouteMapMosaic.MinZoom, mosaic.Zoom - 1), 0, ct, null, aspect,
							sources.CacheMaxAge(options.ProviderId), labels, null, 1.25);
				}

				return new TrackBackdrop(mosaic, options, format.Width, route, wide);
			}
			catch
			{
				wide?.Dispose();
				mosaic.Dispose();
				throw;
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			AppLogger.Warn(ex, "The track's map backdrop couldn't be prepared - the picture stays black");
			return null;
		}
	}

	/// <summary>
	///     The picture at a time on the track's own clock, as opaque BGRA over the whole of `bgra`, drawn into it in place:
	///     the map, the track, then the position - the aircraft where the log has its attitude, else the heading arrow (TrailMarker, the Map widget's).
	///     `frames` are the track's processed ones (ExternalTrackPlayback.DerivedFrames).
	/// </summary>
	public unsafe void DrawInto(byte[] bgra, int width, int height, double trackSeconds, IReadOnlyList<DerivedFrame> frames)
	{
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		lock (_lock)
		{
			if (_disposed) return;

			if (!ReferenceEquals(_referenceFrames, frames))
			{
				_referenceFrames = frames;
				_referenceKmh = TrackCamera.ReferenceKmh(frames);
				_rotorPhases = null;
				_plan = null;
			}

			SKRect window = Window(frames, trackSeconds, width, height);
			float scale = width / window.Width;
			bool still = !_options.Follow && EnsureStill(width, height, window, scale);
			if (still) Buffer.BlockCopy(_still!, 0, bgra, 0, width * height * 4);

			fixed (byte* pixels = bgra)
			{
				using var surface = SKSurface.Create(info, (IntPtr)pixels, width * 4);
				if (surface is null)
				{
					if (!_surfaceFailureLogged)
					{
						_surfaceFailureLogged = true;
						AppLogger.Warn("The track's backdrop couldn't draw onto the frame - frames are left black");
					}

					MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, width * height * 4)).Fill(0xFF000000);
					return;
				}

				SKCanvas canvas = surface.Canvas;
				if (!still) DrawBase(canvas, window, scale, width, height, _options.Follow ? Math.Max(0, PassedPoints(trackSeconds) - 1) : 0);

				DerivedFrame? shown = TrackCamera.FrameAt(frames, trackSeconds);
				TelemetryFrame? fix = shown?.Raw;
				SKPoint? tip = fix is null ? null : _mosaic.GetPixel(fix.Latitude, fix.Longitude);
				if (_trail is not null) DrawPassedTrail(canvas, window, scale, height, trackSeconds, tip);

				if (fix is not null)
				{
					SKPoint point = Scaled(tip!.Value, window, scale);
					if (fix.DroneAttitude is { } attitude)
					{
						Span<RotorSpin.RotorPose> rotors = stackalloc RotorSpin.RotorPose[RotorSpin.Count];
						_rotorPhases ??= RotorSpin.Phases(frames, _referenceKmh);
						RotorSpin.At(frames, _rotorPhases, trackSeconds, _referenceKmh, rotors);
						_drone.Draw(canvas, point, height / 20f * _options.MarkerScale, attitude, rotors, true);
					}
					else _marker.Draw(canvas, point.X, point.Y, shown!.SteadyHeadingDegrees, height / 1080f * _options.MarkerScale, MarkerFill, true);
				}
			}
		}
	}

	/// <summary>The map and the dim line ahead: all of the route with the view fixed (the same at any time), only what is still to come while following.</summary>
	private void DrawBase(SKCanvas canvas, SKRect window, float scale, int width, int height, int aheadFrom = 0)
	{
		canvas.Clear(SKColors.Black);
		if (_wide is not null && (window.Left < 0 || window.Top < 0 || window.Right > _mosaic.Image.Width || window.Bottom > _mosaic.Image.Height))
		{
			var onWide = new SKRect(_wideScale * (window.Left - _anchorOnMap.X) + _anchorOnWide.X, _wideScale * (window.Top - _anchorOnMap.Y) + _anchorOnWide.Y,
				_wideScale * (window.Right - _anchorOnMap.X) + _anchorOnWide.X, _wideScale * (window.Bottom - _anchorOnMap.Y) + _anchorOnWide.Y);
			DrawMap(canvas, _wideLevels, onWide, width);
		}

		DrawMap(canvas, _levels, window, width);

		if (_trail is null || !_options.ShowAhead) return;

		if (!_options.Follow)
		{
			StrokeTrail(canvas, _trail, window, scale, height, TrailAheadAlpha);
			return;
		}

		using TrailShape ahead = TrailPaths(_trailPoints.Length, null, Visible(window, scale, height), aheadFrom);
		StrokeTrail(canvas, ahead, window, scale, height, TrailAheadAlpha);
	}

	/// <summary>
	///     The window (in the pixels of the sharpest level) from the level that suits the zoom, the next coarser one over it in the share the zoom
	///     is between them. Each level is only ever drawn between one for one and half size, where a linear sample is clean.
	/// </summary>
	private void DrawMap(SKCanvas canvas, SKImage[] levels, SKRect window, int width)
	{
		float scale = width / window.Width;
		double exact = scale >= 1 ? 0 : Math.Min(levels.Length - 1, Math.Log2(1.0 / scale));
		int level = Math.Min(levels.Length - 1, (int)exact);
		DrawClipped(canvas, levels[level], AtLevel(window, level), width, _imagePaint);

		double blend = exact - level;
		if (level + 1 >= levels.Length || blend < 0.02) return;

		_blendPaint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * blend));
		DrawClipped(canvas, levels[level + 1], AtLevel(window, level + 1), width, _blendPaint);
	}

	private static SKRect AtLevel(SKRect window, int level)
	{
		float factor = 1f / (1 << level);
		return new SKRect(window.Left * factor, window.Top * factor, window.Right * factor, window.Bottom * factor);
	}

	/// <summary>A window can reach past the map (the closing shot): only the part of it that is map is drawn, where it lands in the picture.</summary>
	private void DrawClipped(SKCanvas canvas, SKImage image, SKRect source, int width, SKPaint paint)
	{
		float left = Math.Max(source.Left, 0), top = Math.Max(source.Top, 0), right = Math.Min(source.Right, image.Width), bottom = Math.Min(source.Bottom, image.Height);
		if (right <= left || bottom <= top) return;

		float pixelsPerSource = width / source.Width;
		var target = new SKRect((left - source.Left) * pixelsPerSource, (top - source.Top) * pixelsPerSource, (right - source.Left) * pixelsPerSource, (bottom - source.Top) * pixelsPerSource);
		canvas.DrawImage(image, new SKRect(left, top, right, bottom), target, MapSampling, paint);
	}

	/// <summary>The window in the map's pixels with a margin for the line's width, outside which no stroke can reach the picture.</summary>
	private SKRect Visible(SKRect window, float scale, int height)
	{
		float margin = StrokeWidth(scale, height) * 2;
		return SKRect.Inflate(window, margin, margin);
	}

	/// <summary>The line's width in the map's pixels: its own (of a 1080-pixel-high picture) in the picture's, whatever the zoom.</summary>
	private float StrokeWidth(float scale, int height)
	{
		return _options.TrailWidth * height / 1080f / scale;
	}

	/// <summary>Draws the base once for a size (the view being fixed) into a buffer of its own; false when it couldn't be.</summary>
	private unsafe bool EnsureStill(int width, int height, SKRect window, float scale)
	{
		if (_still is not null && _stillWidth == width && _stillHeight == height) return true;

		byte[] buffer = new byte[width * height * 4];
		fixed (byte* pixels = buffer)
		{
			using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)pixels, width * 4);
			if (surface is null) return false;

			DrawBase(surface.Canvas, window, scale, width, height);
		}

		(_still, _stillWidth, _stillHeight) = (buffer, width, height);
		return true;
	}

	/// <summary>The part of the map in the picture, in the map's pixels - the whole view, or following, the camera's window kept inside it.</summary>
	private SKRect Window(IReadOnlyList<DerivedFrame> frames, double trackSeconds, int width, int height)
	{
		SKRect view = _mosaic.View;
		if (!_options.Follow) return view;

		double aspect = width / (double)height;
		if (_plan is null || Math.Abs(_planAspect - aspect) > 1e-3)
		{
			double closestPixels = _baseWidth * TrackCamera.ClosestFactor;
			// No wider than the map there is: a width past it would be planned for and never shown.
			double widestPixels = Math.Max(closestPixels, Math.Min(_baseWidth * TrackCamera.MaxZoomOut, Math.Min(view.Width, view.Height * aspect)));
			_plan = TrackCamera.Plan.Build(frames, aspect, closestPixels / _pixelsPerMeter, widestPixels / _pixelsPerMeter);
			_planAspect = aspect;
		}

		if (_plan.At(trackSeconds) is not { } shot) return view;

		// Never taller than the map; wider than it only for the closing shot of the whole route (a tall route in a wide picture), then centered on it with
		// nothing but black either side.
		double windowWidth = Math.Min(shot.WidthMeters * _pixelsPerMeter, view.Height * aspect);
		double windowHeight = windowWidth / aspect;
		SKPoint center = _mosaic.GetPixel(shot.Latitude, shot.Longitude);
		double left = center.X - windowWidth / 2, rightLimit = view.Right - windowWidth;
		return SKRect.Create((float)Math.Clamp(left, Math.Min(view.Left, rightLimit), Math.Max(view.Left, rightLimit)),
			(float)Math.Clamp(center.Y - windowHeight / 2, view.Top, view.Bottom - windowHeight), (float)windowWidth, (float)windowHeight);
	}

	private static SKPoint Scaled(SKPoint pixel, SKRect window, float scale)
	{
		return new SKPoint((pixel.X - window.Left) * scale, (pixel.Y - window.Top) * scale);
	}

	private void DrawPassedTrail(SKCanvas canvas, SKRect window, float scale, int height, double trackSeconds, SKPoint? tip)
	{
		int passed = PassedPoints(trackSeconds);
		if (passed == 0) return;

		using TrailShape shape = TrailPaths(passed, tip, _options.Follow ? Visible(window, scale, height) : null, 0);
		StrokeTrail(canvas, shape, window, scale, height, TrailPassedAlpha);
	}

	private const int SolidColorTolerance = 4;

	private static bool IsNearlyOneColor(SKColor a, SKColor b)
	{
		return Math.Abs(a.Red - b.Red) <= SolidColorTolerance && Math.Abs(a.Green - b.Green) <= SolidColorTolerance && Math.Abs(a.Blue - b.Blue) <= SolidColorTolerance;
	}

	private void StrokeTrail(SKCanvas canvas, TrailShape shape, SKRect window, float scale, int height, byte lineAlpha)
	{
		float stroke = StrokeWidth(scale, height);
		canvas.Save();
		canvas.Scale(scale);
		canvas.Translate(-window.Left, -window.Top);

		// A part is drawn opaque into a layer and made translucent as a whole: round caps of neighbouring stretches overlap,
		// and translucent strokes would add up there into dots along the line.
		bool layered = lineAlpha < 255;
		if (layered)
		{
			_layerPaint.Color = SKColors.White.WithAlpha(lineAlpha);
			canvas.SaveLayer(_layerPaint);
		}

		_trailOutline.StrokeWidth = stroke * EdgeWidthFactor;
		_trailOutline.Color = TrailEdge;
		canvas.DrawPath(shape.Path, _trailOutline);

		_trailLine.StrokeWidth = stroke;
		if (shape.Segments.Count == 0)
		{
			_trailLine.Color = _baseColor;
			canvas.DrawPath(shape.Path, _trailLine);
		}
		else
		{
			// In the route's order, so where it crosses itself the later stretch is on top. A stretch whose ends are about one color is
			// drawn plain: a gradient shader per stretch is the costly part, and most stretches of a smoothed route are that close.
			foreach (TrailSegment segment in shape.Segments)
			{
				if (IsNearlyOneColor(segment.FromColor, segment.ToColor) || segment.From == segment.To)
				{
					_trailLine.Color = SpeedColorScale.Blend(segment.FromColor, segment.ToColor, 0.5);
					canvas.DrawLine(segment.From, segment.To, _trailLine);
					continue;
				}

				_gradientColors[0] = segment.FromColor;
				_gradientColors[1] = segment.ToColor;
				using var shader = SKShader.CreateLinearGradient(segment.From, segment.To, _gradientColors, SKShaderTileMode.Clamp);
				_trailLine.Shader = shader;
				canvas.DrawLine(segment.From, segment.To, _trailLine);
				_trailLine.Shader = null;
			}
		}

		if (shape.Bridges.Count > 0) StrokeBridges(canvas, shape.Bridges, stroke);

		_trailOutline.StrokeWidth = stroke * CoreWidthFactor;
		_trailOutline.Color = TrailCore;
		canvas.DrawPath(shape.Path, _trailOutline);

		if (layered) canvas.Restore();
		canvas.Restore();
	}

	/// <summary>The stretches the line crosses a loss by, dashed like the Map widget's: the edge, then the color (the one of the middle of the stretch).</summary>
	private void StrokeBridges(SKCanvas canvas, List<TrailSegment> bridges, float stroke)
	{
		if (_dashEffect is null || Math.Abs(_dashStroke - stroke) > 1e-3f)
		{
			_dashEffect?.Dispose();
			_dashEffect = SKPathEffect.CreateDash([Math.Max(6, stroke * 3), Math.Max(5, stroke * 2.5f)], 0);
			_dashStroke = stroke;
		}

		_trailOutline.PathEffect = _trailLine.PathEffect = _dashEffect;
		_trailOutline.StrokeWidth = stroke * EdgeWidthFactor;
		_trailOutline.Color = TrailEdge;
		foreach (TrailSegment bridge in bridges) canvas.DrawLine(bridge.From, bridge.To, _trailOutline);

		_trailLine.StrokeWidth = stroke;
		foreach (TrailSegment bridge in bridges)
		{
			_trailLine.Color = _trailColors.Length > 0 ? SpeedColorScale.Blend(bridge.FromColor, bridge.ToColor, 0.5) : _baseColor;
			canvas.DrawLine(bridge.From, bridge.To, _trailLine);
		}

		_trailOutline.PathEffect = _trailLine.PathEffect = null;
	}

	/// <summary>The map this picture shows, for its credit: drawn by the overlay (OverlayRenderer.BackdropMap), not here.</summary>
	public BackdropMap Map => new(_options.ProviderId, _options.ShowLabels);

	public void Dispose()
	{
		lock (_lock)
		{
			if (_disposed) return;

			_disposed = true;
			_trail?.Dispose();
			for (int i = 1; i < _levels.Length; i++) _levels[i].Dispose();
			_still = null;
			_drone.Dispose();
			_marker.Dispose();
			foreach (SKPaint paint in new[] { _imagePaint, _blendPaint, _trailOutline, _trailLine, _layerPaint })
				paint.Dispose();
			_dashEffect?.Dispose();
			_mosaic.Dispose();
			for (int i = 1; i < _wideLevels.Length; i++) _wideLevels[i].Dispose();
			_wide?.Dispose();
		}
	}
}
