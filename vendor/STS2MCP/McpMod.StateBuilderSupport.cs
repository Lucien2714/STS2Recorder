using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;

namespace STS2_MCP;

/// <summary>
/// The three members of STS2MCP's <c>McpMod.Actions.cs</c> that
/// <c>BuildGameState()</c> calls, lifted out verbatim.
///
/// This file is the recorder's, not upstream's. Upstream has no such file: there
/// these three sit among the action executors, because over there the same class
/// both reads the game and drives it. The recorder only reads, so carrying
/// <c>McpMod.Actions.cs</c> meant compiling ~88 KB of executors that can never
/// run - and, through it, <c>McpMod.Profile.cs</c> and
/// <c>McpMod.Compendium.cs</c>, another ~41 KB that exists only to satisfy the
/// executors' references. Three helpers is a better trade than 130 KB of code
/// that cannot execute.
///
/// The bodies are copied unchanged, because changing them would change the
/// recorded state. Keep it that way: when re-syncing, re-copy these from
/// upstream's <c>McpMod.Actions.cs</c> rather than editing them here, and if
/// <c>BuildGameState()</c> starts calling something new from over there, lift
/// that out into this file too.
///
/// Copied from STS2MCP (MIT, Yikun Ji) - see LICENSE beside this file - at the
/// upstream commit recorded as <c>Sts2McpUpstreamCommit</c> in
/// STS2_Recorder.csproj.
/// </summary>
public static partial class McpMod
{
    /// <summary>
    /// Option-name prefix for a custom-run run modifier toggle:
    /// <c>modifier_&lt;id&gt;</c>. The state advertises modifiers under their
    /// bare id plus this option name.
    /// </summary>
    internal const string ModifierOptionPrefix = "modifier_";

    private static NProceedButton? FindCrystalSphereProceedButton(NCrystalSphereScreen screen)
    {
        var namedButton = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (IsControlVisibleOrActionable(namedButton))
            return namedButton;

        return FindAll<NProceedButton>(screen)
            .FirstOrDefault(IsControlVisibleOrActionable);
    }

    private static List<string> GetProgressEpochIdsByState(params string[] states)
    {
        var stateSet = new HashSet<string>(states, System.StringComparer.OrdinalIgnoreCase);
        try
        {
            var progress = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.Progress;
            if (progress == null)
                return new List<string>();

            return progress.Epochs
                .Where(epoch => stateSet.Contains(epoch.State.ToString()))
                .Select(epoch => epoch.Id)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
