using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Recording;

namespace STS2_Recorder.Capture;

/// <summary>
/// Captures every card the player picks without playing it: the card taken from
/// a "choose a card" offer, the cards picked out of a deck grid, the bundle
/// taken from a "choose a pack" offer, the card taken from a combat reward, and
/// the cards picked out of hand mid-combat.
///
/// Until this existed, an Ancient room that handed the player an Arcane Scroll
/// recorded only the <c>proceed</c> that followed it. The cards on offer live in
/// the state of the pick itself - <c>card_select.cards</c>,
/// <c>bundle_select.bundles</c> - and no step was recorded while any of those
/// screens was up, so nothing in the file said what was offered or what was
/// taken.
///
/// These screens never reach the action queue, and their picks do not reach
/// <see cref="UiClicks"/> either. A human clicks a card's hitbox, which is an
/// <c>NClickableControl</c> - but STS2MCP's own <c>select_card</c> emits the
/// holder's <c>Pressed</c> signal directly instead, so patching the clickable
/// would see a human's pick and miss an agent's. The screens' own handlers are
/// where both paths converge, and that is what is patched here.
///
/// Prefix, so the state recorded beside a pick is the screen the player was
/// choosing from rather than what the pick turned it into.
///
/// The buttons that *finish* these screens - confirm, cancel, skip - are
/// ordinary clickables that both paths press, so they are captured in
/// <see cref="UiClicks"/> along with the rest of the click decisions.
/// </summary>
internal static class CardSelections
{
    /// <summary>
    /// <c>select_card</c>, for the card taken from a "choose a card" offer.
    /// Returns whether a step was recorded, which is what lets the patch take
    /// it back if the screen turns out to have ignored the pick.
    /// </summary>
    internal static bool RecordChosenCard(NChooseACardSelectionScreen screen, NCardHolder? holder) =>
        Guarded(() => RecordCardPick(screen, holder?.CardModel));

    /// <summary>
    /// <c>select_card</c>, for a card picked out of a deck grid. This is a
    /// toggle - the same card clicked again deselects it - exactly as replaying
    /// <c>select_card</c> would be.
    /// </summary>
    internal static void RecordGridPick(NCardGridSelectionScreen screen, CardModel? card) =>
        Guarded(() => RecordCardPick(screen, card));

    /// <summary><c>select_bundle</c>, by the bundle's index in the screen.</summary>
    internal static void RecordBundlePick(NChooseABundleSelectionScreen screen, NCardBundle? bundle) =>
        Guarded(() =>
        {
            if (bundle == null) return false;

            return RecorderMod.RecordAction(_ => new RecordedAction
            {
                Action  = "select_bundle",
                Args    = Index(IndexOfBundle(screen, bundle)),
                Label   = BundleLabel(bundle),
                Subject = BundleSubject(bundle)
            });
        });

    /// <summary>
    /// <c>select_card_reward</c>, for the card taken from a combat reward. The
    /// skip beside it is already captured as <c>skip_card_reward</c> in
    /// <see cref="UiClicks"/>.
    /// </summary>
    internal static void RecordRewardPick(NCardRewardSelectionScreen screen, NCardHolder? holder) =>
        Guarded(() =>
        {
            var card = holder?.CardModel;
            if (card == null) return false;

            return RecorderMod.RecordAction(_ => new RecordedAction
            {
                Action  = "select_card_reward",
                Args    = CardIndex(IndexOfCard<NCardHolder>(screen, card)),
                Label   = CardIdentity.Title(card),
                Subject = CardIdentity.Describe(card)
            });
        });

    /// <summary>
    /// <c>combat_select_card</c>, for a card picked out of hand by a card that
    /// asked for one - discard, exhaust, upgrade. Playing a card comes through
    /// the same button but not through here; it is captured from the action
    /// queue as <c>play_card</c>.
    /// </summary>
    internal static void RecordHandPick(NPlayerHand hand, NHandCardHolder? holder) =>
        Guarded(() =>
        {
            var card = holder?.CardModel;
            if (card == null) return false;

            return RecorderMod.RecordAction(_ => new RecordedAction
            {
                Action  = "combat_select_card",
                Args    = CardIndex(IndexInHand(hand, card)),
                Label   = CardIdentity.Title(card),
                Subject = CardIdentity.Describe(card)
            });
        });

