using System.Text;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class OpenAiEndpoints
{
    private static OpenAiResponseStreamItem CreateFunctionCallItem(ResponsesToolState state, string status)
        => new()
        {
            Id = state.ItemId,
            Type = "function_call",
            Status = status,
            CallId = state.CallId,
            Name = state.ToolName,
            Arguments = state.Arguments
        };

    private static OpenAiResponseStreamItem CreateFunctionCallOutputItem(ResponsesToolState state, string output, string status)
        => new()
        {
            Id = state.ResultItemId ?? throw new InvalidOperationException("Tool output item id has not been assigned."),
            Type = "function_call_output",
            Status = status,
            CallId = state.CallId,
            Output = output
        };

    private static OpenAiResponseError CreateResponseError(string? errorCode, string message)
        => new()
        {
            Code = errorCode switch
            {
                "provider_failure" => "provider_error",
                "session_token_limit" => "session_limit_exceeded",
                "max_iterations" => "orchestration_limit_exceeded",
                _ => string.IsNullOrWhiteSpace(errorCode) ? "runtime_error" : errorCode
            },
            Message = message
        };

    private static OpenAiResponseStreamItem CreateMessageItem(string itemId, string text, string status)
        => new()
        {
            Id = itemId,
            Type = "message",
            Status = status,
            Role = "assistant",
            Content = [new OpenAiResponseContent { Text = text }]
        };

    private static List<OpenAiResponseOutput> BuildResponseOutputItems(
        IReadOnlyList<ChatTurn> addedTurns,
        string fallbackText)
    {
        var outputs = new List<OpenAiResponseOutput>();
        var nextToolIndex = 0;
        var nextMessageIndex = 0;
        var messageAdded = false;

        foreach (var turn in addedTurns)
        {
            if (turn.Content == "[tool_use]" && turn.ToolCalls is { Count: > 0 })
            {
                foreach (var invocation in turn.ToolCalls)
                {
                    var callId = $"call_openclaw_{++nextToolIndex}";
                    outputs.Add(new OpenAiResponseOutput
                    {
                        Id = $"fc_{nextToolIndex}",
                        Type = "function_call",
                        Status = "completed",
                        CallId = callId,
                        Name = invocation.ToolName,
                        Arguments = string.IsNullOrWhiteSpace(invocation.Arguments) ? "{}" : invocation.Arguments
                    });
                    outputs.Add(new OpenAiResponseOutput
                    {
                        Id = $"fco_{nextToolIndex}",
                        Type = "function_call_output",
                        Status = "completed",
                        CallId = callId,
                        Output = invocation.Result ?? ""
                    });
                }

                continue;
            }

            if (turn.Role == "assistant" && turn.Content != "[tool_use]")
            {
                outputs.Add(new OpenAiResponseOutput
                {
                    Id = $"msg_{++nextMessageIndex}",
                    Type = "message",
                    Status = "completed",
                    Role = "assistant",
                    Content = [new OpenAiResponseContent { Text = turn.Content }]
                });
                messageAdded = true;
            }
        }

        if (!messageAdded)
        {
            outputs.Add(new OpenAiResponseOutput
            {
                Id = $"msg_{++nextMessageIndex}",
                Type = "message",
                Status = "completed",
                Role = "assistant",
                Content = [new OpenAiResponseContent { Text = fallbackText }]
            });
        }

        return outputs;
    }

    private static IEnumerable<string> SplitArguments(string arguments, int chunkSize = 48)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            yield return "";
            yield break;
        }

        for (var index = 0; index < arguments.Length; index += chunkSize)
        {
            var length = Math.Min(chunkSize, arguments.Length - index);
            yield return arguments.Substring(index, length);
        }
    }

    private sealed class ResponsesToolState
    {
        public ResponsesToolState(
            int outputIndex,
            string itemId,
            string callId,
            string toolName,
            string arguments)
        {
            OutputIndex = outputIndex;
            ItemId = itemId;
            CallId = callId;
            ToolName = toolName;
            Arguments = arguments;
        }

        public int OutputIndex { get; }
        public string ItemId { get; }
        public string CallId { get; }
        public string ToolName { get; }
        public string Arguments { get; }
        public int? ResultOutputIndex { get; set; }
        public string? ResultItemId { get; set; }
        public StringBuilder ResultOutput { get; } = new();
    }

    private sealed class ResponsesTextState
    {
        public ResponsesTextState(int outputIndex, string itemId)
        {
            OutputIndex = outputIndex;
            ItemId = itemId;
        }

        public int OutputIndex { get; }
        public string ItemId { get; }
        public StringBuilder Content { get; } = new();
    }
}
