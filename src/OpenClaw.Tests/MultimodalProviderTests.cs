using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MultimodalProviderTests
{
    [Fact]
    public async Task ElevenLabsProvider_SynthesizeSpeechAsync_UsesConfiguredEndpointAndReturnsDataUrl()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var config = new GatewayConfig();
        config.Multimodal.ElevenLabs.Endpoint = "https://example.test";
        config.Multimodal.ElevenLabs.ApiKey = "raw:test-key";
        config.Multimodal.ElevenLabs.VoiceId = "voice123";
        config.Multimodal.ElevenLabs.Model = "eleven_turbo_v2";
        config.Multimodal.ElevenLabs.OutputFormat = "mp3_44100_128";

        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new CallbackHttpMessageHandler(request =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return response;
        });
        var provider = new ElevenLabsTextToSpeechProvider(
            config,
            new MediaCacheStore(storagePath),
            new HttpClient(handler));

        var result = await provider.SynthesizeSpeechAsync(new TextToSpeechRequest
        {
            Text = "Hello from ElevenLabs"
        }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("https://example.test/v1/text-to-speech/voice123?output_format=mp3_44100_128", captured!.RequestUri!.ToString());
        Assert.Equal("test-key", captured.Headers.GetValues("xi-api-key").Single());
        Assert.Contains("Hello from ElevenLabs", capturedBody, StringComparison.Ordinal);
        Assert.Equal("elevenlabs", result.Provider);
        Assert.Equal("audio/mpeg", result.Asset.MediaType);
        Assert.StartsWith("data:audio/mpeg;base64,", result.DataUrl, StringComparison.Ordinal);
        Assert.StartsWith("[AUDIO_URL:data:audio/mpeg;base64,", result.Marker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveSessionService_UsesRequestedProvider()
    {
        var providerA = new StubLiveSessionProvider("gemini");
        var providerB = new StubLiveSessionProvider("other");
        var service = new LiveSessionService(
            new GatewayConfig(),
            [providerA, providerB],
            NullLogger<LiveSessionService>.Instance);

        using var socket = new ClientWebSocket();
        await service.BridgeAsync(socket, new LiveSessionOpenRequest { Provider = "other" }, CancellationToken.None);

        Assert.False(providerA.WasCalled);
        Assert.True(providerB.WasCalled);
    }

    private sealed class CallbackHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _callback;

        public CallbackHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        {
            _callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_callback(request));
    }

    private sealed class StubLiveSessionProvider : ILiveSessionProvider
    {
        public StubLiveSessionProvider(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public bool WasCalled { get; private set; }

        public Task BridgeAsync(WebSocket clientSocket, LiveSessionOpenRequest request, CancellationToken ct)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }
}
