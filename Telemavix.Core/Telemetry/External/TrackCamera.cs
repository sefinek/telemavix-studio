namespace Telemavix.Core.Telemetry.External;

/// <summary>
///     Where the map behind a track looks and how wide: on the position, as wide as the stretch around it needs (Plan) - close on a
///     walk or a circle round one spot, open on a fast run. Only a function of the time asked for, so the picture of a frame never
///     depends on the frames drawn before.
/// </summary>
public static class TrackCamera
{
	/// <summary>The widest and closest width of the picture, in times its own width in the map's pixels: the closest is 0.65 of it, so the map is enlarged about 1.5 times there.</summary>
	public const double MaxZoomOut = 4, ClosestFactor = 0.65;

	private const double PositionHalfWindowSeconds = 1.5;
	// A track that hardly moves (a hover) has no pace to measure against.
	private const double MinReferenceKmh = 5;

	/// <summary>Where the picture looks and how many meters of the ground it is wide.</summary>
	public readonly record struct Shot(double Latitude, double Longitude, double WidthMeters);

	/// <summary>The speed a pace is measured against: what the track keeps to for most of it (its 90th percentile).</summary>
	public static double ReferenceKmh(IReadOnlyList<DerivedFrame> frames)
	{
		if (frames.Count == 0) return MinReferenceKmh;

		double[] speeds = [.. frames.Select(f => f.SpeedKmh).Where(double.IsFinite).Order()];
		return speeds.Length == 0 ? MinReferenceKmh : Math.Max(MinReferenceKmh, speeds[(int)((speeds.Length - 1) * 0.9)]);
	}

	/// <summary>
	///     Null when the track has no fix at all. The frames are 10 Hz and the picture 30 fps, so the position is carried between the
	///     two windows around the time instead of jumping to the nearest frame's.
	/// </summary>
	public static (double Latitude, double Longitude)? PositionAt(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		if (frames.Count == 0) return null;

		(int index, double fraction, double step) = Locate(frames, seconds);
		int positionReach = (int)Math.Ceiling(PositionHalfWindowSeconds / step);
		if (AveragePosition(frames, index, positionReach) is not { } position) return null;

		(double latitude, double longitude) = position;
		if (fraction > 0 && AveragePosition(frames, index + 1, positionReach) is { } next)
		{
			latitude += (next.Latitude - latitude) * fraction;
			longitude += (next.Longitude - longitude) * fraction;
		}

		return (latitude, longitude);
	}

	/// <summary>
	///     How wide the picture is at each moment of a track, planned once for all of it, like an operator who keeps a shot and changes
	///     it only when the scene asks for it. At every moment there is a band of widths that are right: not narrower than what has to
	///     be seen (the track just behind and soon ahead, with a margin), not wider than what is worth seeing (a longer stretch, with
	///     a roomier margin). Inside the band any width does; the plan is the one path through the bands that moves the least (a cost
	///     for each octave of zoom, so a short slowdown or a bend in a steady ride changes nothing, a long stop or a circle round one
	///     spot closes in, a fast run opens up) - then its steps are eased into zooms, in the logarithm of the width so they are even.
	///     It depends on the frames alone, never on which times were asked for before.
	/// </summary>
	public sealed class Plan
	{
		private const double GridSeconds = 0.5;
		// The narrowest: the track this long behind and this long ahead stays in the picture, the farthest point at 1/margin of the half-width.
		private const double NeedBackSeconds = 10, NeedAheadSeconds = 30, NeedMargin = 1.3;
		// The widest worth having: a longer stretch with more room round it.
		private const double ComfortBackSeconds = 60, ComfortAheadSeconds = 180, ComfortMargin = 2.4;
		// Costs per second: being narrower than the band (squared octaves, so a little is cheap and a lot isn't), being wider (the same, gentler),
		// a pull to the band's top so a steady shot isn't an arbitrary one, and per octave of change (what a shot costs to leave).
		private const double TooNarrowCost = 1, TooWideCost = 0.5, PullToComfortCost = 0.05, ZoomCostPerOctave = 6;
		private const double LevelStepOctaves = 0.5, SmoothSigmaSeconds = 3;
		private const double MetersPerDegree = 111320;
		// The last seconds: the picture eases out to the whole route, centered on it, and stays there (a track too short for it has none).
		private const double FinaleEaseSeconds = 6, FinaleHoldSeconds = 5;
		/// <summary>The closing shot is this much wider than the route (a share of its larger side, in the picture's shape).</summary>
		public const double FinaleMargin = 1.25;

