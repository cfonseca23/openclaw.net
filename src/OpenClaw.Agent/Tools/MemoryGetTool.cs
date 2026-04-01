using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Retrieve a specific memory note by key. Direct get without action parameter.
/// </summary>
public sealed class MemoryGetTool : ITool
{
    private readonly IMemoryStore _store;

    public MemoryGetTool(IMemoryStore store) => _store = store;

    public string Name => "memory_get";
    public string Description => "Retrieve a specific memory note by its key. Returns the note content or an error if not found.";
    public string ParameterSchema => """{"type":"object","properties":{"key":{"type":"string","description":"The memory note key to retrieve"}},"required":["key"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var key = root.TryGetProperty("key", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String
            ? k.GetString() : null;
        if (string.IsNullOrWhiteSpace(key))
            return "Error: 'key' is required.";

        var keyError = OpenClaw.Core.Security.InputSanitizer.CheckMemoryKey(key);
        if (keyError is not null)
            return $"Error: {keyError}";

        var content = await _store.LoadNoteAsync(key, ct);
        return content ?? $"Note '{key}' not found.";
    }
}
