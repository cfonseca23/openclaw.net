using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class GeminiTextToSpeechProvider : ITextToSpeechProvider
{
    private readonly GeminiMultimodalService _gemini;

    public GeminiTextToSpeechProvider(GeminiMultimodalService gemini)
    {
        _gemini = gemini;
    }

    public string Name => "gemini";

    public Task<TextToSpeechSynthesisResult> SynthesizeSpeechAsync(TextToSpeechRequest request, CancellationToken ct)
        => _gemini.SynthesizeSpeechAsync(request.Text, request.VoiceName, request.Model, ct);
}
