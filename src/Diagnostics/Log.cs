using System;
using Godot;

namespace STS2_Recorder.Diagnostics;

/// <summary>
/// Console logging with a consistent prefix, mirroring STS2MCP's convention so
/// both mods are legible in the same Godot output pane.
/// </summary>
internal static class Log
{
    private const string Prefix = "[STS2 Recorder]";

    internal static void Info(string message) => GD.Print($"{Prefix} {message}");

    internal static void Warn(string message) => GD.PrintErr($"{Prefix} WARN: {message}");

    internal static void Error(string message) => GD.PrintErr($"{Prefix} {message}");

    internal static void Error(string message, Exception ex) =>
        GD.PrintErr($"{Prefix} {message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>
    /// Runs <paramref name="action"/>, swallowing and logging any exception.
    ///
    /// Every entry point reachable from game code (Harmony patches, per-frame
    /// callbacks) is wrapped in this. The recorder is a passive observer of
    /// someone's real run: a bug in it must never surface as a crash, a stutter,
    /// or a lost run for the player.
    /// </summary>
    internal static void Guard(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Error($"{context} failed (recording may be incomplete)", ex);
        }
    }
}
