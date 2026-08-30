using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace STS2_MCP;

/// <summary>
/// Supplies the few members that the STS2MCP partials in the vendor/STS2MCP
/// submodule expect from <c>McpMod.cs</c>, which this mod deliberately does not
/// compile (it carries the HTTP listener and STS2MCP's own
/// <c>[ModInitializer]</c>).
///
/// This file is the entire seam between STS2Recorder and STS2MCP's source. Keep
/// it minimal: anything added here is a member upstream code can start depending
/// on, which makes future submodule bumps harder.
/// </summary>
public static partial class McpMod
{
    /// <summary>
    /// Byte-identical to STS2MCP's own serializer options. The recorder writes
    /// game state with these so a recorded <c>state</c> object is exactly what
    /// <c>GET /api/v1/singleplayer</c> would have returned.
    ///
    /// Note there is no <c>DictionaryKeyPolicy</c>: state dictionary keys are
    /// already snake_case and must pass through verbatim. The naming policy
    /// applies only to POCO properties (the recorder's own wrapper types).
    /// </summary>
    internal static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Marshals work onto Godot's main thread.
    ///
    /// In STS2MCP this hands work from the HTTP listener's background thread to
    /// the main thread via a queue drained each frame. This recorder has no
    /// background threads at all - it is driven entirely by Harmony patches and
    /// a per-frame callback, both of which already run on the main thread - so
    /// the correct implementation here is to run inline.
    ///
    /// Only upstream code that the recorder never invokes (the profile and
    /// compendium endpoint handlers) calls this. It exists to satisfy the
    /// compiler, and running inline keeps it correct rather than merely
    /// compiling: a caller on the main thread gets exactly the semantics it
    /// expects, and there is no other kind of caller.
    /// </summary>
    internal static Task<T> RunOnMainThread<T>(Func<T> func)
    {
        try
        {
            return Task.FromResult(func());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    /// <inheritdoc cref="RunOnMainThread{T}(Func{T})"/>
    internal static Task RunOnMainThread(Action action)
    {
        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// Bridges to STS2MCP's <c>BuildGameState()</c>, which is private to this
    /// partial class and therefore unreachable from the recorder's own types.
    ///
    /// Must be called on Godot's main thread.
    /// </summary>
    internal static Dictionary<string, object?> CaptureSingleplayerState() => BuildGameState();
}
