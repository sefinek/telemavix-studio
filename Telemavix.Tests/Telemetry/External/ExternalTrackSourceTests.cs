using Telemavix.Core;
using Telemavix.Core.Ffmpeg;
using Telemavix.Core.Telemetry;
using Telemavix.Core.Telemetry.External;

namespace Telemavix.Tests.Telemetry.External;

[TestClass]
public sealed class ExternalTrackSourceTests
{
	private static readonly DateTime TrackStart = new(2026, 10, 21, 14, 20, 49, DateTimeKind.Utc);
	private const double Fps = 25;

	private static string WriteTrack(string directory)
	{
		FitBuilder builder = new FitBuilder().DefineRecord();
		for (int s = 0; s < 60; s++) builder.Record(TrackStart.AddSeconds(s), 53.1 + s * 5 * 9e-6, 18.9, 80, 5);

		string path = Path.Combine(directory, "ride.fit");
		File.WriteAllBytes(path, builder.ToArray());
		return path;
	}

	private static List<TelemetryFrame> Frames()
	{
		return [.. Enumerable.Range(0, (int)(20 * Fps)).Select(f => new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false))];
	}

	[TestMethod]
	public void Apply_AnAlreadyLoadedTrackIsNotReadAgain()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			string path = WriteTrack(directory);
			var source = new ExternalTrackSource(path, Synchronization: new TrackSynchronization(new TrackSyncAnchor(0, TrackStart)));
			List<TelemetryFrame> frames = Frames();
			var summary = new FileSummary(["synthetic.mp4"], [20], [frames.Count], null,
				new VideoInfo("hevc", "Main", 640, 360, "25", "yuv420p", null, null, null, null, 1_000_000),
				null, 20, 0, true, frames, null, null, frames.Count);
			ExternalTrack loaded = ExternalTrackReader.Read(path);
			FileSummary expected = source.Apply(summary);
			File.Delete(path);

			FileSummary actual = source.Apply(summary, loaded);

			CollectionAssert.AreEqual(expected.TelemetryFrames!.ToArray(), actual.TelemetryFrames!.ToArray());
			CollectionAssert.AreEqual(expected.DerivedFrames!.ToArray(), actual.DerivedFrames!.ToArray());
			Assert.AreEqual(expected.Telemetry, actual.Telemetry);
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_AGivenOffsetIsHowFarTheTracksClockIsAheadOfTheCameras()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			// The camera said 14:27:45 at the first frame, the track's clock 14:20:59 then: -406 s.
			DateTime cameraStart = new(2026, 10, 21, 14, 27, 45, DateTimeKind.Utc);
			var source = new ExternalTrackSource(WriteTrack(directory), (TrackStart.AddSeconds(10) - cameraStart).TotalSeconds);

			IReadOnlyList<TelemetryFrame> frames = source.Apply(Frames(), Fps, cameraStart);

			Assert.IsTrue(frames[0].HasGpsFix);
			Assert.AreEqual(53.1 + 10 * 5 * 9e-6, frames[0].Latitude, 1e-6);
			Assert.AreEqual(TrackStart.AddSeconds(10), frames[0].GpsTimestamp);
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_AnAnchorWorksWithoutTheRecordingsStartTime()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory), Synchronization: new TrackSynchronization(new TrackSyncAnchor(5, TrackStart.AddSeconds(15))));
			IReadOnlyList<TelemetryFrame> frames = source.Apply(Frames(), Fps, null);
			Assert.AreEqual(TrackStart.AddSeconds(10), frames[0].GpsTimestamp);
			Assert.AreEqual(TrackStart.AddSeconds(15), frames[125].GpsTimestamp);
			Assert.IsTrue(frames[0].HasGpsFix);
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_RejectsAnAlignmentOutsideTheTrack()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory), Synchronization: new TrackSynchronization(new TrackSyncAnchor(0, TrackStart.AddMinutes(5))));
			Assert.ThrowsExactly<InvalidOperationException>(() => source.Apply(Frames(), Fps, null));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Apply_AnOffsetNeedsTheRecordingsStartTime()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory), -406);

			Assert.ThrowsExactly<InvalidOperationException>(() => source.Apply(Frames(), Fps, null));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void Propose_WithoutMatchingMotionFallsBackToTheCameraClockAndAsksForVerification()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			ExternalTrack track = ExternalTrackReader.Read(WriteTrack(directory));
			TrackAlignment? proposed = ExternalTrackSource.Propose(Frames(), Fps, TrackStart.AddSeconds(5), track);

			Assert.AreEqual(TrackAlignmentMethod.ContainerClock, proposed?.Method);
			Assert.IsTrue(proposed!.NeedsVerification);
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	private static List<TelemetryFrame> FramesOf(double seconds)
	{
		return [.. Enumerable.Range(0, (int)(seconds * Fps)).Select(f => new TelemetryFrame(f, f / Fps, 0, 0, 0, null, 0, 0, 1, HasGpsFix: false))];
	}

	[TestMethod]
	public void OverlappingClockAnchor_ARecordingStartingASecondBeforeTheTrackIsPlacedOnTheClock()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			ExternalTrack track = ExternalTrackReader.Read(WriteTrack(directory));
			DateTime start = TrackStart.AddSeconds(-1);

			Assert.AreEqual(start, ExternalTrackSource.OverlappingClockAnchor(Frames(), Fps, track, start));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void OverlappingClockAnchor_NeedsAtLeastTwentySecondsOfOverlapOrAllOfAShorterRecording()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			ExternalTrack track = ExternalTrackReader.Read(WriteTrack(directory));
			List<TelemetryFrame> hundredSeconds = FramesOf(100);

			Assert.IsNotNull(ExternalTrackSource.OverlappingClockAnchor(hundredSeconds, Fps, track, TrackStart.AddSeconds(-30)), "the camera outlived the track: all 59 s of it overlap");
			Assert.IsNotNull(ExternalTrackSource.OverlappingClockAnchor(hundredSeconds, Fps, track, TrackStart.AddSeconds(-80)), "20 s overlap is enough");
			Assert.IsNull(ExternalTrackSource.OverlappingClockAnchor(hundredSeconds, Fps, track, TrackStart.AddSeconds(-81)), "under 20 s says nothing");
			Assert.IsNull(ExternalTrackSource.OverlappingClockAnchor(hundredSeconds, Fps, track, TrackStart.AddSeconds(41)), "the camera started 18 s before the track ended");
			Assert.IsNotNull(ExternalTrackSource.OverlappingClockAnchor(FramesOf(15), Fps, track, TrackStart.AddSeconds(49)), "a short clip needs its length less the slack (5 s), not 20 s");
			Assert.IsNull(ExternalTrackSource.OverlappingClockAnchor(FramesOf(15), Fps, track, TrackStart.AddSeconds(54.5)), "under 5 s inside");
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void OverlappingClockAnchor_CountsBackFromTheFirstFramesOwnTime()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			ExternalTrack track = ExternalTrackReader.Read(WriteTrack(directory));
			List<TelemetryFrame> frames = [.. Frames().Select(f => f with { SampleTimeSeconds = f.SampleTimeSeconds + 4 })];

			Assert.AreEqual(TrackStart.AddSeconds(1), ExternalTrackSource.OverlappingClockAnchor(frames, Fps, track, TrackStart.AddSeconds(5)));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}

	[TestMethod]
	public void ShiftOf_IsHowMuchLaterTheManualStartLiesOnTheTrackThanTheProposal()
	{
		var proposal = new TrackAlignment(TrackStart.AddSeconds(-1), TrackAlignmentMethod.ContainerClock);

		Assert.AreEqual(0, proposal.ShiftOf(new TrackSynchronization(new TrackSyncAnchor(0, TrackStart.AddSeconds(-1)))), 1e-9);
		Assert.AreEqual(7, proposal.ShiftOf(new TrackSynchronization(new TrackSyncAnchor(0, TrackStart.AddSeconds(6)))), 1e-9);
		Assert.AreEqual(7, proposal.ShiftOf(new TrackSynchronization(new TrackSyncAnchor(78, TrackStart.AddSeconds(84)))), 1e-9, "an anchor later in the video counts back to the first frame");
		Assert.AreEqual(-3, proposal.ShiftOf(new TrackSynchronization(new TrackSyncAnchor(0, TrackStart.AddSeconds(-4)))), 1e-9);
	}

	[TestMethod]
	public void Apply_WithNoWayToMatchItIsAnErrorThatNamesTheFile()
	{
		string directory = Directory.CreateTempSubdirectory().FullName;
		try
		{
			var source = new ExternalTrackSource(WriteTrack(directory));

			InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => source.Apply(Frames(), Fps, TrackStart.AddMinutes(7)));

			Assert.IsTrue(error.Message.Contains("ride.fit"));
		}
		finally
		{
			Directory.Delete(directory, true);
		}
	}
}
