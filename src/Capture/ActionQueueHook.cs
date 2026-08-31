using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Recording;

namespace STS2_Recorder.Capture;

/// <summary>
/// Captures what the player did, from the queue every acted-on decision passes
/// through.
///
/// <c>ActionQueueSynchronizer.RequestEnqueue</c> is the game's own funnel for
/// locally initiated actions - the play-card, end-turn, potion and map paths all
/// reach it - and it is the same call STS2MCP's <c>ExecuteAction</c> makes to
/// drive the game. Hooking it rather than the individual buttons means a
/// decision cannot be captured in one place and missed in another, and the
/// recorded vocabulary lines up with the MCP action API by construction.
///
/// Prefix, so the state recorded beside an action is the one the player was
/// looking at when they chose it, not its result.
///
/// The queue also carries the game's own bookkeeping - combat state hand-offs,
/// turn plumbing, dev-console commands. Only the action types below are player
/// decisions; everything else is ignored rather than guessed at, because a step
/// this recorder emits has to be something a human actually chose.
/// </summary>
[HarmonyPatch(typeof(ActionQueueSynchronizer), nameof(ActionQueueSynchronizer.RequestEnqueue))]
internal static class ActionQueueRequestEnqueuePatch
{
    private static void Prefix(GameAction action) => Log.Guard("Action hook", () =>
    {
        // Ending a turn can be taken back before it resolves. The end_turn is
        // already recorded by then, so it has to come back out: a trajectory
        // must not contain a decision the player reversed.
        if (action is UndoEndPlayerTurnAction)
        {
            RecorderMod.RetractAction("end_turn");
            return;
        }

        // Snapshotting the state is the expensive part of recording a step, and
        // most of this queue is the game talking to itself, so the cheap type
        // check comes first.
        if (!IsPlayerDecision(action)) return;

        RecorderMod.RecordAction(state => Describe(action, state));
    });

    private static bool IsPlayerDecision(GameAction action) =>
        action is PlayCardAction or EndPlayerTurnAction
               or UsePotionAction or DiscardPotionGameAction
               or VoteForMapCoordAction or PickRelicAction;

    private static RecordedAction? Describe(GameAction action, Dictionary<string, object?>? state) =>
        action switch
        {
            PlayCardAction play          => DescribePlayCard(play, state),
            EndPlayerTurnAction          => new RecordedAction { Action = "end_turn" },
            UsePotionAction potion       => DescribeUsePotion(potion, state),
            DiscardPotionGameAction drop => DescribeDiscardPotion(drop),
            VoteForMapCoordAction vote   => DescribeChooseMapNode(vote, state),
            PickRelicAction relic        => DescribeClaimTreasureRelic(relic),

            // MoveToMapCoordAction lands here too, but it is the resolution of
            // the vote above rather than a second decision, so it is ignored.
            _                            => null
        };

    /// <summary>
    /// <c>play_card</c>, with the card's position in the hand and the target
    /// spelled as the recorded state spells it.
    /// </summary>
    private static RecordedAction DescribePlayCard(PlayCardAction play, Dictionary<string, object?>? state)
    {
        var card = PlayedCard(play);
        var args = new Dictionary<string, object?>();

        int index = HandIndexOf(play, card);
        if (index >= 0) args["card_index"] = index;

        string? target = EntityIdOf(play.Target, state);
        if (target != null) args["target"] = target;

        return new RecordedAction
        {
            Action  = "play_card",
            Args    = args,
            Label   = CardLabel(index, state) ?? play.CardModelId.Entry,
            Subject = CardIdentity.Describe(card, CardLabel(index, state))
        };
    }

    /// <summary><c>use_potion</c>, by slot, with its target if it had one.</summary>
    private static RecordedAction DescribeUsePotion(UsePotionAction potion, Dictionary<string, object?>? state)
    {
        var args = new Dictionary<string, object?> { ["slot"] = (int)potion.PotionIndex };

        string? target = EntityIdForCombatId(potion.TargetId, state);
        if (target != null) args["target"] = target;

        return new RecordedAction
        {
            Action = "use_potion",
            Args   = args,
            Label  = PotionLabel((int)potion.PotionIndex, state)
        };
    }

    /// <summary><c>discard_potion</c>, by slot.</summary>
    private static RecordedAction DescribeDiscardPotion(DiscardPotionGameAction drop)
    {
        int slot = -1;

        // The slot is private on the action, but the net form the game builds
        // for its own use carries it in the open.
        if (drop.ToNetAction() is NetDiscardPotionGameAction net) slot = (int)net.potionSlotIndex;

        return new RecordedAction
        {
            Action = "discard_potion",
            Args   = slot >= 0 ? new Dictionary<string, object?> { ["slot"] = slot } : []
        };
    }

