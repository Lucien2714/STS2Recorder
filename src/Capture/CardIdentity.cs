using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Capture;

/// <summary>
/// Identifies a card in its own right, for the <c>subject</c> of a step that
/// acted on one.
///
/// An index alone does not say what a card was, and neither does the recorded
/// state's entry for it. STS2MCP records a card's id, name, type, cost, rarity
/// and an upgraded flag - everything a card is *printed* with - and nothing at
/// all about what a run has since done to it. Two Strikes in the same hand can
/// differ in how far they are upgraded, what they are enchanted with, what
/// afflicts them, and whether they are real deck cards or copies conjured for
/// one combat, and the state calls all of them "Strike, upgraded".
///
/// So this carries exactly what the state cannot: everything about a card that
/// a run changed after it was printed.
/// </summary>
internal static class CardIdentity
{
    /// <param name="card">The card acted on; null yields no subject.</param>
    /// <param name="name">
    /// The card's name as the recorded state spells it, so a subject reads the
    /// same as the state entry beside it. Falls back to the card's own title
    /// when the state has no entry to read it from.
    /// </param>
    internal static Dictionary<string, object?>? Describe(CardModel? card, string? name = null)
    {
        if (card == null) return null;

        var subject = new Dictionary<string, object?>
        {
            ["kind"]              = "card",
            ["id"]                = card.Id.Entry,
            ["name"]              = name ?? Title(card),
            ["is_upgraded"]       = card.IsUpgraded,
            ["upgrade_level"]     = card.CurrentUpgradeLevel,

            // Without the ceiling, a level says nothing: +1 is a finished card
            // for most, and a third of the way for a card that upgrades thrice.
            ["max_upgrade_level"] = card.MaxUpgradeLevel
        };

        AddRunHistory(card, subject);

        return subject;
    }

    /// <summary>
    /// What the run did to this card beyond upgrading it. Guarded as a block
    /// rather than left to the caller's guard: these read further into the card
    /// than the fields above, and a step recorded without its enchantment is
    /// worth more than no step at all.
    /// </summary>
    private static void AddRunHistory(CardModel card, Dictionary<string, object?> subject)
    {
        try
        {
            if (card.Enchantment is { } enchantment)
                subject["enchantment"] = Modifier(enchantment.Id.Entry, () => enchantment.Title, enchantment.Amount);

            if (card.Affliction is { } affliction)
                subject["affliction"] = Modifier(affliction.Id.Entry, () => affliction.Title, affliction.Amount);

            // Copies exist only for the combat that made them. A play of one is
            // not a play of the deck card it was copied from, and a deck built
            // from this file must not count it.
            if (card.IsClone) subject["is_clone"] = true;
            if (card.IsDupe) subject["is_dupe"] = true;

            // Absent for a card not in the deck yet - one still on offer in a
            // shop or a reward.
            if (card.FloorAddedToDeck is { } floor) subject["floor_added_to_deck"] = floor;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read what the run did to a card: {ex.Message}");
        }
    }

    /// <summary>
    /// An enchantment or an affliction: what it is, and how much of it. The name
    /// is there for reading by eye; the id is what a consumer should match on.
    /// </summary>
    private static Dictionary<string, object?> Modifier(string id, Func<object?> title, int amount) =>
        new()
        {
            ["id"]     = id,
            ["name"]   = STS2_MCP.McpMod.SafeGetText(title),
            ["amount"] = amount
        };

    /// <summary>
    /// The card's display name, resolved the way STS2MCP's state builder
    /// resolves it so a label reads the same as the state beside it.
    /// </summary>
    internal static string? Title(CardModel? card) =>
        card == null ? null : STS2_MCP.McpMod.SafeGetText(() => card.Title);
}
