using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Recording;

namespace STS2_Recorder.Game;

/// <summary>
/// Read-only accessors over the game's run state.
///
/// Everything here is defensive: these are called from a per-frame callback and
/// from Harmony patches during scene transitions, when <c>RunManager</c> and its
/// members are legitimately half-initialized. A null or a false is always
/// preferable to an exception thrown into game code.
///
/// Must be called on Godot's main thread.
/// </summary>
internal static class RunContext
{
    /// <summary>True when a run is active and not tearing down.</summary>
    internal static bool IsRunActive()
    {
        try
        {
            var run = RunManager.Instance;
            return run is { IsInProgress: true, IsCleaningUp: false };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the active run is co-op.
    ///
    /// This recorder is singleplayer-only: in co-op, player intent is routed
    /// through a dozen separate network synchronizers rather than the local UI
    /// funnel the capture layer hooks, so it would record a partial and
    /// misleading trajectory. Better to record nothing than something wrong.
    /// </summary>
    internal static bool IsMultiplayer()
    {
        try
        {
            var run = RunManager.Instance;
            return run.IsInProgress && run.NetService.Type.IsMultiplayer();
        }
        catch
        {
            // Unknown means "do not record": failing closed keeps a co-op run
            // from being silently mis-recorded as singleplayer.
            return true;
        }
    }

    /// <summary>
    /// The run's start time in epoch seconds, or 0 while it is not yet knowable.
    ///
    /// This is the recorder's run id: the game mints it when a run begins, saves
    /// it, and passes it back unchanged on load, so it is the one value that is
    /// identical for a run and every continue of it.
    ///
    /// It is not readable at run start. <c>RunManager</c> keeps it in a private
    /// field and only publishes it in two places: the <c>SerializableRun</c>
    /// handed to every save, and <c>RunManager.History</c> - which is not the
    /// live run's history but a summary record built when a run *ends*
    /// (<c>OnEnded</c> and the abandon paths call
    /// <c>RunHistoryUtilities.CreateRunHistoryEntry</c>, the only assignment to
    /// it). Reading it during a run therefore yields null, which is why the id
    /// is adopted from the first save instead; see
    /// <see cref="RecordingSession.AdoptRunId"/>.
    /// </summary>
    internal static long GetHistoryStartTime()
    {
        try
        {
            return RunManager.Instance.History?.StartTime ?? 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read run start time: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// The id a run is recorded under until the game reveals its real one.
    ///
    /// A run is recorded from its first frame, but its id only becomes readable
    /// at the first save (a second or two later), so a session needs something
    /// to be called in the meantime. Nothing is written to disk under this id
    /// unless a run somehow ends without ever saving.
    /// </summary>
    internal static string NewProvisionalRunId() =>
        $"pending{DateTime.UtcNow:yyyyMMddHHmmss}";

    /// <summary>Floor number reached, or 0 if unavailable.</summary>
    internal static int GetFloorReached()
    {
        try
        {
            return RunManager.Instance.DebugOnlyGetState()?.TotalFloor ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Builds the outcome record for a run that has just ended.
    ///
    /// <paramref name="victory"/> comes from the game's own run-end call rather
    /// than being read back off <c>History</c>, so it is right even if the
    /// history record is not where this expects it to be.
    /// </summary>
    internal static RunOutcome GetOutcome(bool victory)
    {
        bool abandoned = false;

        try
        {
            // Set before the game kills the party, so it is already true here.
            abandoned = RunManager.Instance.IsAbandoned;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read run outcome: {ex.Message}");
        }

        return new RunOutcome
        {
            Victory      = victory,
            Abandoned    = abandoned,
            FloorReached = GetFloorReached(),
            EndedAt      = RecordingSession.Timestamp(DateTime.UtcNow)
        };
    }

    /// <summary>
    /// Snapshots the full game state using STS2MCP's own serializer, so the
    /// recorded object is identical to its <c>GET /api/v1/singleplayer</c>
    /// response. Returns null if the snapshot fails, which is recorded as a step
    /// with no state rather than dropping the action.
    /// </summary>
    internal static Dictionary<string, object?>? CaptureState()
    {
        try
        {
            return STS2_MCP.McpMod.CaptureSingleplayerState();
        }
        catch (Exception ex)
        {
            Log.Error("State capture failed; recording this step without state", ex);
            return null;
        }
    }
}
