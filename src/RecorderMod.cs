using System;
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
///      verbatim (see vendor/README.md) rather than reimplementing it.
///   3. Stay passive. This mod reads and observes; it never enqueues an action
///      or mutates game state.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class RecorderMod
{
    private const string HarmonyId = "com.sts2recorder";

    /// <summary>Version, stamped from mod_manifest.json at build time.</summary>
    internal static readonly string Version = ResolveAssemblyVersion();

    /// <summary>STS2MCP commit that produced the vendored state serializer.</summary>
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
            Log.Info($"State serializer vendored from STS2MCP @ {Shorten(StateBuilderCommit)}");
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

        string runId = RunContext.GetRunId();

        // Resuming a save re-enters this path for a run we may already hold.
        // Keeping the existing session preserves one-file-per-run across a
        // quit-and-continue, which is the whole point of keying on the game's
        // own run id rather than the wall clock.
        if (Session is { IsClosed: false } existing && existing.RunId == runId)
        {
            existing.MarkResumed();
            Log.Info($"Resumed run {runId}; continuing {existing.FileName}");
            return;
        }

        Session = new RecordingSession(
            _writer,
            runId,
            RunContext.GetRunMeta(),
            DateTime.UtcNow,
            Version,
            StateBuilderCommit);

        Log.Info($"Recording run {runId} to {Session.FileName}");
    }

    private static void OnRunEnded()
    {
        if (Session is not { IsClosed: false } session) return;

        // The final state is captured before RunManager finishes tearing down,
        // so a game-over screen is still readable here.
        session.Close(RunContext.CaptureState(), RunContext.GetOutcome());

        Log.Info($"Run {session.RunId} ended after {session.Trajectory.Steps.Count} step(s).");
        Session = null;
    }

    /// <summary>
    /// Flushes the active recording. Called when the game saves.
    /// </summary>
    internal static void FlushActiveSession() =>
        Log.Guard("Flush", () => Session?.Flush());

    /// <summary>
    /// Appends a decision point to the active recording. No-op when no run is
    /// being recorded, so capture hooks can call unconditionally.
    /// </summary>
    internal static void RecordAction(RecordedAction action) => Log.Guard("Record action", () =>
    {
        if (Session is not { IsClosed: false } session) return;

        session.RecordStep(RunContext.CaptureState(), action);
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
