using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Apply a unified diff patch to a file. Supports multi-hunk patches.
/// </summary>
public sealed class ApplyPatchTool : ITool
{
    private readonly ToolingConfig _config;

    public ApplyPatchTool(ToolingConfig config) => _config = config;

    public string Name => "apply_patch";
    public string Description => "Apply a unified diff patch to a file. Supports multi-hunk patches for complex edits.";
    public string ParameterSchema => """{"type":"object","properties":{"path":{"type":"string","description":"File path to patch"},"patch":{"type":"string","description":"Unified diff patch content (lines starting with +/- and @@ hunk headers)"}},"required":["path","patch"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (_config.ReadOnlyMode)
            return "Error: apply_patch is disabled because Tooling.ReadOnlyMode is enabled.";

        using var args = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var path = GetString(root, "path");
        if (string.IsNullOrWhiteSpace(path))
            return "Error: 'path' is required.";

        var patch = GetString(root, "patch");
        if (string.IsNullOrWhiteSpace(patch))
            return "Error: 'patch' is required.";

        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);

        if (!ToolPathPolicy.IsWriteAllowed(_config, resolvedPath))
            return $"Error: Write access denied for path: {path}";

        if (!File.Exists(resolvedPath))
            return $"Error: File not found: {path}";

        var originalLines = await File.ReadAllLinesAsync(resolvedPath, ct);
        var hunks = ParseHunks(patch);

        if (hunks.Count == 0)
            return "Error: No valid hunks found in patch. Use @@ -start,count +start,count @@ headers.";

        var result = new List<string>(originalLines);
        var offset = 0;

        foreach (var hunk in hunks)
        {
            var startLine = hunk.OriginalStart - 1 + offset;
            if (startLine < 0 || startLine > result.Count)
                return $"Error: Hunk at line {hunk.OriginalStart} is out of range (file has {result.Count} lines).";

            // Validate removed lines match file content
            if (startLine + hunk.RemoveLines.Count > result.Count)
                return $"Error: Hunk at line {hunk.OriginalStart} expects {hunk.RemoveLines.Count} lines to remove, but only {result.Count - startLine} lines remain.";

            for (var i = 0; i < hunk.RemoveLines.Count; i++)
            {
                var expected = hunk.RemoveLines[i];
                var actual = result[startLine + i];
                if (!string.Equals(expected.TrimEnd(), actual.TrimEnd(), StringComparison.Ordinal))
                    return $"Error: Hunk at line {hunk.OriginalStart + i} mismatch. Expected: \"{Truncate(expected, 60)}\" Got: \"{Truncate(actual, 60)}\"";
            }

            // Remove old lines (validated above)
            for (var i = 0; i < hunk.RemoveLines.Count; i++)
                result.RemoveAt(startLine);

            // Insert new lines
            for (var i = hunk.AddLines.Count - 1; i >= 0; i--)
                result.Insert(startLine, hunk.AddLines[i]);

            offset += hunk.AddLines.Count - hunk.RemoveLines.Count;
        }

        var tmp = resolvedPath + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(tmp, result, ct);
            File.Move(tmp, resolvedPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }

        return $"Applied {hunks.Count} hunk(s) to {path}.";
    }

    private sealed record Hunk(int OriginalStart, List<string> RemoveLines, List<string> AddLines);

    private static List<Hunk> ParseHunks(string patch)
    {
        var hunks = new List<Hunk>();
        var lines = patch.Split('\n');
        Hunk? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current is not null)
                    hunks.Add(current);

                var origStart = ParseHunkStart(line);
                current = new Hunk(origStart, [], []);
            }
            else if (current is not null)
            {
                if (line.StartsWith('-'))
                    current.RemoveLines.Add(line[1..]);
                else if (line.StartsWith('+'))
                    current.AddLines.Add(line[1..]);
                // Context lines (starting with space) are skipped — we trust line numbers
            }
        }

        if (current is not null)
            hunks.Add(current);

        return hunks;
    }

    private static int ParseHunkStart(string header)
    {
        // Parse @@ -start,count +start,count @@
        var idx = header.IndexOf('-', 3);
        if (idx < 0) return 1;
        var comma = header.IndexOf(',', idx);
        var end = comma > 0 ? comma : header.IndexOf(' ', idx + 1);
        if (end < 0) end = header.Length;
        return int.TryParse(header.AsSpan(idx + 1, end - idx - 1), out var start) ? start : 1;
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";

    private static string? GetString(System.Text.Json.JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;
}
