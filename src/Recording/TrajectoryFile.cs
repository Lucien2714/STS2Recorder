using System.Collections.Generic;

namespace STS2_Recorder.Recording;

/// <summary>
/// The on-disk shape of one recorded run: <c>run_&lt;date&gt;_&lt;time&gt;_&lt;runid&gt;.json</c>.
///
/// One file per run, rewritten in full each time the game saves. Property names
/// are serialized to snake_case by the shared serializer options.
///
/// Schema changes are breaking for anything consuming these files, so bump
/// <see cref="SchemaVersion"/> and record the change in docs/recording-format.md.
/// </summary>
internal sealed class TrajectoryFile
{
    /// <summary>Incremented on any breaking change to this shape.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Version of this mod, from mod_manifest.json via the assembly.</summary>
    public string RecorderVersion { get; init; } = "";

    /// <summary>
    /// The STS2MCP commit whose <c>BuildGameState()</c> produced every
    /// <see cref="TrajectoryStep.State"/> below. Without this a dataset
    /// collected across a schema change is uninterpretable.
    /// </summary>
    public string StateBuilderCommit { get; init; } = "";

    /// <summary>
    /// Stable per-run identifier - the run's start time in epoch seconds - which
    /// is also the filename suffix. Settable because a run has no readable id
    /// until its first save; see <see cref="RecordingSession.AdoptRunId"/>.
    /// </summary>
    public string RunId { get; set; } = "";

    public RunMeta Run { get; init; } = new();

    /// <summary>UTC ISO-8601 timestamp of when recording began.</summary>
    public string StartedAt { get; init; } = "";

    /// <summary>UTC ISO-8601 timestamp of the most recent flush.</summary>
    public string LastSavedAt { get; set; } = "";

    /// <summary>How many game-save events have flushed this file.</summary>
    public int SaveCount { get; set; }

    /// <summary>
    /// How many times this run was resumed from a save while being recorded.
    /// Steps either side of a resume are contiguous in <see cref="Steps"/>;
    /// the step after one carries <c>resumed = true</c>.
    /// </summary>
    public int ResumeCount { get; set; }

    /// <summary>
    /// The trajectory, in the order the player acted. Present even when empty so
    /// consumers never have to null-check.
    /// </summary>
    public List<TrajectoryStep> Steps { get; init; } = [];

    /// <summary>Null until the run ends; set on victory, death, or abandon.</summary>
    public RunOutcome? Outcome { get; set; }
}

/// <summary>Run-level facts that do not change once the run starts.</summary>
internal sealed class RunMeta
{
    public string? Character { get; set; }
    public string? Seed { get; set; }
    public int Ascension { get; set; }
    public string? GameMode { get; set; }

    /// <summary>
    /// The game's own reload counter. A run reloaded mid-combat can replay the
    /// same decision point twice; consumers training on this data usually want
    /// to know that happened.
    /// </summary>
    public int NumReloads { get; set; }

    /// <summary>
    /// Epoch seconds when the run began, from the game's own save. Identical to
    /// <see cref="TrajectoryFile.RunId"/>; 0 only if a run ended before it ever
    /// saved.
    /// </summary>
    public long StartTime { get; set; }
}

/// <summary>
/// One decision point: the state the player was looking at, and what they did
/// about it.
/// </summary>
internal sealed class TrajectoryStep
{
    /// <summary>Zero-based position in the trajectory.</summary>
    public int Index { get; init; }

    /// <summary>UTC ISO-8601 timestamp of the action.</summary>
    public string T { get; init; } = "";

    /// <summary>
    /// Verbatim <c>BuildGameState()</c> output, captured immediately before the
    /// action was applied - i.e. the state the decision was made from, not its
    /// result. Identical in shape to STS2MCP's <c>GET /api/v1/singleplayer</c>.
    /// </summary>
    public Dictionary<string, object?>? State { get; init; }

    /// <summary>
    /// What the player did. Null only on the terminal step, which records the
    /// final state with no action following it.
    /// </summary>
    public RecordedAction? Action { get; init; }

    /// <summary>True on the first step recorded after resuming from a save.</summary>
    public bool? Resumed { get; init; }
}

/// <summary>
/// A player action, named and shaped to match STS2MCP's action API so a
/// recorded step can be replayed through <c>POST /api/v1/singleplayer</c>.
/// </summary>
internal sealed class RecordedAction
{
    /// <summary>e.g. <c>play_card</c>, <c>end_turn</c>, <c>choose_map_node</c>.</summary>
    public string Action { get; init; } = "";

    /// <summary>
    /// Action arguments, using STS2MCP's parameter names (<c>card_index</c>,
    /// <c>target</c>, <c>index</c>, ...). Empty for parameterless actions.
    /// </summary>
    public Dictionary<string, object?> Args { get; init; } = [];

    /// <summary>
    /// Human-readable description of what was acted on ("Strike", "Rest").
    /// Not needed to replay the action; invaluable when reading a file by eye.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>How the run ended.</summary>
internal sealed class RunOutcome
{
    public bool Victory { get; init; }
    public bool Abandoned { get; init; }
    public int FloorReached { get; init; }
    public string EndedAt { get; init; } = "";
}
