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
    /// Stable identifier for the current run: the run's start time in epoch
    /// seconds, from the game's own run history.
    ///
    /// This survives quitting and resuming - which is exactly what makes
    /// one-file-per-run work across a continue - because the game persists it in
    /// the save rather than regenerating it on load. Falls back to a wall-clock
    /// stamp so a recording always gets a filename even if history is not ready.
    /// </summary>
    internal static string GetRunId()
    {
        try
        {
            long startTime = RunManager.Instance.History?.StartTime ?? 0;
            if (startTime != 0) return startTime.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read run id: {ex.Message}");
        }

        return $"unknown{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    /// <summary>
    /// Reads the run-level metadata. Individual fields degrade to their defaults
    /// rather than failing the whole read.
    /// </summary>
    internal static RunMeta GetRunMeta()
    {
        var meta = new RunMeta();

        try
        {
            var history = RunManager.Instance.History;
            if (history == null) return meta;

            meta.Seed      = history.Seed;
            meta.Ascension = history.Ascension;
            meta.StartTime = history.StartTime;
            meta.GameMode  = history.GameMode.ToString();

            // Character lives on the per-player history record. Singleplayer has
            // exactly one, and this recorder only ever runs in singleplayer.
            var players = history.Players;
            if (players is { Count: > 0 })
                meta.Character = players[0].Character.Entry;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read run metadata: {ex.Message}");
        }

        return meta;
    }

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
    /// </summary>
    internal static RunOutcome GetOutcome()
    {
        bool victory = false;
        bool abandoned = false;

        try
        {
            var run = RunManager.Instance;
            abandoned = run.IsAbandoned;
            victory = run.History?.Win ?? false;
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
    /// Snapshots the full game state using STS2MCP's vendored serializer, so the
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
