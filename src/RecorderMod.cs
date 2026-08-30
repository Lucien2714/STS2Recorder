using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using STS2_Recorder.Config;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Game;
using STS2_Recorder.Recording;

namespace STS2_Recorder;

/// <summary>
/// Mod entry point.
///
/// Records a human's singleplayer run as a (state, action) trajectory and
/// flushes it to disk whenever the game saves.
///
/// Design constraints, in priority order:
///   1. Never disturb the run being recorded. Every callback reachable from game
///      code is exception-guarded; a failure loses recording fidelity, never the
///      player's progress.
///   2. Match STS2MCP's state output exactly, by compiling its serializer
///      straight from the vendor/STS2MCP submodule (see vendor/README.md)
///      rather than reimplementing it.
///   3. Stay passive. This mod reads and observes; it never enqueues an action
///      or mutates game state.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class RecorderMod
{
    private const string HarmonyId = "com.sts2recorder";

    /// <summary>Version, stamped from mod_manifest.json at build time.</summary>
    internal static readonly string Version = ResolveAssemblyVersion();

    /// <summary>STS2MCP submodule commit the state serializer was built from.</summary>
    internal static readonly string StateBuilderCommit = ResolveStateBuilderCommit();

    private static RecorderConfig? _config;
    private static TrajectoryWriter? _writer;

    /// <summary>The run currently being recorded, or null between runs.</summary>
    internal static RecordingSession? Session { get; private set; }

    /// <summary>
    /// Whether a run was active on the previous frame. Comparing against the
    /// current frame is how run start and run end are detected: the game exposes
    /// no single event that covers starting fresh, resuming a save, dying, and
    /// abandoning alike.
    /// </summary>
    private static bool _runWasActive;

    public static void Initialize()
    {
        try
        {
            _config = RecorderConfig.Load();

            if (!_config.Enabled)
            {
                Log.Info($"v{Version} loaded but disabled in STS2_Recorder.conf; not recording.");
                return;
            }

            _writer = new TrajectoryWriter(_config);

            ApplyHarmonyPatches();
            ConnectFrameCallback();

            Log.Info($"v{Version} recording to {_config.OutputDir}");
            Log.Info($"State serializer built from STS2MCP @ {Shorten(StateBuilderCommit)}");
        }
        catch (Exception ex)
        {
            // A recorder that fails to start must not take the game down with
            // it. Log loudly and leave the player's session untouched.
            Log.Error("Failed to start; no recording will happen this session", ex);
        }
    }

    private static void ApplyHarmonyPatches()
    {
        new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
    }

    private static void ConnectFrameCallback()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(OnProcessFrame));
    }

    /// <summary>
    /// Per-frame run lifecycle check. Cheap by design - two property reads on
    /// the frames where nothing changed.
    /// </summary>
    private static void OnProcessFrame() => Log.Guard("Frame callback", () =>
    {
        bool active = RunContext.IsRunActive();

        if (active && !_runWasActive) OnRunStarted();
        else if (!active && _runWasActive) OnRunEnded();

        _runWasActive = active;
    });

    private static void OnRunStarted()
    {
        if (RunContext.IsMultiplayer())
        {
            Log.Info("Co-op run detected; recording is singleplayer-only, skipping.");
            return;
        }

        if (_writer == null) return;

        // Still holding an open session means no run end was observed since it
        // was created, so this can only be the same run becoming active again.
        // Keeping it preserves one file across that hop rather than starting a
        // second one.
        if (Session is { IsClosed: false } existing)
        {
            existing.MarkResumed();
            Log.Info($"Run {existing.RunId} resumed; continuing {existing.FileName}");
            return;
        }

        // The run has no readable id yet - the game only publishes it in the
        // save - so the session starts under a placeholder and adopts the real
        // one at the first save, before anything is written. See
        // RecordingSession.AdoptRunId.
        // Run metadata starts empty for the same reason the id does: nothing on
        // the live RunManager describes this run yet. The save hook fills both
        // in. See RunSaveManagerSaveRunPatch.
        Session = new RecordingSession(
            _writer,
            RunContext.NewProvisionalRunId(),
            new RunMeta(),
            DateTime.UtcNow,
            Version,
            StateBuilderCommit);

        Log.Info($"Recording a new run to {Session.FileName} (id pending first save)");
    }

    /// <summary>
    /// Closes the recording of a run that has just ended, whether by victory,
    /// death, or abandon. Called from the run-end hook while the run is still
    /// intact, so the terminal step holds the real final state.
    /// </summary>
    internal static void FinishRun(bool victory)
    {
        if (Session is not { IsClosed: false } session) return;

        // A run that ended without ever saving still has no id, but the game
        // built its history record on the way into this call, so the start time
        // is readable now. This is the last chance to name the file correctly.
        session.AdoptRunId(RunContext.GetHistoryStartTime());
        session.Close(RunContext.CaptureState(), RunContext.GetOutcome(victory));

        Log.Info($"Run {session.RunId} ended after {session.Trajectory.Steps.Count} step(s).");
        Session = null;
    }

    /// <summary>
    /// Handles the run going inactive. Anything still open here was left rather
    /// than ended - a save-and-quit to the menu - because a real ending closes
    /// the session from the run-end hook first.
    /// </summary>
    private static void OnRunEnded()
    {
        if (Session is not { IsClosed: false } session)
        {
            Session = null;
            return;
        }

        // No outcome and no terminal step: the run is unfinished and can be
        // continued later. Writing one here would record the main menu as the
        // player's final state and invent an ending the run never had.
        session.AdoptRunId(RunContext.GetHistoryStartTime());
        session.Close(finalState: null, outcome: null);

        Log.Info($"Run {session.RunId} left with {session.Trajectory.Steps.Count} step(s) " +
                 "and no outcome; continuing it will record to a new file with the same id.");
        Session = null;
    }

    /// <summary>
    /// Stamps the outcome onto the recording of a run abandoned from the main
    /// menu, where there is no live run and so no session to close.
    ///
    /// The recording was already closed without an outcome when the player quit
    /// to the menu, so this reopens that file by run id and finishes it. Does
    /// nothing if the run was never recorded, or if its file already has an
    /// outcome.
    /// </summary>
    internal static void RecordAbandonedFromMenu(long runStartTime, int floorReached) =>
        Log.Guard("Menu abandon", () =>
        {
            if (_writer == null || runStartTime <= 0) return;

            string runId = runStartTime.ToString(CultureInfo.InvariantCulture);

            string? path = _writer.FindExisting(runId);
            if (path == null)
            {
                Log.Info($"Run {runId} was abandoned from the menu but was never recorded.");
                return;
            }

            var trajectory = _writer.Read(path);
            if (trajectory == null || trajectory.Outcome != null) return;

            trajectory.Outcome = new RunOutcome
            {
                Victory      = false,
                Abandoned    = true,
                FloorReached = floorReached,
                EndedAt      = RecordingSession.Timestamp(DateTime.UtcNow)
            };
            trajectory.LastSavedAt = trajectory.Outcome.EndedAt;

            string fileName = Path.GetFileName(path);
            if (_writer.Write(trajectory, fileName) != null)
                Log.Info($"Run {runId} abandoned from the menu; outcome recorded to {fileName}.");
        });

    /// <summary>
    /// Flushes the active recording. Called when the game saves.
    /// </summary>
    internal static void FlushActiveSession() =>
        Log.Guard("Flush", () => Session?.Flush());

    /// <summary>
    /// Appends a decision point to the active recording. No-op when no run is
    /// being recorded, so capture hooks can call unconditionally.
    ///
    /// The state is snapshotted here, before the action reaches the game, and
    /// handed to <paramref name="describe"/> so a hook can derive its arguments
    /// from the very state it is paired with - the played card's position in the
    /// recorded hand, the target's id as the recorded enemy list spells it. That
    /// is what makes a step replayable: the arguments mean what they say
    /// relative to the state beside them.
    ///
    /// Returning null from <paramref name="describe"/> records nothing, which is
    /// how hooks pass on actions that are not a player decision. Building the
    /// state is not cheap, so it happens only once a session is known to be
    /// listening.
    /// </summary>
    internal static void RecordAction(Func<Dictionary<string, object?>?, RecordedAction?> describe) =>
        Log.Guard("Record action", () =>
        {
            if (Session is not { IsClosed: false } session) return;

            var state = RunContext.CaptureState();

            var action = describe(state);
            if (action == null) return;

            session.RecordStep(state, action);
        });

    /// <summary>
    /// Drops the last recorded step if it recorded <paramref name="action"/>,
    /// for a decision the player took back before it resolved.
    /// </summary>
    internal static void RetractAction(string action) => Log.Guard("Retract action", () =>
    {
        if (Session is not { IsClosed: false } session) return;

        if (session.RetractLastStep(action))
            Log.Info($"Retracted a recorded {action}; it was taken back before it resolved.");
    });

    private static string ResolveAssemblyVersion()
    {
        var attr = (AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
            typeof(RecorderMod).Assembly, typeof(AssemblyInformationalVersionAttribute));

        string? info = attr?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return typeof(RecorderMod).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string ResolveStateBuilderCommit()
    {
        foreach (var attr in typeof(RecorderMod).Assembly
                     .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attr.Key == "StateBuilderCommit" && !string.IsNullOrEmpty(attr.Value))
                return attr.Value;
        }

        return "unknown";
    }

    private static string Shorten(string commit) =>
        commit.Length > 7 ? commit[..7] : commit;
}
