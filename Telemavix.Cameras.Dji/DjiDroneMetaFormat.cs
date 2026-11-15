using System.Text.Json.Nodes;
using Telemavix.Core.Cameras;
using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Localization;
using Telemavix.Core.Overlay;
using Telemavix.Core.Reframe;
using Telemavix.Core.Telemetry;

namespace Telemavix.Cameras.Dji;

/// <summary>
///     A DJI drone that writes its own telemetry into the video (Lito X1): the same djmd stream as the Osmo Action's, laid out
///     for an aircraft (DjiMetaTelemetryParser.ParseDrone) - position, height above take-off, speed and exposure, so no flight
///     log is needed. Registered before DjiOsmoFormat, which would otherwise take the file by its djmd stream and read none of it.
///     No accelerometer or aircraft attitude: the stream's quaternion is the gimbal's.
/// </summary>
public sealed class DjiDroneMetaFormat : ICameraFormat
{
	public string Id => "dji-drone-meta";
	public string DisplayName => CoreStrings.DjiDrone_Name;
	public string Brand => "DJI";
	public bool IsDrone => true;
	public string? PreferredPresetId => OverlayPresetStore.DronePresetId;

	public CameraRecording? Detect(string path, JsonArray streams)
	{
		JsonNode? djmd = streams.FirstOrDefault(s => s?["codec_type"]?.GetValue<string>() == "data" && s["codec_tag_string"]?.GetValue<string>() == "djmd");
		return djmd is not null && DjiMetaTelemetryParser.IsDroneLayout(path) ? new CameraRecording(this, djmd["index"]?.GetValue<int>()) : null;
	}

	public TelemetryExtractionResult ExtractTelemetry(string path, SourceInfo source)
	{
		int stream = source.Camera?.TelemetryStream ?? throw new InvalidOperationException("The recording has no djmd stream.");
		return DjiMetaTelemetryParser.ParseDrone(DjiMetaTelemetryParser.ReadRawStream(path, stream), source.Video.Fps);
	}

	public Direction? Gravity(TelemetryFrame frame)
	{
		return null;
	}

	public string DescribeTelemetry(IReadOnlyList<TelemetryFrame> frames)
	{
		return CoreStrings.DjiDroneMeta_Source;
	}
}