		private readonly record struct Finale(double Start, double Latitude, double Longitude, double Log2Meters);

		private readonly IReadOnlyList<DerivedFrame> _frames;
		private readonly double _start;
		private readonly double[] _log2Meters;
		private readonly Finale? _finale;

		private Plan(IReadOnlyList<DerivedFrame> frames, double start, double[] log2Meters, Finale? finale = null)
		{
			_finale = finale;
			_frames = frames;
			_start = start;
			_log2Meters = log2Meters;
		}

		/// <summary>The width is kept between `minMeters` and `maxMeters`; `aspect` is the picture's width over its height.</summary>
		public static Plan Build(IReadOnlyList<DerivedFrame> frames, double aspect, double minMeters, double maxMeters)
		{
			if (frames.Count == 0) return new Plan(frames, 0, []);

			double first = frames[0].Raw.SampleTimeSeconds, last = frames[^1].Raw.SampleTimeSeconds;
			int count = Math.Max(1, (int)Math.Ceiling((last - first) / GridSeconds) + 1);
			minMeters = Math.Max(1e-3, minMeters);
			double lowLog = Math.Log2(minMeters), highLog = Math.Log2(Math.Max(minMeters, maxMeters));

			TelemetryFrame? origin = frames.Select(f => f.Raw).FirstOrDefault(f => f.HasGpsFix);
			if (origin is null) return new Plan(frames, first, [.. Enumerable.Repeat(lowLog, count)]);

			int[] segmentOfFrame = new int[frames.Count];
			for (int i = 1; i < frames.Count; i++) segmentOfFrame[i] = segmentOfFrame[i - 1] + (frames[i].Raw.StartsAfterGap ? 1 : 0);

			double metersPerDegreeLon = MetersPerDegree * Math.Cos(AngleMath.DegToRad(origin.Latitude));
			double[] x = new double[count], y = new double[count];
			int[] segment = new int[count];
			bool[] valid = new bool[count];
			for (int g = 0; g < count; g++)
			{
				(int index, double fraction, _) = Locate(frames, first + g * GridSeconds);
				if (fraction >= 0.5) index = Math.Min(index + 1, frames.Count - 1);

				TelemetryFrame frame = frames[index].Raw;
				if (!frame.HasGpsFix) continue;

				valid[g] = true;
				segment[g] = segmentOfFrame[index];
				x[g] = (frame.Longitude - origin.Longitude) * metersPerDegreeLon;
				y[g] = (frame.Latitude - origin.Latitude) * MetersPerDegree;
			}

			double[] narrowest = Framing(x, y, segment, valid, aspect, NeedBackSeconds, NeedAheadSeconds, NeedMargin, lowLog, highLog);
			double[] widest = Framing(x, y, segment, valid, aspect, ComfortBackSeconds, ComfortAheadSeconds, ComfortMargin, lowLog, highLog);
			// How fast it goes now against the track's own pace limits how roomy the picture is: a slow stretch closes in, whatever lies ahead of it.
			double[] speed = Speeds(x, y, segment, valid);
			double reference = ReferenceSpeed(speed);
			for (int i = 0; i < count; i++)
			{
				double cap = lowLog + (highLog - lowLog) * Math.Clamp(speed[i] / reference, 0, 1);
				widest[i] = Math.Max(narrowest[i], Math.Min(widest[i], cap));
			}

			double[] steps = Solve(narrowest, widest, lowLog, highLog);
			return new Plan(frames, first, Smooth(steps, SmoothSigmaSeconds / GridSeconds), WholeRoute(frames, first, last, aspect, minMeters, origin.Latitude));
		}

