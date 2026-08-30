using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// The date and time are when recording began; the suffix is the run's own
    /// id. It is rebuilt once, when the session adopts its real id at the first
    /// save, and fixed for every flush after that - so a run is written to one
    /// file, and its name carries the id that identifies the run across saves.
    /// </summary>
    internal static string BuildFileName(DateTime startedAtUtc, string runId)
    {
        string date = startedAtUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string time = startedAtUtc.ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"run_{date}_{time}_{Sanitize(runId)}.json";
    }

    /// <summary>
    /// Strips anything that cannot appear in a filename. Run ids are the game's
    /// own epoch-seconds start time, but this must not be the thing that throws
    /// if that ever changes shape.
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
    /// Finds the recording already on disk for <paramref name="runId"/>, or null
    /// if there is none.
    ///
    /// Runs are looked up by id rather than by filename because the timestamp in
    /// the name belongs to the sitting that started the recording, not to the
    /// run. This is what lets a run picked back up days later be appended to its
    /// own file instead of starting a second one.
    /// </summary>
    internal string? FindExisting(string runId)
    {
        try
        {
            if (!Directory.Exists(_config.OutputDir)) return null;

            var matches = Directory.GetFiles(_config.OutputDir, $"run_*_{Sanitize(runId)}.json");

            // More than one can only happen if an older build wrote a second
            // file for the run. The newest is the one with the most in it.
            return matches.Length == 0
                ? null
                : matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not look for an existing recording of run {runId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads a recording back off disk, or returns null if it cannot be read as
    /// one (already logged). State objects come back as <c>JsonElement</c>s and
    /// re-serialize unchanged, so a reloaded file is written out exactly as it
    /// came in.
    /// </summary>
    internal TrajectoryFile? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TrajectoryFile>(File.ReadAllText(path), _options);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the existing recording at {path} ({ex.Message}); " +
                     "starting a new file for this run.");
            return null;
        }
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
