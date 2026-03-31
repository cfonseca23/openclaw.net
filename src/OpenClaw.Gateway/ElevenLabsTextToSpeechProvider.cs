using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class ElevenLabsTextToSpeechProvider : ITextToSpeechProvider
{
    private readonly GatewayConfig _config;
    private readonly MediaCacheStore _mediaCache;
    private readonly HttpClient _httpClient;

    public ElevenLabsTextToSpeechProvider(GatewayConfig config, MediaCacheStore mediaCache, HttpClient? httpClient = null)
    {
        _config = config;
        _mediaCache = mediaCache;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
    }

    public string Name => "elevenlabs";

    public async Task<TextToSpeechSynthesisResult> SynthesizeSpeechAsync(TextToSpeechRequest request, CancellationToken ct)
    {
        if (!_config.Multimodal.ElevenLabs.Enabled)
            throw new InvalidOperationException("ElevenLabs text-to-speech is disabled by configuration.");

        var apiKey = SecretResolver.Resolve(_config.Multimodal.ElevenLabs.ApiKey)
            ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")
            ?? _config.Multimodal.ElevenLabs.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("An ElevenLabs API key is required for ElevenLabs text-to-speech.");

        var voiceId = !string.IsNullOrWhiteSpace(request.VoiceId)
            ? request.VoiceId.Trim()
            : _config.Multimodal.TextToSpeech.VoiceId ?? _config.Multimodal.ElevenLabs.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new InvalidOperationException("An ElevenLabs voice_id is required.");

        var modelId = !string.IsNullOrWhiteSpace(request.Model)
            ? request.Model.Trim()
            : _config.Multimodal.ElevenLabs.Model;
        var outputFormat = _config.Multimodal.ElevenLabs.OutputFormat;
        var endpoint = _config.Multimodal.ElevenLabs.Endpoint.TrimEnd('/');
        var uri = new Uri($"{endpoint}/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format={Uri.EscapeDataString(outputFormat)}", UriKind.Absolute);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Add("xi-api-key", apiKey);
        httpRequest.Content = new StringContent(BuildRequestBody(request.Text, modelId), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var audioBytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var payload = Encoding.UTF8.GetString(audioBytes);
            throw new InvalidOperationException($"ElevenLabs speech request failed: {(int)response.StatusCode} {payload}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? GuessMediaType(outputFormat);
        var fileName = mediaType switch
        {
            "audio/mpeg" => "speech.mp3",
            "audio/wav" or "audio/x-wav" => "speech.wav",
            _ => "speech.bin"
        };
        var asset = await _mediaCache.SaveAsync(audioBytes, mediaType, fileName, ct);
        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(audioBytes)}";
        return new TextToSpeechSynthesisResult
        {
            Provider = Name,
            Asset = asset,
            Marker = $"[AUDIO_URL:{dataUrl}]",
            DataUrl = dataUrl
        };
    }

    private static string BuildRequestBody(string text, string modelId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("text", text);
        writer.WriteString("model_id", modelId);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string GuessMediaType(string outputFormat)
        => outputFormat.StartsWith("mp3", StringComparison.OrdinalIgnoreCase)
            ? "audio/mpeg"
            : outputFormat.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) || outputFormat.StartsWith("wav", StringComparison.OrdinalIgnoreCase)
                ? "audio/wav"
                : "application/octet-stream";
}
