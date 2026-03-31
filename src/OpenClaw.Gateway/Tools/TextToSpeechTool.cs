using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Gateway.Tools;

internal sealed class TextToSpeechTool : ITool
{
    private readonly TextToSpeechService _speech;

    public TextToSpeechTool(TextToSpeechService speech)
    {
        _speech = speech;
    }

    public string Name => "text_to_speech";
    public string Description => "Generate speech audio from text using the configured native text-to-speech provider.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "text":{"type":"string"},
        "provider":{"type":"string"},
        "voice_id":{"type":"string"},
        "voice_name":{"type":"string"},
        "model":{"type":"string"}
      },
      "required":["text"]
    }
    """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var text = GetString(root, "text");
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text is required.";

        var result = await _speech.SynthesizeSpeechAsync(
            new TextToSpeechRequest
            {
                Text = text,
                Provider = GetString(root, "provider"),
                VoiceId = GetString(root, "voice_id"),
                VoiceName = GetString(root, "voice_name"),
                Model = GetString(root, "model")
            },
            ct);

        return $"provider: {result.Provider}\nasset_id: {result.Asset.Id}\nmedia_type: {result.Asset.MediaType}\n{result.Marker}";
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
