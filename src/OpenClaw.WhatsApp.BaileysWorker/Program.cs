using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.WhatsApp.BaileysWorker;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!args.Contains("--stdio", StringComparer.Ordinal))
            return 2;

        var service = new WhatsAppWorkerService();
        var writerGate = new SemaphoreSlim(1, 1);

        service.Notification += async notification =>
        {
            var payload = JsonSerializer.Serialize(notification, CoreJsonContext.Default.BridgeNotification);
            await writerGate.WaitAsync();
            try
            {
                await Console.Out.WriteLineAsync(payload);
                await Console.Out.FlushAsync();
            }
            finally
            {
                writerGate.Release();
            }
        };

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            BridgeResponse response;
            try
            {
                var request = JsonSerializer.Deserialize(line, CoreJsonContext.Default.BridgeRequest);
                response = request is null
                    ? Error("unknown", -32700, "Request payload was empty.")
                    : await HandleRequestAsync(service, request);
            }
            catch (Exception ex)
            {
                response = Error("unknown", -32603, ex.Message);
            }

            var responseJson = JsonSerializer.Serialize(response, CoreJsonContext.Default.BridgeResponse);
            await writerGate.WaitAsync();
            try
            {
                await Console.Out.WriteLineAsync(responseJson);
                await Console.Out.FlushAsync();
            }
            finally
            {
                writerGate.Release();
            }

            if (string.Equals(line, "__shutdown__", StringComparison.Ordinal))
                break;
        }

        await service.DisposeAsync();
        return 0;
    }

    private static async Task<BridgeResponse> HandleRequestAsync(WhatsAppWorkerService service, BridgeRequest request)
    {
        try
        {
            return request.Method switch
            {
                "init" => Ok(request.Id, await service.InitializeAsync(Parse(request.Params, CoreJsonContext.Default.BridgeInitRequest))),
                "channel_start" => Ok(request.Id, await service.StartAsync(Parse(request.Params, CoreJsonContext.Default.BridgeChannelControlRequest))),
                "channel_stop" => Ok(request.Id, await service.StopAsync(Parse(request.Params, CoreJsonContext.Default.BridgeChannelControlRequest))),
                "channel_send" => Ok(request.Id, await service.SendAsync(Parse(request.Params, CoreJsonContext.Default.BridgeChannelSendRequest))),
                "channel_typing" => Ok(request.Id, await service.SendTypingAsync(Parse(request.Params, CoreJsonContext.Default.BridgeChannelTypingRequest))),
                "channel_read_receipt" => Ok(request.Id, await service.SendReadReceiptAsync(Parse(request.Params, CoreJsonContext.Default.BridgeChannelReceiptRequest))),
                "channel_react" => Ok(request.Id, await service.SendReactionAsync(Parse(request.Params, CoreJsonContext.Default.BridgeChannelReactionRequest))),
                "debug_simulate_inbound" => Ok(request.Id, await service.DebugSimulateInboundAsync(request.Params)),
                "debug_emit_auth_event" => Ok(request.Id, await service.DebugEmitAuthEventAsync(request.Params)),
                "debug_get_state" => Ok(request.Id, service.DebugGetState()),
                "shutdown" => Ok(request.Id, await service.ShutdownAsync()),
                _ => Error(request.Id, -32601, $"Unsupported method '{request.Method}'.")
            };
        }
        catch (Exception ex)
        {
            return Error(request.Id, -32603, ex.Message);
        }
    }

    private static T Parse<T>(JsonElement? element, JsonTypeInfo<T> typeInfo)
    {
        if (element is not { } payload)
            throw new InvalidOperationException("Request params are required.");

        var value = payload.Deserialize(typeInfo);
        return value ?? throw new InvalidOperationException("Request params were invalid.");
    }

    private static BridgeResponse Ok(string id, object? value)
        => new()
        {
            Id = id,
            Result = value is null ? null : JsonSerializer.SerializeToElement(value)
        };

    private static BridgeResponse Error(string id, int code, string message)
        => new()
        {
            Id = id,
            Error = new BridgeError
            {
                Code = code,
                Message = message
            }
        };
}
