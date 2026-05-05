using A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Core.Models;
using System.Text;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter.A2A;

public sealed class OpenClawA2AAgentHandler : IAgentHandler
{
    private readonly MafOptions _options;
    private readonly IOpenClawA2AExecutionBridge _bridge;
    private readonly ILogger<OpenClawA2AAgentHandler> _logger;

    public OpenClawA2AAgentHandler(
        IOptions<MafOptions> options,
        IOpenClawA2AExecutionBridge bridge,
        ILogger<OpenClawA2AAgentHandler> logger)
    {
        _options = options.Value;
        _bridge = bridge;
        _logger = logger;
    }

    public async Task ExecuteAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        var taskId = string.IsNullOrWhiteSpace(context.TaskId)
            ? Guid.NewGuid().ToString("N")
            : context.TaskId;
        var contextId = string.IsNullOrWhiteSpace(context.ContextId)
            ? taskId
            : context.ContextId;
        var responseText = new StringBuilder();
        string? errorMessage = null;

        try
        {
            await _bridge.ExecuteStreamingAsync(
                new OpenClawA2AExecutionRequest
                {
                    SessionId = taskId,
                    ChannelId = "a2a",
                    SenderId = contextId,
                    UserText = ExtractUserText(context),
                    MessageId = context.Message?.MessageId
                },
                (evt, ct) =>
                {
                    switch (evt.Type)
                    {
                        case AgentStreamEventType.TextDelta when !string.IsNullOrEmpty(evt.Content):
                            responseText.Append(evt.Content);
                            break;
                        case AgentStreamEventType.Error when !string.IsNullOrWhiteSpace(evt.Content):
                            errorMessage = evt.Content;
                            break;
                    }

                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2A execution failed for task {TaskId}", taskId);
            await eventQueue.EnqueueMessageAsync(
                CreateAgentMessage("A2A request failed.", taskId, contextId),
                cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            _logger.LogWarning("A2A execution failed for task {TaskId}: {Message}", taskId, errorMessage);
            await eventQueue.EnqueueMessageAsync(
                CreateAgentMessage(errorMessage, taskId, contextId),
                cancellationToken);
            return;
        }

        await eventQueue.EnqueueMessageAsync(
            responseText.Length > 0
                ? CreateAgentMessage(responseText.ToString(), taskId, contextId)
                : CreateAgentMessage($"[{OpenClawA2AAgent.GetDisplayName(_options)}] Request completed.", taskId, contextId),
            cancellationToken);
    }

    public async Task CancelAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken)
    {
        var taskId = string.IsNullOrWhiteSpace(context.TaskId)
            ? Guid.NewGuid().ToString("N")
            : context.TaskId;
        var contextId = string.IsNullOrWhiteSpace(context.ContextId)
            ? taskId
            : context.ContextId;
        var updater = new TaskUpdater(eventQueue, taskId, contextId);
        await updater.CancelAsync(cancellationToken);
    }

    private static string ExtractUserText(RequestContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.UserText))
            return context.UserText;

        if (context.Message?.Parts is not null)
        {
            var text = string.Concat(context.Message.Parts.Select(static part => part.Text));
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return string.Empty;
    }

    private static Message CreateAgentMessage(string text, string taskId, string contextId)
        => new()
        {
            Role = Role.Agent,
            MessageId = Guid.NewGuid().ToString("N"),
            TaskId = taskId,
            ContextId = contextId,
            Parts = [Part.FromText(text)]
        };
}