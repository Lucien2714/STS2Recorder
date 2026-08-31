using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Merchant;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Recording;

namespace STS2_Recorder.Capture;

/// <summary>
/// Captures what the player bought, from the one call every purchase goes
/// through.
///
/// The four shop slots - card, relic, potion, card removal - each hand their
/// click to <c>MerchantEntry.OnTryPurchaseWrapper</c>, and that is the same call
/// STS2MCP's <c>shop_purchase</c> makes on the entry directly. Hooking the entry
/// rather than the slots means a human's purchase and an agent's are recorded
/// identically, and the merchant's own UI cannot be bypassed into an unrecorded
/// sale. It covers the fake merchant event too, which sells its relics through
/// the same entries.
///
/// Card removal is the exception that needs its own patch:
/// <see cref="MerchantCardRemovalEntry"/> declares its own wrapper - taking the
/// extra <c>cancelable</c> flag - which does not call the base one, so a removal
/// bought from the shop UI never reaches the method above.
///
/// Prefix, so the state recorded beside a purchase is the shop as it was priced
/// and stocked when the player chose, not after the sale.
/// </summary>
internal static class ShopPurchases
{
    /// <summary>
    /// The entry whose recorded step can still be taken back, or null when
    /// there is none. Only ever compared by reference: a failure is reported on
    /// the entry that failed, and taking back a step means taking back the one
    /// recorded for *that* entry, never whatever happens to be last.
    /// </summary>
    private static MerchantEntry? _retractable;

    /// <summary>
    /// <c>shop_purchase</c>, by the item's index in the shop as the recorded
    /// state numbers it.
    /// </summary>
    /// <param name="ignoreCost">
    /// The game buying on the player's behalf rather than the player buying:
    /// Lord's Parasol clears the whole shop this way when the room opens. Free
    /// items still arrive in the deck, but nobody chose them, so they are not
    /// decisions and are not recorded.
    /// </param>
    internal static void Record(MerchantEntry entry, MerchantInventory? inventory, bool ignoreCost) =>
        Log.Guard("Shop purchase hook", () =>
        {
            if (ignoreCost) return;

            bool recorded = RecorderMod.RecordAction(_ => new RecordedAction
            {
                Action  = "shop_purchase",
                Args    = Index(IndexOf(entry, inventory)),
                Label   = Label(entry),
                Subject = Subject(entry)
            });

            _retractable = recorded ? entry : null;
        });

    /// <summary>
    /// Drops the step recorded for a purchase the shop then refused - sold out,
    /// not enough gold, no room for the potion. The click happened, but the
    /// player did not buy anything, and a trajectory must not claim they did.
    ///
    /// Reads the game's own verdict rather than re-deriving when a sale is
    /// allowed, so the recorder cannot disagree with the shop about what was
    /// sold.
    /// </summary>
    internal static void RecordFailure(MerchantEntry entry, PurchaseStatus status) =>
        Log.Guard("Shop purchase hook", () =>
        {
            if (status == PurchaseStatus.Success || !ReferenceEquals(entry, _retractable)) return;

            _retractable = null;
            RecorderMod.RetractAction("shop_purchase");
        });

    /// <summary>
    /// Settles a purchase that went through, so a later failure on the same
    /// entry - clicking the slot it just emptied - cannot take it back.
    /// </summary>
    internal static void RecordSuccess(MerchantEntry entry) =>
        Log.Guard("Shop purchase hook", () =>
        {
            if (ReferenceEquals(entry, _retractable)) _retractable = null;
        });

    /// <summary>
    /// The item's index in the shop, walked the way STS2MCP's state builder and
    /// action API both walk it - <c>AllEntries</c>, which is cards, then relics,
    /// then potions, then card removal - so the index points at the entry beside
    /// it in the recorded <c>shop.items</c>.
    /// </summary>
    private static int IndexOf(MerchantEntry entry, MerchantInventory? inventory)
    {
        if (inventory == null) return -1;

        int index = 0;
        foreach (var candidate in inventory.AllEntries)
        {
            if (ReferenceEquals(candidate, entry)) return index;

            index++;
        }

        return -1;
    }

    private static string? Label(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card     => CardIdentity.Title(card.CreationResult?.Card),
        MerchantRelicEntry relic   => STS2_MCP.McpMod.SafeGetText(() => relic.Model?.Title),
        MerchantPotionEntry potion => STS2_MCP.McpMod.SafeGetText(() => potion.Model?.Title),
        MerchantCardRemovalEntry   => "card removal",
        _                          => null
    };

    /// <summary>
    /// Cards only. A relic or a potion is pinned down by the id the state
    /// records beside it, but a bought card carries an upgrade level and an
    /// enchantment that the shop entry in the state does not.
    /// </summary>
    private static Dictionary<string, object?>? Subject(MerchantEntry entry) =>
        entry is MerchantCardEntry card ? CardIdentity.Describe(card.CreationResult?.Card) : null;

    private static Dictionary<string, object?> Index(int index) =>
        index >= 0 ? new Dictionary<string, object?> { ["index"] = index } : [];
}

/// <summary>
/// Buying a card, a relic or a potion - and a card removal bought through
/// STS2MCP, which calls this wrapper rather than the one below. See
/// <see cref="ShopPurchases"/>.
/// </summary>
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper),
    typeof(MerchantInventory), typeof(bool))]
internal static class MerchantEntryPurchasePatch
{
    private static void Prefix(MerchantEntry __instance, MerchantInventory inventory, bool ignoreCost) =>
        ShopPurchases.Record(__instance, inventory, ignoreCost);
}

/// <summary>
/// Buying a card removal from the shop UI, which goes through the removal
/// entry's own wrapper. See <see cref="ShopPurchases"/>.
/// </summary>
[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper),
    typeof(MerchantInventory), typeof(bool), typeof(bool))]
internal static class MerchantCardRemovalPurchasePatch
{
    private static void Prefix(MerchantCardRemovalEntry __instance, MerchantInventory inventory, bool ignoreCost) =>
        ShopPurchases.Record(__instance, inventory, ignoreCost);
}

/// <summary>
/// The shop refusing a sale. See <see cref="ShopPurchases.RecordFailure"/>.
/// </summary>
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseFailed))]
internal static class MerchantEntryPurchaseFailedPatch
{
    private static void Prefix(MerchantEntry __instance, PurchaseStatus status) =>
        ShopPurchases.RecordFailure(__instance, status);
}

/// <summary>
/// The shop completing a sale. See <see cref="ShopPurchases.RecordSuccess"/>.
/// </summary>
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
internal static class MerchantEntryPurchaseCompletedPatch
{
    private static void Prefix(MerchantEntry __instance) => ShopPurchases.RecordSuccess(__instance);
}
