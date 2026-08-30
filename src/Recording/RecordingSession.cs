using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Recording;

/// <summary>
/// One run being recorded: the in-memory trajectory plus the filename it flushes
/// to, which is fixed once the run's id is known.
///
/// Lives entirely on Godot's main thread - actions are captured from Harmony
/// patches and flushes are driven from the per-frame callback, both of which run
/// there. Nothing here is thread-safe, and nothing needs to be.
/// </summary>
internal sealed class RecordingSession
{
    private readonly TrajectoryWriter _writer;
    private readonly DateTime _startedAtUtc;

    internal string RunId { get; private set; }
    internal string FileName { get; private set; }
    internal TrajectoryFile Trajectory { get; private set; }

    /// <summary>
    /// True until the game reveals the run's real id; see
    /// <see cref="AdoptRunId"/>.
    /// </summary>
    internal bool HasProvisionalRunId { get; private set; } = true;

    /// <summary>Set once the run ends; further actions are ignored.</summary>
    internal bool IsClosed { get; private set; }

    /// <summary>
    /// True when the trajectory has changed since the last successful flush.
    /// Lets a save event skip rewriting a file that would be byte-identical.
    /// </summary>
    private bool _dirty = true;

    /// <summary>Applied to the next recorded step; see TrajectoryStep.Resumed.</summary>
    private bool _pendingResumeMark;

    internal RecordingSession(
        TrajectoryWriter writer,
        string runId,
        RunMeta meta,
        DateTime startedAtUtc,
        string recorderVersion,
        string stateBuilderCommit)
    {
        _writer       = writer;
        _startedAtUtc = startedAtUtc;
        RunId         = runId;
        FileName      = TrajectoryWriter.BuildFileName(startedAtUtc, runId);

        Trajectory = new TrajectoryFile
        {
            RecorderVersion    = recorderVersion,
            StateBuilderCommit = stateBuilderCommit,
            RunId              = runId,
            Run                = meta,
            StartedAt          = Timestamp(startedAtUtc)
        };
    }

    internal static string Timestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// Replaces the provisional id with the run's real one - its start time in
    /// epoch seconds - once the game reveals it, and rebuilds the filename to
    /// match.
    ///
    /// A run has no readable id at the moment it starts (see
    /// <c>RunContext.GetHistoryStartTime</c>), so a session is created with a
    /// placeholder and adopts the real value from the first save. No file is
    /// renamed on disk: nothing reaches disk before the first save, because
    /// flushes only happen on a save or at run end, and both adopt first. The
    /// <see cref="TrajectoryFile.SaveCount"/> guard makes that ordering explicit
    /// rather than assumed - a file already written keeps the name it was
    /// written under, so a rename can never orphan a recording.
    ///
    /// Ignores a zero or negative <paramref name="startTime"/>, which is how the
    /// game reports "not known yet".
    /// </summary>
    internal void AdoptRunId(long startTime)
    {
        if (!HasProvisionalRunId || startTime <= 0) return;

        Trajectory.Run.StartTime = startTime;

        if (Trajectory.SaveCount > 0)
        {
            // Already on disk under the provisional name. Record the real id
            // inside the file, but leave the filename alone.
            Trajectory.RunId = startTime.ToString(CultureInfo.InvariantCulture);
            HasProvisionalRunId = false;
            _dirty = true;
            Log.Warn($"Run id resolved to {Trajectory.RunId} after the first write; " +
                     $"{FileName} keeps its provisional name.");
            return;
        }

        RunId    = startTime.ToString(CultureInfo.InvariantCulture);
        FileName = TrajectoryWriter.BuildFileName(_startedAtUtc, RunId);

        Trajectory.RunId    = RunId;
        HasProvisionalRunId = false;
        _dirty = true;

        if (TryContinueExistingRecording()) return;

        Log.Info($"Run id resolved to {RunId}; recording to {FileName}");
    }

