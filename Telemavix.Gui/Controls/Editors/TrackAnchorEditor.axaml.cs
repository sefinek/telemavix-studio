using System.Globalization;
using Avalonia.Controls;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Gui;

/// <summary>
///     One synchronization point of an external track: a moment of the video (seconds from the start of the whole recording) and the
///     track's UTC time of the same event - typed, or picked from the track's own fixes.
/// </summary>
public partial class TrackAnchorEditor : UserControl
{
	private double _previewSeconds;

	/// <summary>The video time or the track's time was changed - typed, picked or set from code.</summary>
	public event Action? Changed;

	public TrackAnchorEditor()
	{
		InitializeComponent();
		CurrentFrameButton.Click += (_, _) => VideoSeconds = _previewSeconds;
		VideoBox.ValueChanged += (_, _) => Changed?.Invoke();
		UtcBox.TextChanged += (_, _) => Changed?.Invoke();
		PointBox.SelectionChanged += (_, _) =>
		{
			if (PointBox.SelectedItem is TrackPointChoice choice) Utc = choice.Point.Utc;
		};
	}

	/// <summary>The latest moment of the video the point can be at, and the step of its box (one frame).</summary>
	public void SetVideoRange(double lastSeconds, double framesPerSecond, double previewSeconds)
	{
		VideoBox.Maximum = (decimal)lastSeconds;
		VideoBox.Increment = framesPerSecond > 0 ? (decimal)(1 / framesPerSecond) : 0.04m;
		_previewSeconds = Math.Clamp(previewSeconds, 0, lastSeconds);
	}

	public void SetTrackPoints(IEnumerable<ExternalTrackPoint> points)
	{
		PointBox.ItemsSource = points.Select(p => new TrackPointChoice(p)).ToList();
	}

	public double VideoSeconds
	{
		get => (double)(VideoBox.Value ?? 0);
		set => VideoBox.Value = (decimal)Math.Clamp(value, 0, (double)VideoBox.Maximum);
	}

	public double MaxVideoSeconds => (double)VideoBox.Maximum;

	public DateTime Utc
	{
		set => UtcBox.Text = value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
	}

	/// <summary>The point as typed - InvalidDataException with a message for the user when the time or the date doesn't read.</summary>
	public TrackSyncAnchor Read()
	{
		return TryRead() ?? throw new InvalidDataException(Strings.Track_InvalidTime);
	}

	/// <summary>The point as typed, or null while it doesn't read - for what runs on every keystroke, where a thrown exception would be the usual case.</summary>
	public TrackSyncAnchor? TryRead()
	{
		return !DataValidationErrors.GetHasErrors(VideoBox) && VideoBox.Value is { } seconds &&
		       TrackSyncAnchor.TryParse($"{seconds.ToString(CultureInfo.InvariantCulture)}={UtcBox.Text}", out TrackSyncAnchor? anchor)
			? anchor
			: null;
	}

	private sealed record TrackPointChoice(ExternalTrackPoint Point)
	{
		public override string ToString() => $"{Point.Utc:HH:mm:ss} UTC" + (Point.SpeedMs is { } speed ? $" · {speed * 3.6:0.0} km/h" : "");
	}
}
