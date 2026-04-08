using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Extensions;
using OpenClaw.Gateway.Models;
using OpenClaw.Gateway.PromptCaching;
using Xunit;

namespace OpenClaw.Tests;

[Collection(DynamicProviderRegistryCollection.Name)]
public sealed class PromptCachingTests
{
    [Fact]
    public void ConfigValidator_RejectsOpenAiCompatiblePromptCachingWithoutExplicitDialect()
    {
        var config = new GatewayConfig
        {
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "gemma4-prod",
                        Provider = "openai-compatible",
                        Model = "gemma-4",
                        BaseUrl = "https://example.invalid/v1",
                        ApiKey = "raw:test",
                        PromptCaching = new PromptCachingConfig
                        {
                            Enabled = true,
                            Dialect = "auto"
                        }
                    }
                ]
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("Models.Profiles.gemma4-prod.PromptCaching.Dialect", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_MergesProfilePromptCachingOverrideOverGlobalDefaults()
    {
        LlmClientFactory.ResetDynamicProviders();
        LlmClientFactory.RegisterProvider("fake-profile-tests", new TestChatClient());

        var config = new GatewayConfig
        {
            Llm = new LlmProviderConfig
            {
                Provider = "fake-profile-tests",
                Model = "legacy-model",
                PromptCaching = new PromptCachingConfig
                {
                    Enabled = true,
                    Dialect = "openai",
                    Retention = "long",
                    TraceEnabled = true
                }
            },
            Models = new ModelsConfig
            {
                Profiles =
                [
                    new ModelProfileConfig
                    {
                        Id = "profile-a",
                        Provider = "fake-profile-tests",
                        Model = "model-a",
                        PromptCaching = new PromptCachingConfig
                        {
                            Retention = "short"
                        }
                    }
                ]
            }
        };

        var registry = new ConfiguredModelProfileRegistry(config, NullLogger<ConfiguredModelProfileRegistry>.Instance);

        Assert.True(registry.TryGet("profile-a", out var profile));
        Assert.True(profile!.PromptCaching.Enabled == true);
        Assert.Equal("openai", profile.PromptCaching.Dialect);
        Assert.Equal("short", profile.PromptCaching.Retention);
        Assert.True(profile.PromptCaching.TraceEnabled == true);
    }

    [Fact]
    public void Coordinator_UsesDeterministicFingerprintAcrossVolatileSuffixAndToolOrdering()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = Path.Combine(Path.GetTempPath(), "openclaw-prompt-cache-tests", Guid.NewGuid().ToString("N"))
            }
        };
        Directory.CreateDirectory(config.Memory.StoragePath);
        var coordinator = new PromptCacheCoordinator(config, new PromptCacheTraceWriter(config));
        var profile = CreateProfile("anthropic", "anthropic");
        var session = new Session { Id = "s1", ChannelId = "test", SenderId = "user" };

        var toolA = AIFunctionFactory.CreateDeclaration(
            "tool_a",
            "Tool A",
            JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement.Clone(),
            returnJsonSchema: null);
        var toolB = AIFunctionFactory.CreateDeclaration(
            "tool_b",
            "Tool B",
            JsonDocument.Parse("""{"type":"object","properties":{"count":{"type":"integer"}}}""").RootElement.Clone(),
            returnJsonSchema: null);

        var first = coordinator.Prepare(
            session,
            profile,
            profile.ModelId,
            [new ChatMessage(ChatRole.System, "Stable prelude\n\n[Route Instructions]\nroute one"), new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Tools = [toolA, toolB] });
        var second = coordinator.Prepare(
            session,
            profile,
            profile.ModelId,
            [new ChatMessage(ChatRole.System, "Stable prelude\n\n[Route Instructions]\nroute two"), new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { Tools = [toolB, toolA] });

        Assert.Equal(first.Descriptor.StableFingerprint, second.Descriptor.StableFingerprint);
        Assert.Equal("Stable prelude", first.Descriptor.StableSystemPrompt);
        Assert.Equal("route one", first.Descriptor.VolatileSuffix);
    }

    [Fact]
    public void Coordinator_OnlyMarksKeepWarmEligibleProviders()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = Path.Combine(Path.GetTempPath(), "openclaw-prompt-cache-tests", Guid.NewGuid().ToString("N"))
            }
        };
        Directory.CreateDirectory(config.Memory.StoragePath);
        var coordinator = new PromptCacheCoordinator(config, new PromptCacheTraceWriter(config));
        var session = new Session { Id = "s2", ChannelId = "test", SenderId = "user" };

        var openAi = coordinator.Prepare(
            session,
            CreateProfile("openai", "openai", keepWarmEnabled: true),
            "gpt-4.1",
            [new ChatMessage(ChatRole.System, "Stable prompt"), new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions());

        var anthropic = coordinator.Prepare(
            session,
            CreateProfile("anthropic", "anthropic", keepWarmEnabled: true),
            "claude-sonnet",
            [new ChatMessage(ChatRole.System, "Stable prompt"), new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions());

        Assert.False(openAi.Descriptor.KeepWarmEligible);
        Assert.True(anthropic.Descriptor.KeepWarmEligible);
    }

    [Fact]
    public void PromptCacheUsageExtractor_ReadsNormalizedCacheCounters()
    {
        var usage = new UsageDetails
        {
            CachedInputTokenCount = 4096,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["cache_creation_input_tokens"] = 512L
            }
        };

        var result = PromptCacheUsageExtractor.FromUsage(usage);

        Assert.Equal(4096, result.CacheReadTokens);
        Assert.Equal(512, result.CacheWriteTokens);
    }

    private static ModelProfile CreateProfile(string providerId, string dialect, bool keepWarmEnabled = false)
        => new()
        {
            Id = providerId + "-profile",
            ProviderId = providerId,
            ModelId = providerId + "-model",
            Capabilities = new ModelCapabilities
            {
                SupportsPromptCaching = true,
                SupportsExplicitCacheRetention = true,
                ReportsCacheReadTokens = true,
                ReportsCacheWriteTokens = dialect == "anthropic",
                SupportsSystemMessages = true,
                SupportsStreaming = true
            },
            PromptCaching = new PromptCachingConfig
            {
                Enabled = true,
                Dialect = dialect,
                Retention = "long",
                KeepWarmEnabled = keepWarmEnabled
            }
        };

    private sealed class TestChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("fake-profile-tests");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
