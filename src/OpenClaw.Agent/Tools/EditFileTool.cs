using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Targeted search-and-replace editing of files. Safer than full write_file for small changes.
/// </summary>
public sealed class EditFileTool : ITool
{
    private readonly ToolingConfig _config;

    public EditFileTool(ToolingConfig config) => _config = config;

    public string Name => "edit_file";
    public string Description => "Edit a file by replacing a specific text string with new text. Safer than write_file for targeted changes.";
    public string ParameterSchema => """{"type":"object","properties":{"path":{"type":"string","description":"File path to edit"},"old_text":{"type":"string","description":"Exact text to find and replace"},"new_text":{"type":"string","description":"Replacement text"},"replace_all":{"type":"boolean","description":"Replace all occurrences (default: false)"}},"required":["path","old_text","new_text"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (_config.ReadOnlyMode)
            return "Error: edit_file is disabled because Tooling.ReadOnlyMode is enabled.";

        using var args = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var path = GetString(root, "path");
        if (string.IsNullOrWhiteSpace(path))
            return "Error: 'path' is required.";

        var oldText = GetString(root, "old_text");
        if (oldText is null || oldText.Length == 0)
            return "Error: 'old_text' is required and must not be empty.";

        var newText = GetString(root, "new_text");
        if (newText is null)
            return "Error: 'new_text' is required.";

        var replaceAll = root.TryGetProperty("replace_all", out var ra) &&
            ra.ValueKind == System.Text.Json.JsonValueKind.True;

        var resolvedPath = ToolPathPolicy.ResolveRealPath(path);

        if (!ToolPathPolicy.IsWriteAllowed(_config, resolvedPath))
            return $"Error: Write access denied for path: {path}";

        if (!File.Exists(resolvedPath))
            return $"Error: File not found: {path}";

        var content = await File.ReadAllTextAsync(resolvedPath, ct);

        if (!content.Contains(oldText, StringComparison.Ordinal))
            return "Error: 'old_text' not found in file.";

        if (!replaceAll)
        {
            var firstIdx = content.IndexOf(oldText, StringComparison.Ordinal);
            var lastIdx = content.LastIndexOf(oldText, StringComparison.Ordinal);
            if (firstIdx != lastIdx)
                return "Error: 'old_text' appears multiple times. Set replace_all=true or provide more context to make it unique.";
        }

        var updated = replaceAll
            ? content.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceFirst(content, oldText, newText);

        var tmp = resolvedPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, updated, ct);
            File.Move(tmp, resolvedPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }

        var count = replaceAll ? CountOccurrences(content, oldText) : 1;
        return $"Replaced {count} occurrence(s) in {path}.";
    }

    private static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var idx = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (idx < 0) return source;
        return string.Concat(source.AsSpan(0, idx), newValue, source.AsSpan(idx + oldValue.Length));
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }

    private static string? GetString(System.Text.Json.JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;
}
