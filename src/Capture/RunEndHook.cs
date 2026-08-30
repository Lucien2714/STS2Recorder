using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Capture;

/// <summary>
/// Closes the recording at the moment the run actually ends.
///
/// <c>RunManager.OnEnded</c> is the single point every ending funnels through -
/// <c>WinRun</c> for a victory, the player-kill path for a death, and abandoning
/// (which sets <c>IsAbandoned</c> and then kills the party). It runs while the
/// run is still fully intact: the state is readable and <c>History</c> has just
/// been built.
///
/// Waiting for the run to go inactive instead is too late. That only happens in
/// <c>RunManager.CleanUp</c>, called from the run scene's own teardown
/// notification, by which point the state has been dismantled and a snapshot
/// reads as the main menu - which is exactly what earlier recordings captured as
/// their final state.
/// </summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class RunManagerOnEndedPatch
{
    private static void Postfix(bool isVictory)
    {
        Log.Guard("Run end hook", () => RecorderMod.FinishRun(isVictory));
    }
}