    /// <summary>
    /// <c>select_card</c>, by the card's index in the screen as the recorded
    /// state numbers it.
    /// </summary>
    private static bool RecordCardPick(Node screen, CardModel? card)
    {
        if (card == null) return false;

        return RecorderMod.RecordAction(_ => new RecordedAction
        {
            Action  = "select_card",
            Args    = Index(IndexOfCard<NGridCardHolder>(screen, card)),
            Label   = CardIdentity.Title(card),
            Subject = CardIdentity.Describe(card)
        });
    }

    /// <summary>
    /// Runs <paramref name="record"/> under the recorder's guard, reporting
    /// whether it got as far as recording a step.
    ///
    /// These patches sit in front of game code, and reading a card off a node
    /// the game is in the middle of tearing down can throw. A recorder that
    /// loses a step is a nuisance; one that throws into the game costs someone
    /// their run.
    /// </summary>
    private static bool Guarded(Func<bool> record)
    {
        bool recorded = false;

        Log.Guard("Card selection hook", () => recorded = record());

        return recorded;
    }

    /// <summary>
    /// The card's index among the screen's cards, walked exactly as STS2MCP's
    /// state builder walks them - sorted by screen position, skipping holders
    /// with no card - so the index points at the entry beside it in the
    /// recorded state. <typeparamref name="THolder"/> is the holder type that
    /// state section counts, which is the grid holder for the selection screens
    /// and the base holder for a card reward.
    ///
    /// A deck grid only holds nodes for the rows it has allocated, so a card
    /// scrolled far out of view has no holder and no index. The state lists
    /// exactly the same cards, so the two agree either way.
    /// </summary>
    private static int IndexOfCard<THolder>(Node screen, CardModel card) where THolder : NCardHolder
    {
        int index = 0;

        foreach (var holder in STS2_MCP.McpMod.FindAllSortedByPosition<THolder>(screen))
        {
            if (holder.CardModel == null) continue;
            if (ReferenceEquals(holder.CardModel, card)) return index;

            index++;
        }

        return -1;
    }

    /// <summary>
    /// The card's index among the cards the hand is offering, which the state
    /// counts off <c>ActiveHolders</c> rather than off the scene tree - a hand
    /// in selection mode moves picked cards out to a container of their own, so
    /// position says nothing useful about what is still selectable.
    /// </summary>
    private static int IndexInHand(NPlayerHand hand, CardModel card)
    {
        int index = 0;

        foreach (var holder in hand.ActiveHolders)
        {
            if (holder.CardModel == null) continue;
            if (ReferenceEquals(holder.CardModel, card)) return index;

            index++;
        }

        return -1;
    }

    /// <summary>
    /// The bundle's index, found with the same unsorted traversal the state
    /// builder and the action API both use to number
    /// <c>bundle_select.bundles</c>.
    /// </summary>
    private static int IndexOfBundle(NChooseABundleSelectionScreen screen, NCardBundle bundle)
    {
        var bundles = STS2_MCP.McpMod.FindAll<NCardBundle>(screen);

        for (int i = 0; i < bundles.Count; i++)
            if (ReferenceEquals(bundles[i], bundle)) return i;

        return -1;
    }

    /// <summary>
    /// Every card the pack put in the deck, identified one by one.
    ///
    /// Taking a bundle is one click that adds several cards at once, so a step
    /// that named only the pack would leave the file unable to say what the deck
    /// gained. The state lists the bundle's cards, but as printed - what each
    /// one is actually upgraded or enchanted to is only here.
    /// </summary>
    private static Dictionary<string, object?> BundleSubject(NCardBundle bundle)
    {
        var cards = new List<Dictionary<string, object?>>();

        foreach (var card in bundle.Bundle)
            if (CardIdentity.Describe(card) is { } described) cards.Add(described);

        return new Dictionary<string, object?>
        {
            ["kind"]       = "bundle",
            ["card_count"] = cards.Count,
            ["cards"]      = cards
        };
    }

