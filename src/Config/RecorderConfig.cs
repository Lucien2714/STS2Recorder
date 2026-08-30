using System;
using System.IO;
using System.Text.Json;
using Godot;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Config;

/// <summary>
/// User-editable settings, read once at startup from <c>STS2_Recorder.conf</c>
/// next to the mod DLL. A missing file is written with the defaults, matching
/// how STS2MCP handles its own config.
///
/// Every field degrades to its default on a parse error rather than disabling
/// the mod, so a hand-edited typo costs a setting, not a recording.
/// </summary>
internal sealed class RecorderConfig
{
    private const string FileName = "STS2_Recorder.conf";

    /// <summary>Master switch. When false the mod loads but records nothing.</summary>
    internal bool Enabled { get; private init; } = true;

    /// <summary>
    /// Directory for trajectory files. Defaults to <c>recordings/</c> under the
    /// game's user data dir (alongside the game's own saves), which survives
    /// mod reinstalls and game updates.
    /// </summary>
    internal string OutputDir { get; private init; } = DefaultOutputDir();

    /// <summary>
    /// Indent the JSON. Costs roughly 2-3x file size; worth it while the schema
    /// is young and you are eyeballing output, cheap to turn off for bulk
    /// collection.
    /// </summary>
    internal bool PrettyPrint { get; private init; } = true;

    private static string DefaultOutputDir()
    {
        // ProjectSettings.GlobalizePath resolves user:// to the real per-user
        // data dir; OS.GetUserDataDir() is the same path, but going through
        // GlobalizePath keeps behaviour identical if the game ever remaps it.
        try
        {
            return Path.Combine(ProjectSettings.GlobalizePath("user://"), "recordings");
        }
        catch
        {
            return Path.Combine(OS.GetUserDataDir(), "recordings");
        }
    }

    private static string? ModDirectory()
    {
        try
        {
            return Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the config, creating it with defaults if absent. Never throws.
    /// </summary>
    internal static RecorderConfig Load()
    {
        var defaults = new RecorderConfig();

        string? modDir = ModDirectory();
        if (modDir == null)
        {
            Log.Warn("Could not locate the mod directory; using default settings.");
            return defaults;
        }

        string path = Path.Combine(modDir, FileName);

        if (!File.Exists(path))
        {
            WriteDefaultConfig(path, defaults);
            return defaults;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            return new RecorderConfig
            {
                Enabled     = ReadBool(root, "enabled", defaults.Enabled),
                PrettyPrint = ReadBool(root, "pretty_print", defaults.PrettyPrint),
                OutputDir   = ReadString(root, "output_dir", defaults.OutputDir)
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {path} ({ex.Message}); using default settings.");
            return defaults;
        }
    }

    private static void WriteDefaultConfig(string path, RecorderConfig defaults)
    {
        try
        {
            // Written by hand rather than via a serializer so the file can carry
            // explanatory comments-as-keys for whoever opens it next.
            string json = $$"""
            {
              "_comment": "Settings for the STS2 Recorder mod. Restart the game to apply.",
              "enabled": {{(defaults.Enabled ? "true" : "false")}},
              "output_dir": {{JsonSerializer.Serialize(defaults.OutputDir)}},
              "pretty_print": {{(defaults.PrettyPrint ? "true" : "false")}}
            }
            """;
            File.WriteAllText(path, json);
            Log.Info($"Wrote default config to {path}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not write default config to {path} ({ex.Message}); using defaults.");
        }
    }

    private static bool ReadBool(JsonElement root, string key, bool fallback) =>
        root.TryGetProperty(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean()
            : fallback;

    private static string ReadString(JsonElement root, string key, string fallback)
    {
        if (root.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String)
        {
            string? value = e.GetString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return fallback;
    }
}