		/// <summary>The closing shot: the whole route in the picture. Null when the track is too short to have one.</summary>
		private static Finale? WholeRoute(IReadOnlyList<DerivedFrame> frames, double first, double last, double aspect, double minMeters, double originLatitude)
		{
			if (last - first < 3 * (FinaleEaseSeconds + FinaleHoldSeconds)) return null;

			double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
			foreach (DerivedFrame frame in frames)
			{
				if (!frame.Raw.HasGpsFix) continue;

				(minLat, maxLat) = (Math.Min(minLat, frame.Raw.Latitude), Math.Max(maxLat, frame.Raw.Latitude));
				(minLon, maxLon) = (Math.Min(minLon, frame.Raw.Longitude), Math.Max(maxLon, frame.Raw.Longitude));
			}

			double across = (maxLon - minLon) * MetersPerDegree * Math.Cos(AngleMath.DegToRad((minLat + maxLat) / 2));
			double up = (maxLat - minLat) * MetersPerDegree;
			double meters = Math.Max(minMeters, Math.Max(across, up * aspect) * FinaleMargin);
			return new Finale(last - FinaleEaseSeconds - FinaleHoldSeconds, (minLat + maxLat) / 2, (minLon + maxLon) / 2, Math.Log2(meters));
		}

		/// <summary>
		///     The logarithm of the width that keeps the farthest point of the track from `backSeconds` before to `aheadSeconds` after in
		///     the picture (within a stretch with no gap), at each step; steps with no fix take the one before.
		/// </summary>
		private static double[] Framing(double[] x, double[] y, int[] segment, bool[] valid, double aspect, double backSeconds, double aheadSeconds, double margin,
			double lowLog, double highLog)
		{
			int count = x.Length;
			int back = (int)Math.Ceiling(backSeconds / GridSeconds), ahead = (int)Math.Ceiling(aheadSeconds / GridSeconds);
			double[] result = new double[count];
			for (int i = 0; i < count; i++)
			{
				if (!valid[i]) continue;

				double reach = 0;
				for (int j = Math.Max(0, i - back); j <= Math.Min(count - 1, i + ahead); j++)
				{
					if (!valid[j] || segment[j] != segment[i]) continue;

					reach = Math.Max(reach, Math.Max(Math.Abs(x[j] - x[i]), Math.Abs(y[j] - y[i]) * aspect));
				}

				result[i] = Math.Clamp(Math.Log2(Math.Max(1e-3, 2 * reach * margin)), lowLog, highLog);
			}

			FillWithoutFix(result, valid, lowLog);
			return result;
		}

		/// <summary>The speed in meters a second at each step: across two seconds either side, then the mean of ten seconds either side (a stop-and-go isn't a standstill).</summary>
		private static double[] Speeds(double[] x, double[] y, int[] segment, bool[] valid)
		{
			int count = x.Length, reach = (int)Math.Round(2 / GridSeconds), mean = (int)Math.Round(10 / GridSeconds);
			double[] across = new double[count];
			for (int i = 0; i < count; i++)
			{
				int a = Math.Max(0, i - reach), b = Math.Min(count - 1, i + reach);
				if (b == a || !valid[a] || !valid[b] || segment[a] != segment[b]) continue;

				across[i] = Math.Sqrt(Math.Pow(x[b] - x[a], 2) + Math.Pow(y[b] - y[a], 2)) / ((b - a) * GridSeconds);
			}

			double[] result = new double[count];
			for (int i = 0; i < count; i++)
			{
				int a = Math.Max(0, i - mean), b = Math.Min(count - 1, i + mean);
				double sum = 0;
				for (int j = a; j <= b; j++) sum += across[j];
				result[i] = sum / (b - a + 1);
			}

			return result;
		}

