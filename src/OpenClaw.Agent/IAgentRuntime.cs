using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

public interface IAgentRuntime
{
    CircuitState CircuitBreakerState { get; }

    Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null);

    IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null);
}