    /// <summary>
    /// <c>choose_map_node</c>, by the option's index in the recorded map state.
    ///
    /// Clicking a node is a vote - the same call STS2MCP's own
    /// <c>choose_map_node</c> makes - and the index is read back off the state's
    /// <c>next_options</c> rather than re-deriving how those options are
    /// ordered, so the recorded index always points at the option beside it.
    /// </summary>
    private static RecordedAction? DescribeChooseMapNode(
        VoteForMapCoordAction vote, Dictionary<string, object?>? state)
    {
        if (vote.ToNetAction() is not NetVoteForMapCoordAction net) return null;

        // No destination means the vote was withdrawn, which is not a move.
        if (net.destination is not { } destination) return null;
        var coord = destination.coord;

        foreach (var option in ListAt(state, "map", "next_options"))
        {
            if (!SameNumber(Value(option, "col"), coord.col)
                || !SameNumber(Value(option, "row"), coord.row)) continue;

            return new RecordedAction
            {
                Action = "choose_map_node",
                Args   = new Dictionary<string, object?> { ["index"] = Value(option, "index") },
                Label  = Value(option, "type") as string
            };
        }

        // The destination was not among the recorded options, so no index would
        // mean anything against this state. Better to record the move without
        // one than to guess.
        return new RecordedAction
        {
            Action = "choose_map_node",
            Label  = $"({coord.col},{coord.row})"
        };
    }

    /// <summary><c>claim_treasure_relic</c>, by the relic's index.</summary>
    private static RecordedAction? DescribeClaimTreasureRelic(PickRelicAction relic)
    {
        if (relic.ToNetAction() is not NetPickRelicAction net) return null;

        // A pick with no index is the game resolving the room, not a choice.
        if (net.relicIndex is not { } index) return null;

        return new RecordedAction
        {
            Action = "claim_treasure_relic",
            Args   = new Dictionary<string, object?> { ["index"] = (int)index }
        };
    }

    // --- Reading the action ----------------------------------------------------

    /// <summary>
    /// The card being played. <c>PlayCardAction</c> publishes the model id but
    /// not the instance, and two copies of Strike in hand are different
    /// instances, so the instance is looked up from the combat card registry the
    /// action carries a handle to.
    ///
    /// The action does hold the instance in a private <c>_card</c> field, and
    /// reading that field is what this used to do - but the field is assigned in
    /// <c>ExecuteAction()</c>, and this hook runs when the action is *enqueued*,
    /// where it is always still null. That silently cost every <c>play_card</c>
    /// step its <c>card_index</c> and its subject, and left the label as a raw
    /// model id. <c>NetCombatCard</c> is set in the constructor instead, so it
    /// is readable exactly when this needs it - and it resolves through the same
    /// registry <c>ExecuteAction()</c> resolves through, to the same instance.
    /// </summary>
    private static CardModel? PlayedCard(PlayCardAction play)
    {
        try
        {
            return play.NetCombatCard.ToCardModelOrNull();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the played card: {ex.Message}");
            return null;
        }
    }

    private static int HandIndexOf(PlayCardAction play, CardModel? card)
    {
        if (card == null) return -1;

        var hand = play.Player?.PlayerCombatState?.Hand?.Cards;
        if (hand == null) return -1;

        for (int i = 0; i < hand.Count; i++)
            if (ReferenceEquals(hand[i], card)) return i;

        return -1;
    }

    // --- Reading the state it is paired with -----------------------------------

    /// <summary>
    /// The <c>entity_id</c> the recorded state gives this creature, found by its
    /// combat id rather than by re-deriving the naming. A target in a step's
    /// arguments then always matches an entry in the enemy list beside it,
    /// however STS2MCP chooses to spell those ids.
    /// </summary>
    private static string? EntityIdOf(Creature? target, Dictionary<string, object?>? state) =>
        target == null ? null : EntityIdForCombatId(target.CombatId, state);

    private static string? EntityIdForCombatId(object? combatId, Dictionary<string, object?>? state)
    {
        if (combatId == null) return null;

        foreach (var enemy in ListAt(state, "battle", "enemies"))
        {
            if (enemy.TryGetValue("combat_id", out var id) && SameNumber(id, combatId))
                return enemy.TryGetValue("entity_id", out var entity) ? entity as string : null;
        }

        return null;
    }

    private static string? CardLabel(int index, Dictionary<string, object?>? state) =>
        NameAt(ListAt(state, "player", "hand"), index);

    private static string? PotionLabel(int slot, Dictionary<string, object?>? state) =>
        NameAt(ListAt(state, "player", "potions"), slot);

    private static object? Value(Dictionary<string, object?> entry, string key) =>
        entry.TryGetValue(key, out var value) ? value : null;

    private static string? NameAt(List<Dictionary<string, object?>> entries, int index) =>
        index < 0 || index >= entries.Count
            ? null
            : entries[index].TryGetValue("name", out var name) ? name as string : null;

    /// <summary>
    /// Reads <c>state[section][key]</c> as a list of objects, returning an empty
    /// list rather than throwing when the state was not captured or is shaped
    /// differently than expected.
    /// </summary>
    private static List<Dictionary<string, object?>> ListAt(
        Dictionary<string, object?>? state, string section, string key)
    {
        var entries = new List<Dictionary<string, object?>>();

        if (state == null
            || !state.TryGetValue(section, out var sectionValue)
            || sectionValue is not Dictionary<string, object?> inner
            || !inner.TryGetValue(key, out var listValue)
            || listValue is not IEnumerable items)
            return entries;

        foreach (var item in items)
            if (item is Dictionary<string, object?> entry) entries.Add(entry);

        return entries;
    }

    private static bool SameNumber(object? a, object? b)
    {
        try
        {
            return a != null && b != null && Convert.ToInt64(a) == Convert.ToInt64(b);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