		/// <summary>What the track keeps to when it moves (its 85th percentile above walking pace), never under a slow walk.</summary>
		private static double ReferenceSpeed(double[] speeds)
		{
			double[] moving = [.. speeds.Where(v => v > 0.5).Order()];
			return moving.Length == 0 ? 1.5 : Math.Max(1.5, moving[(int)((moving.Length - 1) * 0.85)]);
		}

		/// <summary>
		///     The path of widths (a few levels half an octave apart) with the least cost, by dynamic programming: at each step a
		///     price for being outside that step's band, and a price for every octave moved between two steps.
		/// </summary>
		private static double[] Solve(double[] narrowest, double[] widest, double lowLog, double highLog)
		{
			int count = narrowest.Length;
			int levels = Math.Max(1, (int)Math.Ceiling((highLog - lowLog) / LevelStepOctaves - 1e-9) + 1);
			double[] value = new double[levels];
			for (int k = 0; k < levels; k++) value[k] = Math.Min(highLog, lowLog + k * LevelStepOctaves);

			double DataCost(int step, int level)
			{
				double w = value[level], cost = 0;
				if (w < narrowest[step]) cost = TooNarrowCost * Math.Pow(narrowest[step] - w, 2);
				else if (w > widest[step]) cost = TooWideCost * Math.Pow(w - widest[step], 2);
				else cost = PullToComfortCost * Math.Pow(widest[step] - w, 2);
				return cost * GridSeconds;
			}

			byte[,] from = new byte[count, levels];
			double[] previous = new double[levels], current = new double[levels];
			for (int k = 0; k < levels; k++) previous[k] = DataCost(0, k);
			for (int t = 1; t < count; t++)
			{
				for (int k = 0; k < levels; k++)
				{
					double best = double.MaxValue;
					int bestFrom = k;
					for (int j = 0; j < levels; j++)
					{
						double cost = previous[j] + ZoomCostPerOctave * Math.Abs(value[k] - value[j]);
						if (cost >= best) continue;

						(best, bestFrom) = (cost, j);
					}

					current[k] = best + DataCost(t, k);
					from[t, k] = (byte)bestFrom;
				}

				(previous, current) = (current, previous);
			}

			int level = 0;
			for (int k = 1; k < levels; k++)
				if (previous[k] < previous[level]) level = k;

			double[] path = new double[count];
			for (int t = count - 1; t >= 0; t--)
			{
				path[t] = value[level];
				level = from[t, level];
			}

			return path;
		}

		/// <summary>Null when the track has no fix at all. The width is in meters of the ground.</summary>
		public Shot? At(double seconds)
		{
			if (PositionAt(_frames, seconds) is not { } position) return null;

			double meters = 1;
			if (_log2Meters.Length > 0)
			{
				double at = Math.Clamp((seconds - _start) / GridSeconds, 0, _log2Meters.Length - 1);
				int index = Math.Min((int)at, _log2Meters.Length - 1);
				int next = Math.Min(index + 1, _log2Meters.Length - 1);
				meters = Math.Pow(2, _log2Meters[index] + (_log2Meters[next] - _log2Meters[index]) * (at - index));
			}

			if (_finale is { } finale && seconds > finale.Start)
			{
				double u = Math.Clamp((seconds - finale.Start) / FinaleEaseSeconds, 0, 1);
				double weight = u * u * (3 - 2 * u);
				meters = Math.Pow(2, Math.Log2(meters) + (finale.Log2Meters - Math.Log2(meters)) * weight);
				position = (position.Latitude + (finale.Latitude - position.Latitude) * weight, position.Longitude + (finale.Longitude - position.Longitude) * weight);
			}

			return new Shot(position.Latitude, position.Longitude, meters);
		}

