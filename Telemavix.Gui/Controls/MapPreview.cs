using Avalonia.Media.Imaging;
using SkiaSharp;
using Telemavix.Core.Logging;
using Telemavix.Core.Mapping;

namespace Telemavix.Gui;

internal static class MapPreview
{
	/// <summary>One map's picture of an area, fetched like a render's (the same cache rules and server cooldowns). Null when nothing could be fetched.</summary>
	public static async Task<Bitmap?> RenderAsync(IReadOnlyList<(double Lat, double Lon)> area, string urlTemplate, int zoom, TimeSpan? cacheMaxAge,
		string? labelsUrlTemplate, CancellationToken ct = default)
	{
		try
		{
			using RouteMapMosaic? mosaic = await Task.Run(() => RouteMapMosaic.BuildAsync(area, urlTemplate, zoom, 0, ct,
				targetAspectRatio: 2.0, cacheMaxAge: cacheMaxAge, labelsUrlTemplate: labelsUrlTemplate), ct);
			if (mosaic is null) return null;

			using SKImage? view = mosaic.Image.Subset(SKRectI.Round(mosaic.View));
			if (view is null) return null;

			using SKData png = view.Encode(SKEncodedImageFormat.Png, 100);
			using var stream = new MemoryStream(png.ToArray());
			return new Bitmap(stream);
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			AppLogger.Info($"Map preview failed: {ex.Message}");
			return null;
		}
	}
}
