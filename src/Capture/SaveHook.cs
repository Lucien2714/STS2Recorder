using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Recording;

namespace STS2_Recorder.Capture;

/// <summary>
/// Flushes the recording whenever the game writes a run save.
///
/// <c>RunSaveManager.SaveRun(SerializableRun, bool)</c> is the single point every
/// run save funnels through - the <c>SaveRun(AbstractRoom)</c> overload builds a
/// <see cref="SerializableRun"/> and delegates here - so one patch covers all of
/// them. It is preferred over the public <c>SaveManager.Saved</c> event because
/// it also hands over the run being saved, letting the recorder mirror the
/// game's own view of reload count and progress.
///
/// Postfix rather than prefix: flushing after the game has committed its save
/// keeps the recording from ever being ahead of the save it claims to accompany.
///
/// The save also carries the two run-level values the live <c>RunManager</c>
/// does not expose - the run's start time (its id) and the reload count - so
/// this is where both are mirrored onto the recording.
/// </summary>
[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun),
    [typeof(SerializableRun), typeof(bool)])]
internal static class RunSaveManagerSaveRunPatch
{
    private static void Postfix(SerializableRun save, bool isMultiplayer)
    {
        // Co-op saves are not recorded; see RunContext.IsMultiplayer.
        if (isMultiplayer) return;

        Log.Guard("Save hook", () =>
        {
            var session = RecorderMod.Session;
            if (session is not { IsClosed: false }) return;

            // The save is the first place the run's own id becomes readable, so
            // the session adopts it here - before the flush below, so the file
            // is written under its real name from the very first write.
            session.AdoptRunId(save.StartTime);

            MirrorRunMeta(session.Trajectory.Run, save);

            RecorderMod.FlushActiveSession();
        });
    }

    /// <summary>
    /// Copies the run's fixed facts off the save.
    ///
    /// None of these are readable from the live <c>RunManager</c>: its
    /// <c>History</c> is built only when a run ends, and because
    /// <c>RunManager.Instance</c> is a process-wide singleton that history is
    /// never cleared - so reading it at run start returned the *previous* run's
    /// character and seed, silently attributing them to this one. The save is
    /// the only source that always describes the run in hand.
    ///
    /// Re-read on every save rather than just the first: it is a handful of
    /// field copies, and it keeps the recording right if the game ever revises
    /// one of them mid-run.
    /// </summary>
    private static void MirrorRunMeta(RunMeta meta, SerializableRun save)
    {
        // The game's own reload counter. A run reloaded mid-combat replays a
        // decision point, and consumers of this data need to know that.
        meta.NumReloads = save.NumReloads;
        meta.Ascension  = save.Ascension;
        meta.GameMode   = save.GameMode.ToString();
        meta.Seed       = save.SerializableRng?.Seed;

        // Singleplayer has exactly one player, and this recorder only ever runs
        // in singleplayer.
        var players = save.Players;
        if (players is { Count: > 0 })
            meta.Character = players[0]?.CharacterId?.Entry;
    }
}
