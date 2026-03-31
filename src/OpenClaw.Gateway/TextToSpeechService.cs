using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class TextToSpeechRequest
{
    public required string Text { get; init; }
    public string? Provider { get; init; }
    public string? VoiceName { get; init; }
    public string? VoiceId { get; init; }
    public string? Model { get; init; }
}

internal sealed class TextToSpeechSynthesisResult
{
    public required string Provider { get; init; }
    public required StoredMediaAsset Asset { get; init; }
    public required string Marker { get; init; }
    public required string DataUrl { get; init; }
}

internal interface ITextToSpeechProvider
{
    string Name { get; }
    Task<TextToSpeechSynthesisResult> SynthesizeSpeechAsync(TextToSpeechRequest request, CancellationToken ct);
}

internal interface ILiveSessionProvider
{
    string Name { get; }
    Task BridgeAsync(WebSocket clientSocket, LiveSessionOpenRequest request, CancellationToken ct);
}

internal sealed class TextToSpeechService
{
    private readonly GatewayConfig _config;
    private readonly IReadOnlyDictionary<string, ITextToSpeechProvider> _providers;

    public TextToSpeechService(GatewayConfig config, IEnumerable<ITextToSpeechProvider> providers)
    {
        _config = config;
        _providers = providers.ToDictionary(static provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    public Task<TextToSpeechSynthesisResult> SynthesizeSpeechAsync(TextToSpeechRequest request, CancellationToken ct)
    {
        if (!_config.Multimodal.Enabled || !_config.Multimodal.TextToSpeech.Enabled)
            throw new InvalidOperationException("Text-to-speech is disabled by configuration.");

        var providerName = string.IsNullOrWhiteSpace(request.Provider)
            ? _config.Multimodal.TextToSpeech.Provider
            : request.Provider.Trim();

        if (!_providers.TryGetValue(providerName, out var provider))
            throw new InvalidOperationException($"Unknown text-to-speech provider '{providerName}'.");

        return provider.SynthesizeSpeechAsync(request, ct);
    }

    public IReadOnlyList<string> ListProviders()
        => _providers.Keys.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray();
}

internal sealed class LiveSessionService
{
    private readonly GatewayConfig _config;
    private readonly ILogger<LiveSessionService> _logger;
    private readonly IReadOnlyDictionary<string, ILiveSessionProvider> _providers;

    public LiveSessionService(
        GatewayConfig config,
        IEnumerable<ILiveSessionProvider> providers,
        ILogger<LiveSessionService> logger)
    {
        _config = config;
        _logger = logger;
        _providers = providers.ToDictionary(static provider => provider.Name, StringComparer.OrdinalIgnoreCase);
    }

    public async Task BridgeAsync(WebSocket clientSocket, LiveSessionOpenRequest request, CancellationToken ct)
    {
        if (!_config.Multimodal.Enabled)
            throw new InvalidOperationException("Multimodal live sessions are disabled by configuration.");

        var providerName = string.IsNullOrWhiteSpace(request.Provider)
            ? _config.Multimodal.LiveProvider
            : request.Provider.Trim();

        if (!_providers.TryGetValue(providerName, out var provider))
            throw new InvalidOperationException($"Unknown live session provider '{providerName}'.");

        _logger.LogInformation("Starting live session bridge with provider {Provider}", providerName);
        await provider.BridgeAsync(clientSocket, request, ct);
    }

    public IReadOnlyList<string> ListProviders()
        => _providers.Keys.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray();
}
