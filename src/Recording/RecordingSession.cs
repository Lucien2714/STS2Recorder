using System;
using System.Collections.Generic;
using System.Globalization;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Recording;

/// <summary>
/// One run being recorded: the in-memory trajectory plus the fixed filename it
/// flushes to.
///
/// Lives entirely on Godot's main thread - actions are captured from Harmony
/// patches and flushes are driven from the per-frame callback, both of which run
/// there. Nothing here is thread-safe, and nothing needs to be.
/// </summary>
internal sealed class RecordingSession
{
    private readonly TrajectoryWriter _writer;

    internal string RunId { get; }
    internal string FileName { get; }
    internal TrajectoryFile Trajectory { get; }

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
        _writer  = writer;
        RunId    = runId;
        FileName = TrajectoryWriter.BuildFileName(startedAtUtc, runId);

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
    /// Closes the run with a terminal step holding the final state and no
    /// action, then flushes. Idempotent.
    /// </summary>
    internal void Close(Dictionary<string, object?>? finalState, RunOutcome outcome)
    {
        if (IsClosed) return;

        RecordStep(finalState, action: null);
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
