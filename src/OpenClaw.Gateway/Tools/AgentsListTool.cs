using System.Text;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// List available sub-agent profiles that can be delegated to.
/// </summary>
internal sealed class AgentsListTool : ITool
{
    private readonly DelegationConfig _delegation;

    public AgentsListTool(DelegationConfig delegation)
    {
        _delegation = delegation;
    }

    public string Name => "agents_list";
    public string Description => "List available sub-agent profiles. Shows configured delegation profiles that can be used with delegate_agent.";
    public string ParameterSchema => """{"type":"object","properties":{}}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        if (!_delegation.Enabled || _delegation.Profiles.Count == 0)
            return ValueTask.FromResult("No agent profiles configured. Enable Delegation and add profiles to gateway config.");

        var sb = new StringBuilder();
        sb.AppendLine($"Available agents ({_delegation.Profiles.Count}):");
        sb.AppendLine($"  Max delegation depth: {_delegation.MaxDepth}");
        sb.AppendLine();

        foreach (var (name, profile) in _delegation.Profiles)
        {
            sb.AppendLine($"  [{name}]");
            if (!string.IsNullOrWhiteSpace(profile.SystemPrompt))
            {
                var preview = profile.SystemPrompt.Length > 80
                    ? profile.SystemPrompt[..80] + "…"
                    : profile.SystemPrompt;
                sb.AppendLine($"    Prompt: {preview}");
            }
            if (profile.AllowedTools.Length > 0)
                sb.AppendLine($"    Tools: {string.Join(", ", profile.AllowedTools)}");
            if (profile.MaxIterations > 0)
                sb.AppendLine($"    Max iterations: {profile.MaxIterations}");
            sb.AppendLine();
        }

        return ValueTask.FromResult(sb.ToString().TrimEnd());
    }
}
