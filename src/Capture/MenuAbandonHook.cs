using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Capture;

/// <summary>
/// Records the outcome of a run abandoned from the main menu.
///
/// This ending never touches a live run: the player quit to the menu - which
/// closed the recording without an outcome, since a save-and-quit is not an
/// ending - and then pressed Abandon. The game builds the run's history entry
/// straight from the save file without loading the run, so none of the in-run
/// hooks fire and, before this, the recording simply stayed outcome-less
/// forever.
///
/// Prefix rather than postfix: <c>AbandonRun</c> is the last reader of the save
/// it abandons, and the menu refreshes that field once the run is gone.
/// </summary>
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu.AbandonRun))]
internal static class NMainMenuAbandonRunPatch
{
    private static void Prefix(ReadSaveResult<SerializableRun> ____readRunSaveResult)
    {
        Log.Guard("Menu abandon hook", () =>
        {
            if (____readRunSaveResult is not { Success: true, SaveData: { } save }) return;

            RecorderMod.RecordAbandonedFromMenu(save.StartTime, save.FloorReached);
        });
    }
}
