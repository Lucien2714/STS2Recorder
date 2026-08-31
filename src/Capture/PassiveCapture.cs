using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace STS2_Recorder.Capture;

/// <summary>
/// Keeps a state snapshot from changing the run it is snapshotting.
///
/// STS2MCP's <c>BuildGameState()</c> is not purely a reader. Where a screen has
/// to be open before an agent can act on it, it opens the screen as a side
/// effect of describing it: the shopkeeper's inventory in a merchant room, the
/// fake merchant's inventory, the chest in a treasure room. That is right for
/// STS2MCP, whose caller is about to act. It is wrong here - this mod snapshots
/// the state beside every decision a *human* makes, so those side effects fire
/// mid-run, unasked. Clicking proceed in a merchant room popped the shop back
/// open, because recording that click captured the state first.
///
/// None of them earn anything for a recording either. The shop's items,
/// prices and stock come off <c>MerchantRoom.GetLocalInventory()</c> and the
/// fake merchant's off its own model, whether or not the inventory UI is open,
/// and a treasure room that has not been opened yet has no relics to list in
/// either case.
///
/// So the recorder declares its snapshots inert: while one is running, the calls
/// that would change the game are turned into no-ops. Suppressing the whole
/// class of them - rather than the three sites that do it today - keeps the
/// guarantee true when the vendored sources are next re-synced.
///
/// This cannot mute the STS2MCP mod's own state building when both are
/// installed. The flag is set only for the span of this recorder's snapshot,
/// which runs synchronously on the main thread inside a Harmony patch, and
/// STS2MCP builds its state on that same thread from a queue drained once a
/// frame; the two cannot be inside each other.
/// </summary>
internal static class PassiveCapture
{
    /// <summary>
    /// Depth rather than a flag: a snapshot taken from inside another one must
    /// not lift the guard when the inner one finishes.
    /// </summary>
    private static int _depth;

    /// <summary>True while a state snapshot is being built.</summary>
    internal static bool InProgress => _depth > 0;

    internal static void Enter() => _depth++;

    internal static void Exit()
    {
        if (_depth > 0) _depth--;
    }
}

/// <summary>
/// The merchant room's inventory, which the state builder opens when it finds it
/// closed. See <see cref="PassiveCapture"/>.
/// </summary>
[HarmonyPatch(typeof(NMerchantRoom), nameof(NMerchantRoom.OpenInventory))]
internal static class MerchantOpenInventoryPatch
{
    private static bool Prefix() => !PassiveCapture.InProgress;
}
