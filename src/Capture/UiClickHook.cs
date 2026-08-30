using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using STS2_Recorder.Diagnostics;
using STS2_Recorder.Recording;

namespace STS2_Recorder.Capture;

/// <summary>
/// Captures the decisions the player makes by clicking rather than by acting on
/// the run: which event option, which rest, which reward, which relic.
///
/// These never reach the action queue - the game drives them straight off the
/// button - so this hooks the click funnel instead. Every clickable in the game
/// ends a click in <c>NClickableControl</c>: a real mouse release goes through
/// <c>OnReleaseHandler</c>, and a programmatic one through <c>ForceClick</c>,
/// which is how STS2MCP itself presses these buttons. Patching both means a
/// human's click and an agent's land in the same place, and there is no path
/// that quietly bypasses the recorder.
///
/// Every clickable in the game comes through here, menus and tooltips included,
/// so the control types below are the whitelist of things that count as a run
/// decision. Anything else is ignored.
/// </summary>
internal static class UiClicks
{
    /// <summary>
    /// Records the click if it was a decision. Disabled controls are skipped:
    /// the game ignores those clicks, and a trajectory should not contain a
    /// choice that never happened.
    /// </summary>
    internal static void Record(NClickableControl control) => Log.Guard("Click hook", () =>
    {
        if (!control.IsEnabled || !IsDecision(control)) return;

        RecorderMod.RecordAction(state => Describe(control, state));
    });

    private static bool IsDecision(NClickableControl control) =>
        control is NEventOptionButton or NRestSiteButton or NRewardButton
                or NCardRewardAlternativeButton or NRelicBasicHolder or NProceedButton
        || IsAncientDialogue(control);

    private static RecordedAction? Describe(NClickableControl control, Dictionary<string, object?>? state) =>
        control switch
        {
            NEventOptionButton option => DescribeEventOption(option),
            NRestSiteButton rest      => DescribeRestOption(rest, state),
            NRewardButton reward      => DescribeClaimReward(reward),
            NRelicBasicHolder relic   => DescribeSelectRelic(relic),

            NCardRewardAlternativeButton => new RecordedAction { Action = "skip_card_reward" },
            NProceedButton proceed       => new RecordedAction
            {
                Action = "proceed",
                Label  = proceed.IsSkip ? "skip" : "proceed"
            },

            _ => IsAncientDialogue(control)
                ? new RecordedAction { Action = "advance_dialogue" }
                : null
        };

    /// <summary>
    /// <c>choose_event_option</c>, indexed exactly as the recorded event state
    /// indexes its options - by walking the same buttons from the same room with
    /// the same helper STS2MCP's state builder uses, rather than trusting that a
    /// separate ordering would agree with it.
    /// </summary>
    private static RecordedAction DescribeEventOption(NEventOptionButton option)
    {
        int index = IndexAmong<NEventOptionButton>(option, NEventRoom.Instance);

        return new RecordedAction
        {
            Action = "choose_event_option",
            Args   = Index(index),
            Label  = Text(() => option.Option?.Title)
        };
    }

    /// <summary>
    /// <c>choose_rest_option</c>. The rest state lists the room's own options
    /// rather than its buttons, so the index is found by the option id the state
    /// records beside it.
    /// </summary>
    private static RecordedAction DescribeRestOption(NRestSiteButton rest, Dictionary<string, object?>? state)
    {
        object? optionId = rest.Option?.OptionId;
        int index = -1;

        var options = StateList(state, "rest_site", "options");
        for (int i = 0; i < options.Count && index < 0; i++)
            if (optionId != null && Equals(options[i].GetValueOrDefault("id"), optionId)) index = i;

        return new RecordedAction
        {
            Action = "choose_rest_option",
            Args   = Index(index),
            Label  = Text(() => rest.Option?.Title)
        };
    }

    private static RecordedAction DescribeClaimReward(NRewardButton reward) =>
        new()
        {
            Action = "claim_reward",
            Args   = Index(IndexAmong<NRewardButton>(reward, Ancestor<NRewardsScreen>(reward))),
            Label  = Text(() => reward.Reward?.Description)
        };

    private static RecordedAction DescribeSelectRelic(NRelicBasicHolder relic) =>
        new()
        {
            Action = "select_relic",
            Args   = Index(IndexAmong<NRelicBasicHolder>(relic, Ancestor<NChooseARelicSelection>(relic)))
        };

    /// <summary>
    /// The Ancient rooms advance their dialogue through a bare clickable named
    /// in the layout rather than a button type of its own, which is also how
    /// STS2MCP finds it.
    /// </summary>
    private static bool IsAncientDialogue(NClickableControl control) =>
        control.Name == "DialogueHitbox" && Ancestor<NAncientEventLayout>(control) != null;

    // --- Plumbing --------------------------------------------------------------

    /// <summary>
    /// The control's position among the same kind of control under
    /// <paramref name="root"/>, using STS2MCP's own traversal so the index means
    /// the same thing as the one in the state recorded beside it. -1 when the
    /// root is gone or the control is not under it.
    /// </summary>
    private static int IndexAmong<T>(T control, Node? root) where T : Node
    {
        if (root == null) return -1;

        var found = STS2_MCP.McpMod.FindAll<T>(root);
        for (int i = 0; i < found.Count; i++)
            if (ReferenceEquals(found[i], control)) return i;

        return -1;
    }

    private static T? Ancestor<T>(Node node) where T : Node
    {
        for (var current = node.GetParent(); current != null; current = current.GetParent())
            if (current is T match) return match;

        return null;
    }

    /// <summary>
    /// Resolves display text the way the state builder does, so a label reads
    /// the same as the text in the state beside it.
    /// </summary>
    private static string? Text(System.Func<object?> getter) => STS2_MCP.McpMod.SafeGetText(getter);

    private static Dictionary<string, object?> Index(int index) =>
        index >= 0 ? new Dictionary<string, object?> { ["index"] = index } : [];

    private static List<Dictionary<string, object?>> StateList(
        Dictionary<string, object?>? state, string section, string key)
    {
        var entries = new List<Dictionary<string, object?>>();

        if (state == null
            || !state.TryGetValue(section, out var sectionValue)
            || sectionValue is not Dictionary<string, object?> inner
            || !inner.TryGetValue(key, out var listValue)
            || listValue is not System.Collections.IEnumerable items)
            return entries;

        foreach (var item in items)
            if (item is Dictionary<string, object?> entry) entries.Add(entry);

        return entries;
    }
}

/// <summary>Real mouse clicks. See <see cref="UiClicks"/>.</summary>
[HarmonyPatch(typeof(NClickableControl), "OnReleaseHandler")]
internal static class NClickableControlReleasePatch
{
    private static void Prefix(NClickableControl __instance) => UiClicks.Record(__instance);
}

/// <summary>
/// Programmatic clicks - how STS2MCP presses these same buttons, so an agent's
/// run records the same way a human's does. See <see cref="UiClicks"/>.
/// </summary>
[HarmonyPatch(typeof(NClickableControl), nameof(NClickableControl.ForceClick))]
internal static class NClickableControlForceClickPatch
{
    private static void Prefix(NClickableControl __instance) => UiClicks.Record(__instance);
}