		/// <summary>A step with no fix takes the need of the last one before it (the first one's, ahead of the first fix).</summary>
		private static void FillWithoutFix(double[] values, bool[] valid, double fallback)
		{
			int firstValid = Array.IndexOf(valid, true);
			if (firstValid < 0)
			{
				Array.Fill(values, fallback);
				return;
			}

			for (int i = 0; i < firstValid; i++) values[i] = values[firstValid];
			for (int i = firstValid + 1; i < values.Length; i++)
				if (!valid[i]) values[i] = values[i - 1];
		}

		private static double[] Smooth(double[] values, double sigmaSteps)
		{
			int radius = Math.Max(1, (int)Math.Ceiling(sigmaSteps * 3));
			double[] kernel = new double[radius * 2 + 1];
			for (int k = -radius; k <= radius; k++) kernel[k + radius] = Math.Exp(-0.5 * k * k / (sigmaSteps * sigmaSteps));

			double[] result = new double[values.Length];
			for (int i = 0; i < values.Length; i++)
			{
				double sum = 0, weights = 0;
				for (int k = -radius; k <= radius; k++)
				{
					int j = i + k;
					if (j < 0 || j >= values.Length) continue;

					sum += values[j] * kernel[k + radius];
					weights += kernel[k + radius];
				}

				result[i] = sum / weights;
			}

			return result;
		}
	}

	/// <summary>
	///     The frame at a time with its position carried between the two frames around it (not across a gap), or null with no fix
	///     there - what the marker is drawn at, so it moves as smoothly as the view does.
	/// </summary>
	public static TelemetryFrame? FixAt(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		return FrameAt(frames, seconds)?.Raw;
	}

	/// <summary>The same carrying as FixAt, with what is derived from the frames (the heading the map turns by); null without a fix there.</summary>
	public static DerivedFrame? FrameAt(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		if (frames.Count == 0) return null;

		DerivedFrame frame = FrameInterpolation.At(frames, seconds);
		return frame.Raw.HasGpsFix ? frame : null;
	}

	/// <summary>The frames are evenly spaced: the one at or before a time, how far past it the time is (0 to 1) and the spacing.</summary>
	internal static (int Index, double Fraction, double Step) Locate(IReadOnlyList<DerivedFrame> frames, double seconds)
	{
		int count = frames.Count;
		double first = frames[0].Raw.SampleTimeSeconds;
		double step = count > 1 ? (frames[^1].Raw.SampleTimeSeconds - first) / (count - 1) : 1;
		if (step <= 0) step = 1;

		double at = Math.Clamp((seconds - first) / step, 0, count - 1);
		int index = Math.Min((int)at, count - 1);
		return (index, at - index, step);
	}

	/// <summary>The mean of the fixes within a reach of a frame, or the nearest fix when there is none in it.</summary>
	private static (double Latitude, double Longitude)? AveragePosition(IReadOnlyList<DerivedFrame> frames, int center, int reach)
	{
		center = Math.Min(center, frames.Count - 1);
		(double latitude, double longitude, int fixes) = (0, 0, 0);
		for (int i = Math.Max(0, center - reach); i <= Math.Min(frames.Count - 1, center + reach); i++)
		{
			if (!frames[i].Raw.HasGpsFix) continue;
			latitude += frames[i].Raw.Latitude;
			longitude += frames[i].Raw.Longitude;
			fixes++;
		}

		if (fixes > 0) return (latitude / fixes, longitude / fixes);
		return NearestFix(frames, center) is { } nearest ? (nearest.Latitude, nearest.Longitude) : null;
	}

	private static TelemetryFrame? NearestFix(IReadOnlyList<DerivedFrame> frames, int index)
	{
		for (int distance = 1; distance < frames.Count; distance++)
		{
			if (index - distance >= 0 && frames[index - distance].Raw.HasGpsFix) return frames[index - distance].Raw;
			if (index + distance < frames.Count && frames[index + distance].Raw.HasGpsFix) return frames[index + distance].Raw;
		}

		return null;
	}
}
