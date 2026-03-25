namespace OpenClaw.Agent.Plugins;

public sealed class BridgeProcessLaunchSpec
{
    public required string FileName { get; init; }
    public string[] Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);
}
