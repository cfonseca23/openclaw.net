using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class GeminiLiveProxyService : ILiveSessionProvider
{
    private readonly GatewayConfig _config;
    private readonly ILogger<GeminiLiveProxyService> _logger;

    public GeminiLiveProxyService(GatewayConfig config, ILogger<GeminiLiveProxyService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public string Name => "gemini";

    public async Task BridgeAsync(
        System.Net.WebSockets.WebSocket clientSocket,
        LiveSessionOpenRequest request,
        CancellationToken ct)
    {
        if (!_config.Multimodal.Enabled || !_config.Multimodal.GeminiLive.Enabled)
            throw new InvalidOperationException("Gemini Live is disabled by configuration.");

        using var geminiSocket = new ClientWebSocket();
        var apiKey = SecretResolver.Resolve(_config.Llm.ApiKey)
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
            ?? _config.Llm.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("A Google Gemini API key is required for Gemini Live.");

        geminiSocket.Options.SetRequestHeader("x-goog-api-key", apiKey);
        await geminiSocket.ConnectAsync(new Uri(_config.Multimodal.GeminiLive.Endpoint, UriKind.Absolute), ct);
        await SendGeminiMessageAsync(geminiSocket, BuildSetupPayload(request), ct);
        if (!await WaitForSetupCompleteAsync(geminiSocket, clientSocket, request, ct))
            return;

        var serverToClient = RelayGeminiToClientAsync(geminiSocket, clientSocket, ct);
        var clientToServer = RelayClientToGeminiAsync(clientSocket, geminiSocket, ct);
        await Task.WhenAny(serverToClient, clientToServer);

        try
        {
            if (geminiSocket.State == WebSocketState.Open)
                await geminiSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        }
        catch
        {
        }
    }

    private async Task RelayClientToGeminiAsync(System.Net.WebSockets.WebSocket clientSocket, ClientWebSocket geminiSocket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && clientSocket.State == WebSocketState.Open && geminiSocket.State == WebSocketState.Open)
        {
            var message = await ReceiveTextAsync(clientSocket, ct);
            if (message is null)
                break;

            var envelope = JsonSerializer.Deserialize(message, CoreJsonContext.Default.LiveClientEnvelope);
            if (envelope is null)
                continue;

            var payload = envelope.Type switch
            {
                "text" => BuildClientTextPayload(envelope.Text ?? "", envelope.TurnComplete),
                "audio" => BuildClientAudioPayload(envelope.Base64Data ?? "", envelope.MimeType ?? "audio/pcm;rate=16000", envelope.TurnComplete),
                "audio_end" => BuildClientAudioEndPayload(),
                "interrupt" => BuildClientInterruptPayload(),
                "close" => null,
                _ => null
            };

            if (envelope.Type == "close")
                break;

            if (!string.IsNullOrWhiteSpace(payload))
                await SendGeminiMessageAsync(geminiSocket, payload, ct);
        }
    }

    private async Task RelayGeminiToClientAsync(ClientWebSocket geminiSocket, System.Net.WebSockets.WebSocket clientSocket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && geminiSocket.State == WebSocketState.Open && clientSocket.State == WebSocketState.Open)
        {
            var message = await ReceiveTextAsync(geminiSocket, ct);
            if (message is null)
                break;

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!await RelayGeminiMessageAsync(root, clientSocket, ct))
                break;
        }
    }

    private async Task<bool> WaitForSetupCompleteAsync(
        ClientWebSocket geminiSocket,
        System.Net.WebSockets.WebSocket clientSocket,
        LiveSessionOpenRequest request,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && geminiSocket.State == WebSocketState.Open && clientSocket.State == WebSocketState.Open)
        {
            var message = await ReceiveTextAsync(geminiSocket, ct);
            if (message is null)
                return false;

            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.TryGetProperty("setupComplete", out _))
            {
                await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
                {
                    Type = "opened",
                    Text = request.Model ?? _config.Multimodal.GeminiLive.Model
                }, ct);
                return true;
            }

            if (!await RelayGeminiMessageAsync(root, clientSocket, ct))
                return false;
        }

        return false;
    }

    private async Task<bool> RelayGeminiMessageAsync(JsonElement root, System.Net.WebSockets.WebSocket clientSocket, CancellationToken ct)
    {
        if (root.TryGetProperty("error", out var error))
        {
            await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
            {
                Type = "error",
                Error = ExtractError(error)
            }, ct);
            return false;
        }

        if (root.TryGetProperty("goAway", out var goAway))
        {
            await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
            {
                Type = "error",
                Error = ExtractError(goAway)
            }, ct);
            return false;
        }

        if (root.TryGetProperty("toolCall", out _))
        {
            await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
            {
                Type = "error",
                Error = "Gemini Live tool calls are not supported by this proxy."
            }, ct);
            return true;
        }

        if (root.TryGetProperty("toolCallCancellation", out _))
        {
            await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
            {
                Type = "interrupted",
                Interrupted = true
            }, ct);
        }

        if (root.TryGetProperty("serverContent", out var serverContent))
            await RelayServerContentAsync(serverContent, clientSocket, ct);

        if (root.TryGetProperty("inputTranscription", out var inputTranscription))
            await RelayTranscriptionAsync(clientSocket, "input_transcription", inputTranscription, ct);

        if (root.TryGetProperty("outputTranscription", out var outputTranscription))
            await RelayTranscriptionAsync(clientSocket, "output_transcription", outputTranscription, ct);

        return true;
    }

    private static async Task RelayServerContentAsync(
        JsonElement serverContent,
        System.Net.WebSockets.WebSocket clientSocket,
        CancellationToken ct)
    {
        if (serverContent.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
        {
            await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
            {
                Type = "interrupted",
                Interrupted = true
            }, ct);
        }

        if (serverContent.TryGetProperty("inputTranscription", out var inputTranscription))
            await RelayTranscriptionAsync(clientSocket, "input_transcription", inputTranscription, ct);

        if (serverContent.TryGetProperty("outputTranscription", out var outputTranscription))
            await RelayTranscriptionAsync(clientSocket, "output_transcription", outputTranscription, ct);

        if (serverContent.TryGetProperty("modelTurn", out var modelTurn) &&
            modelTurn.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
                    {
                        Type = "text",
                        Text = text.GetString()
                    }, ct);
                }

                if (TryGetInlineData(part, out var data, out var mimeType))
                {
                    await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
                    {
                        Type = "audio",
                        Base64Data = data,
                        MimeType = mimeType
                    }, ct);
                }
            }
        }

        if (serverContent.TryGetProperty("turnComplete", out var turnComplete) && turnComplete.ValueKind == JsonValueKind.True)
        {
            await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
            {
                Type = "turn_complete",
                TurnComplete = true
            }, ct);
        }
    }

    private static async Task RelayTranscriptionAsync(
        System.Net.WebSockets.WebSocket clientSocket,
        string type,
        JsonElement transcription,
        CancellationToken ct)
    {
        var text = transcription.ValueKind == JsonValueKind.String
            ? transcription.GetString()
            : transcription.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString()
                : transcription.GetRawText();

        if (string.IsNullOrWhiteSpace(text))
            return;

        await SendClientEnvelopeAsync(clientSocket, new LiveServerEnvelope
        {
            Type = type,
            Text = text
        }, ct);
    }

    private string BuildSetupPayload(LiveSessionOpenRequest request)
    {
        var model = request.Model ?? _config.Multimodal.GeminiLive.Model;
        var modalities = request.ResponseModalities?.Length > 0
            ? request.ResponseModalities
            : _config.Multimodal.GeminiLive.ResponseModalities;

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WritePropertyName("setup");
        writer.WriteStartObject();
        writer.WriteString("model", $"models/{model}");
        writer.WritePropertyName("generationConfig");
        writer.WriteStartObject();
        writer.WritePropertyName("responseModalities");
        writer.WriteStartArray();
        foreach (var modality in modalities)
            writer.WriteStringValue(modality);
        writer.WriteEndArray();
        if (modalities.Any(static item => string.Equals(item, "AUDIO", StringComparison.OrdinalIgnoreCase)))
        {
            writer.WritePropertyName("speechConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("voiceConfig");
            writer.WriteStartObject();
            writer.WritePropertyName("prebuiltVoiceConfig");
            writer.WriteStartObject();
            writer.WriteString("voiceName", request.VoiceName ?? _config.Multimodal.GeminiLive.VoiceName ?? _config.Multimodal.TextToSpeech.VoiceName);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            writer.WritePropertyName("systemInstruction");
            writer.WriteStartObject();
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", request.SystemInstruction);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (_config.Multimodal.GeminiLive.InputTranscription)
        {
            writer.WritePropertyName("inputAudioTranscription");
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        if (_config.Multimodal.GeminiLive.OutputTranscription)
        {
            writer.WritePropertyName("outputAudioTranscription");
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildClientTextPayload(string text, bool turnComplete)
        => $"{{\"clientContent\":{{\"turns\":[{{\"role\":\"user\",\"parts\":[{{\"text\":\"{EscapeJsonString(text)}\"}}]}}],\"turnComplete\":{(turnComplete ? "true" : "false")}}}}}";

    private static string BuildClientAudioPayload(string base64Data, string mimeType, bool turnComplete)
        => $"{{\"realtimeInput\":{{\"audio\":{{\"data\":\"{EscapeJsonString(base64Data)}\",\"mimeType\":\"{EscapeJsonString(mimeType)}\"}}{(turnComplete ? ",\"audioStreamEnd\":true" : string.Empty)}}}}}";

    private static string BuildClientInterruptPayload()
        => """{"clientContent":{"turns":[],"turnComplete":false}}""";

    private static string BuildClientAudioEndPayload()
        => """{"realtimeInput":{"audioStreamEnd":true}}""";

    private static async Task SendGeminiMessageAsync(ClientWebSocket socket, string payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task SendClientEnvelopeAsync(System.Net.WebSockets.WebSocket socket, LiveServerEnvelope envelope, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(envelope, CoreJsonContext.Default.LiveServerEnvelope);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveTextAsync(System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static bool TryGetInlineData(JsonElement part, out string? base64Data, out string? mimeType)
    {
        base64Data = null;
        mimeType = null;

        if (!part.TryGetProperty("inlineData", out var inlineData) && !part.TryGetProperty("inline_data", out inlineData))
            return false;

        base64Data = inlineData.TryGetProperty("data", out var data) ? data.GetString() : null;
        mimeType = inlineData.TryGetProperty("mimeType", out var mime) ? mime.GetString()
            : inlineData.TryGetProperty("mime_type", out mime) ? mime.GetString()
            : "audio/pcm";
        return !string.IsNullOrWhiteSpace(base64Data);
    }

    private static string ExtractError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? "Gemini Live error.";

        if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? "Gemini Live error.";

        if (error.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            return reason.GetString() ?? "Gemini Live error.";

        return error.GetRawText();
    }

    private static string EscapeJsonString(string value)
        => JsonEncodedText.Encode(value).ToString();
}
