namespace Telemavix.Core.Telemetry.External;

/// <summary>Who owns the position, altitude, speed and time of a frame when a track is put on a recording.</summary>
public enum ExternalTrackMergeMode
{
	/// <summary>The track's position, altitude, speed and time replace the camera's wherever the track covers the frame (a camera without GPS, or a better receiver beside it).</summary>
	Replace,

	/// <summary>The recording's own telemetry stays; the track fills only what the camera lacks (a frame without a fix, the phone's readings, battery, heart rate, cadence).</summary>
	Supplement
}
