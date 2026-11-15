namespace Telemavix.Core.Ffmpeg;

internal static class HevcEncodingLimits
{
	// Main/Main 10 limits per level: max luma picture size, max luma samples a second and bitrate in bits/s.
	// Camera headers can declare Main Tier above its bitrate limit, or a level too low for their own picture (Lito X1: 4K60 at 5.0).
	private static readonly (int Level, long LumaPs, long LumaSr, long Main, long High)[] Levels =
	[
		(30, 36_864, 552_960, 128_000, 0),
		(60, 122_880, 3_686_400, 1_500_000, 0),
		(63, 245_760, 7_372_800, 3_000_000, 0),
		(90, 552_960, 16_588_800, 6_000_000, 0),
		(93, 983_040, 33_177_600, 10_000_000, 0),
		(120, 2_228_224, 66_846_720, 12_000_000, 30_000_000),
		(123, 2_228_224, 133_693_440, 20_000_000, 50_000_000),
		(150, 8_912_896, 267_386_880, 25_000_000, 100_000_000),
		(153, 8_912_896, 534_773_760, 40_000_000, 160_000_000),
		(156, 8_912_896, 1_069_547_520, 60_000_000, 240_000_000),
		(180, 35_651_584, 1_069_547_520, 60_000_000, 240_000_000),
		(183, 35_651_584, 2_139_095_040, 120_000_000, 480_000_000),
		(186, 35_651_584, 4_278_190_080, 240_000_000, 800_000_000)
	];

	internal static VideoInfo For(VideoInfo video, long bitRate)
	{
		if (video.Level <= 0) return video;

		long lumaPs = (long)video.Width * video.Height;
		long lumaSr = (long)Math.Ceiling(lumaPs * video.Fps);

		foreach ((int Level, long LumaPs, long LumaSr, long Main, long High) level in Levels)
		{
			if (level.Level < video.Level || level.LumaPs < lumaPs || level.LumaSr < lumaSr) continue;

			bool high = video.HighTier == true || bitRate > level.Main;
			if (bitRate <= (high ? level.High : level.Main))
				return video with { Level = level.Level, HighTier = high };
		}

		return video with { Level = 0, HighTier = true };
	}
}
