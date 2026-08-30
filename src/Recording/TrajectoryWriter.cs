using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using STS2_Recorder.Config;
using STS2_Recorder.Diagnostics;

namespace STS2_Recorder.Recording;

/// <summary>
/// Serializes a <see cref="TrajectoryFile"/> to disk.
///
/// One file per run, rewritten in full on every flush. Rewriting rather than
/// appending keeps the file valid JSON at all times - a run interrupted by a
/// crash or an alt-F4 still leaves a readable file containing everything up to
/// the last save.
/// </summary>
internal sealed class TrajectoryWriter
{
    private readonly RecorderConfig _config;
    private readonly JsonSerializerOptions _options;

    internal TrajectoryWriter(RecorderConfig config)
    {
        _config = config;

        // Mirrors STS2MCP's options so recorded state matches its API output.
        // WriteIndented is the one knob the user controls; the rest must not
        // diverge. Notably absent: DictionaryKeyPolicy, so the state dict's
        // already-snake_case keys pass through untouched.
        _options = new JsonSerializerOptions
        {
            WriteIndented = config.PrettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }

    /// <summary>
    /// Builds the filename for a run: <c>run_20260829_204512_1756500312.json</c>.
    ///
    /// Fixed at run start and reused for every subsequent flush, so resuming a
    /// run appends to its original file instead of starting a new one.
    /// </summary>
    internal static string BuildFileName(DateTime startedAtUtc, string runId)
    {
        string date = startedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string time = startedAtUtc.ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"run_{date}_{time}_{Sanitize(runId)}.json";
    }

    /// <summary>
    /// Strips anything that cannot appear in a filename. Run ids come from the
    /// game (a numeric RunHistory.Id), but this must not be the thing that
    /// throws if that ever changes shape.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Writes the trajectory to <paramref name="fileName"/> in the configured
    /// output directory.
    ///
    /// Writes to a temporary file and moves it into place, so a crash mid-write
    /// cannot truncate an existing good recording. Returns the full path on
    /// success, or null if the write failed (already logged).
    /// </summary>
    internal string? Write(TrajectoryFile trajectory, string fileName)
    {
        string path = Path.Combine(_config.OutputDir, fileName);
        string tempPath = path + ".tmp";

        try
        {
            Directory.CreateDirectory(_config.OutputDir);

            string json = JsonSerializer.Serialize(trajectory, _options);
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);

            return path;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write recording to {path}", ex);
            TryDeleteTemp(tempPath);
            return null;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not clean up {tempPath}: {ex.Message}");
        }
    }
}
