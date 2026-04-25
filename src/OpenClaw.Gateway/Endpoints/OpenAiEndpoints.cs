using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private const int MaxChatCompletionRequestBytes = 1024 * 1024;
    private const string StableSessionHeader = "X-OpenClaw-Session-Id";

    public static void MapOpenClawOpenAiEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        MapChatCompletionsEndpoint(app, startup, runtime);
        MapResponsesEndpoint(app, startup, runtime);
    }
}
