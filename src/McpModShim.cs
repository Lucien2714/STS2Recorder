using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2_MCP;

/// <summary>
/// Supplies the two members that the STS2MCP partials vendored at
/// vendor/STS2MCP expect from <c>McpMod.cs</c>, which this mod deliberately does
/// not compile (it carries the HTTP listener and STS2MCP's own
/// <c>[ModInitializer]</c>).
///
/// This file is the entire seam between STS2Recorder and STS2MCP's source. Keep
/// it minimal: anything added here is a member upstream code can start depending
/// on, which makes future re-syncs harder.
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
    /// Bridges to STS2MCP's <c>BuildGameState()</c>, which is private to this
    /// partial class and therefore unreachable from the recorder's own types.
    ///
    /// Must be called on Godot's main thread.
    /// </summary>
    internal static Dictionary<string, object?> CaptureSingleplayerState() => BuildGameState();
}
