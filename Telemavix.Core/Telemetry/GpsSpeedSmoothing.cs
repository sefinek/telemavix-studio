using System.Buffers;

namespace Telemavix.Core.Telemetry;

/// <summary>
///     The receiver's own speed (TelemetryFrame.GpsSpeedMs) is a reading per fix - a phone's or a watch's one a second - and carries the receiver's noise: a step up and
///     back down over a second or two that nothing in the track explains, which the speed gauge then shows as a flick of the needle. Smoothing it is a median over
///     MedianHalfWindowSeconds on each side, which cuts such a spike down to the height its flanks have (a spike of one fix to about half its excess), then a mean over
///     MeanHalfWindowSeconds, which rounds the corners the median leaves. A change that lasts - a stop, a pull away - passes through with only its corners rounded.
///     The windows are in seconds, so a 10 Hz track and a 60 fps recording come out alike. They never reach across a gap between files (StartsAfterGap) or a frame with no receiver
///     speed (a lost fix, or a camera that has none), where a stretch ends. Only the speed the HUD shows is smoothed (TelemetryProcessor.Process): the frames keep the receiver's
///     reading, which the outlier repair, the GPS alert and the standstill test of the distance read as it was.
/// </summary>
internal static class GpsSpeedSmoothing
{
	internal const double MedianHalfWindowSeconds = 1.0;
	internal const double MeanHalfWindowSeconds = 1.0;

	/// <summary>The smoothed receiver speed of every frame (m/s), null where the frame has none.</summary>
	public static double?[] Apply(IReadOnlyList<TelemetryFrame> frames)
	{
		var result = new double?[frames.Count];
		int start = 0;
		while (start < frames.Count)
		{
			if (frames[start].GpsSpeedMs is null)
			{
				start++;
				continue;
			}

			int end = start + 1;
			while (end < frames.Count && frames[end].GpsSpeedMs is not null && !frames[end].StartsAfterGap) end++;

			Smooth(frames, start, end, result);
			start = end;
		}

		return result;
	}

	private static void Smooth(IReadOnlyList<TelemetryFrame> frames, int start, int end, double?[] result)
	{
		int count = end - start;
		double[] medians = ArrayPool<double>.Shared.Rent(count);
		double[] window = ArrayPool<double>.Shared.Rent(count);
		double[] sums = ArrayPool<double>.Shared.Rent(count + 1);
		try
		{
			int low = 0, high = 0;
			for (int i = 0; i < count; i++)
			{
				double time = frames[start + i].SampleTimeSeconds;
				while (time - frames[start + low].SampleTimeSeconds > MedianHalfWindowSeconds) low++;
				while (high < count && frames[start + high].SampleTimeSeconds - time <= MedianHalfWindowSeconds) high++;

				for (int j = low; j < high; j++) window[j - low] = frames[start + j].GpsSpeedMs!.Value;
				medians[i] = Median(window, high - low);
			}

			sums[0] = 0;
			for (int i = 0; i < count; i++) sums[i + 1] = sums[i] + medians[i];

			low = 0;
			high = 0;
			for (int i = 0; i < count; i++)
			{
				double time = frames[start + i].SampleTimeSeconds;
				while (time - frames[start + low].SampleTimeSeconds > MeanHalfWindowSeconds) low++;
				while (high < count && frames[start + high].SampleTimeSeconds - time <= MeanHalfWindowSeconds) high++;

				result[start + i] = (sums[high] - sums[low]) / (high - low);
			}
		}
		finally
		{
			ArrayPool<double>.Shared.Return(medians);
			ArrayPool<double>.Shared.Return(window);
			ArrayPool<double>.Shared.Return(sums);
		}
	}

	/// <summary>The middle of the first `length` values (the mean of the two in the middle for an even count); the values are put in order.</summary>
	private static double Median(double[] values, int length)
	{
		Array.Sort(values, 0, length);
		return length % 2 == 1 ? values[length / 2] : (values[length / 2 - 1] + values[length / 2]) / 2;
	}
}