    /// <summary>The bundle's cards by name, which is how a reader tells two packs apart.</summary>
    private static string? BundleLabel(NCardBundle bundle)
    {
        var names = new List<string>();

        foreach (var card in bundle.Bundle)
        {
            string? title = CardIdentity.Title(card);
            if (title != null) names.Add(title);
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static Dictionary<string, object?> Index(int index) =>
        index >= 0 ? new Dictionary<string, object?> { ["index"] = index } : [];

    /// <summary>The action API spells a card's position <c>card_index</c> on these two.</summary>
    private static Dictionary<string, object?> CardIndex(int index) =>
        index >= 0 ? new Dictionary<string, object?> { ["card_index"] = index } : [];
}

/// <summary>
/// Taking one card from a "choose a card" offer. See <see cref="CardSelections"/>.
/// </summary>
[HarmonyPatch(typeof(NChooseACardSelectionScreen), "SelectHolder")]
internal static class ChooseACardSelectHolderPatch
{
    /// <summary>
    /// The screen ignores a pick made in the moments right after it opens, so
    /// the click that opened it cannot take a card by accident. Whether that
    /// happened is only knowable after the fact, hence the postfix below.
    /// </summary>
    private static readonly FieldInfo? ScreenCompleteField =
        AccessTools.Field(typeof(NChooseACardSelectionScreen), "_screenComplete");

    private static void Prefix(NCardHolder cardHolder, NChooseACardSelectionScreen __instance, out bool __state) =>
        __state = CardSelections.RecordChosenCard(__instance, cardHolder);

    private static void Postfix(NChooseACardSelectionScreen __instance, bool __state) =>
        Log.Guard("Card pick hook", () =>
        {
            // Retracts only the step this patch just recorded, so a swallowed
            // pick can never take an earlier one out with it.
            if (__state && Swallowed(__instance)) RecorderMod.RetractAction("select_card");
        });

    /// <summary>
    /// Whether the screen ignored the pick. Unreadable state is treated as a
    /// pick that landed: a recording is better off holding a step that may be
    /// spurious than missing every card taken from these screens.
    /// </summary>
    private static bool Swallowed(NChooseACardSelectionScreen screen)
    {
        try
        {
            return ScreenCompleteField?.GetValue(screen) is false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not tell whether a card pick landed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Picking cards out of a deck grid - upgrade, transform, enchant, remove, and
/// the in-combat pile screens. Each screen implements the click itself, so every
/// concrete screen's override is patched. See <see cref="CardSelections"/>.
/// </summary>
[HarmonyPatch]
internal static class CardGridSelectionOnCardClickedPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var targets = new List<MethodBase>();

        // Found rather than listed, so a screen added by a game update is
        // captured too instead of quietly going unrecorded.
        foreach (var type in ScreenTypes())
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(NCardGridSelectionScreen))) continue;

            var method = AccessTools.DeclaredMethod(type, "OnCardClicked", [typeof(CardModel)]);
            if (method != null) targets.Add(method);
        }

        if (targets.Count == 0)
            Log.Warn("No card-grid selection screens found; picks from a deck grid will not be recorded.");

        return targets;
    }

    private static Type[] ScreenTypes()
    {
        try
        {
            return typeof(NCardGridSelectionScreen).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A game assembly carrying a type the recorder cannot load is not a
            // reason to give up on the screens that did load.
            var loaded = new List<Type>();
            foreach (var type in ex.Types)
                if (type != null) loaded.Add(type);

            return loaded.ToArray();
        }
    }

    private static void Prefix(NCardGridSelectionScreen __instance, CardModel card) =>
        CardSelections.RecordGridPick(__instance, card);
}

/// <summary>
/// Taking one bundle from a "choose a pack" offer. See <see cref="CardSelections"/>.
/// </summary>
[HarmonyPatch(typeof(NChooseABundleSelectionScreen), "OnBundleClicked")]
internal static class ChooseABundleClickedPatch
{
    private static void Prefix(NChooseABundleSelectionScreen __instance, NCardBundle bundleNode) =>
        CardSelections.RecordBundlePick(__instance, bundleNode);
}

/// <summary>
/// Taking the card from a combat reward. See <see cref="CardSelections"/>.
///
/// Unlike the choose-a-card screen, this one keeps an early click off a card by
/// making the holders unclickable for a moment rather than by refusing the pick,
/// so reaching this handler always means a card was taken.
/// </summary>
[HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
internal static class CardRewardSelectCardPatch
{
    private static void Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder) =>
        CardSelections.RecordRewardPick(__instance, cardHolder);
}

/// <summary>
/// Picking a card out of hand mid-combat, for a card that asked for one. The
/// hand routes a click to one of these two by the mode it is in, and both are
/// reached only after every check that could refuse the pick. See
/// <see cref="CardSelections"/>.
/// </summary>
[HarmonyPatch]
internal static class PlayerHandSelectCardPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "SelectCardInSimpleMode", "SelectCardInUpgradeMode" })
        {
            var method = AccessTools.DeclaredMethod(typeof(NPlayerHand), name, [typeof(NHandCardHolder)]);

            if (method != null) yield return method;
            else Log.Warn($"NPlayerHand.{name} not found; in-combat card picks may not be recorded.");
        }
    }

    private static void Prefix(NPlayerHand __instance, NHandCardHolder holder) =>
        CardSelections.RecordHandPick(__instance, holder);
}
