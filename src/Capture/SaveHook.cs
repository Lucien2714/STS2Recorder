using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using STS2_Recorder.Diagnostics;

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

            // The game's reload counter only settles once a save exists, so it
            // is mirrored here rather than at run start.
            session.Trajectory.Run.NumReloads = save.NumReloads;

            RecorderMod.FlushActiveSession();
        });
    }
}