    /// <summary>
    /// Picks up the run's existing recording, if it has one, so a run played
    /// across several sittings lands in a single file.
    ///
    /// Quitting to the main menu ends the session but not the run: the game
    /// keeps the save, and continuing later starts a fresh session for the same
    /// run. Knowing the run's id is what makes the two recognizable as one run,
    /// so this runs the moment the id is adopted - before anything of this
    /// session has been written.
    ///
    /// Declines to continue a file whose steps were produced by a different
    /// build, because a recording is only interpretable if every state in it
    /// came from one serializer, and one that already carries an outcome, since
    /// a finished run must never be reopened. Either way the session keeps its
    /// own new file rather than losing the run.
    /// </summary>
    private bool TryContinueExistingRecording()
    {
        string? path = _writer.FindExisting(RunId);
        if (path == null) return false;

        var previous = _writer.Read(path);
        if (previous == null) return false;

        string reason =
            previous.Outcome != null                                       ? "it is already finished"
            : previous.SchemaVersion != Trajectory.SchemaVersion           ? "it uses a different schema version"
            : previous.RecorderVersion != Trajectory.RecorderVersion       ? $"it was recorded by v{previous.RecorderVersion}"
            : previous.StateBuilderCommit != Trajectory.StateBuilderCommit ? "its states came from a different STS2MCP build"
            : "";

        if (reason != "")
        {
            Log.Warn($"Not continuing {Path.GetFileName(path)} because {reason}; " +
                     $"this sitting is recorded to {FileName} instead.");
            return false;
        }

        var carried = Trajectory.Steps;
        int alreadyRecorded = previous.Steps.Count;

        Trajectory = previous;
        FileName   = Path.GetFileName(path);
        Trajectory.ResumeCount++;

        // Anything captured between this sitting starting and its id resolving
        // belongs at the end of the file being continued, renumbered onto it.
        for (int i = 0; i < carried.Count; i++)
            Trajectory.Steps.Add(new TrajectoryStep
            {
                Index   = Trajectory.Steps.Count,
                T       = carried[i].T,
                State   = carried[i].State,
                Action  = carried[i].Action,
                Resumed = i == 0 ? true : carried[i].Resumed
            });

        // With nothing carried over, the resume mark lands on the next step
        // recorded instead.
        _pendingResumeMark = carried.Count == 0;
        _dirty = true;

        Log.Info($"Run {RunId} continues {FileName} " +
                 $"({alreadyRecorded} step(s) already recorded).");
        return true;
    }

    /// <summary>
    /// Records a decision point. <paramref name="state"/> must have been
    /// captured before the action took effect.
    /// </summary>
    internal void RecordStep(Dictionary<string, object?>? state, RecordedAction? action)
    {
        if (IsClosed) return;

        Trajectory.Steps.Add(new TrajectoryStep
        {
            Index   = Trajectory.Steps.Count,
            T       = Timestamp(DateTime.UtcNow),
            State   = state,
            Action  = action,
            Resumed = _pendingResumeMark ? true : null
        });

        _pendingResumeMark = false;
        _dirty = true;
    }

    /// <summary>
    /// Notes that the run was resumed from a save. The next recorded step is
    /// marked, and the count surfaces in the file header.
    /// </summary>
    internal void MarkResumed()
    {
        if (IsClosed) return;

        Trajectory.ResumeCount++;
        _pendingResumeMark = true;
        _dirty = true;
    }

    /// <summary>
    /// Closes the run and flushes. Idempotent.
    ///
    /// A terminal step - the final state, with no action after it - is added
    /// only when there is something to record: a run that ended has both a state
    /// and an <paramref name="outcome"/>, while one merely left for the main
    /// menu has neither, and gets closed exactly as it stood.
    /// </summary>
    internal void Close(Dictionary<string, object?>? finalState, RunOutcome? outcome)
    {
        if (IsClosed) return;

        if (finalState != null || outcome != null) RecordStep(finalState, action: null);
        Trajectory.Outcome = outcome;
        IsClosed = true;
        _dirty = true;

        Flush(force: true);
    }

    /// <summary>
    /// Writes the trajectory to disk. Skipped when nothing changed since the
    /// last flush unless <paramref name="force"/> is set.
    /// </summary>
    internal void Flush(bool force = false)
    {
        if (!_dirty && !force) return;

        Trajectory.LastSavedAt = Timestamp(DateTime.UtcNow);
        Trajectory.SaveCount++;

        string? path = _writer.Write(Trajectory, FileName);
        if (path == null)
        {
            // Write failed and has been logged. Stay dirty so the next save
            // retries rather than silently dropping the run.
            Trajectory.SaveCount--;
            return;
        }

        _dirty = false;
        Log.Info($"Recorded {Trajectory.Steps.Count} step(s) to {FileName}");
    }
}
