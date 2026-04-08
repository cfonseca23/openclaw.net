using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

[Collection(DynamicProviderRegistryCollection.Name)]
public sealed class LlmClientFactoryTests
{
    [Fact]
    public void TryRegisterProvider_DuplicateId_FirstRegistrationWins()
    {
        LlmClientFactory.ResetDynamicProviders();
        var firstClient = Substitute.For<IChatClient>();
        var secondClient = Substitute.For<IChatClient>();

        var first = LlmClientFactory.TryRegisterProvider("dup-provider", firstClient, "owner-a");
        var duplicate = LlmClientFactory.TryRegisterProvider("dup-provider", secondClient, "owner-b");
        var resolved = LlmClientFactory.CreateChatClient(new OpenClaw.Core.Models.LlmProviderConfig
        {
            Provider = "dup-provider",
            Model = "test"
        });

        Assert.Equal(LlmClientFactory.DynamicProviderRegistrationResult.Registered, first);
        Assert.Equal(LlmClientFactory.DynamicProviderRegistrationResult.Duplicate, duplicate);
        Assert.Same(firstClient, resolved);
    }

    [Fact]
    public void UnregisterProvidersOwnedBy_RemovesOnlyMatchingOwner()
    {
        LlmClientFactory.ResetDynamicProviders();
        var retainedClient = Substitute.For<IChatClient>();
        var removedClient = Substitute.For<IChatClient>();

        _ = LlmClientFactory.TryRegisterProvider("keep-provider", retainedClient, "owner-keep");
        _ = LlmClientFactory.TryRegisterProvider("remove-provider", removedClient, "owner-remove");

        LlmClientFactory.UnregisterProvidersOwnedBy("owner-remove");
        var owners = LlmClientFactory.GetDynamicProviderOwners();

        Assert.Contains("keep-provider", owners.Keys);
        Assert.DoesNotContain("remove-provider", owners.Keys);
    }

    [Fact]
    public void CreateTransportOptions_DisablesHiddenRetries()
    {
        var transport = LlmClientFactory.CreateTransportOptions("https://example.invalid/v1");

        Assert.Equal(new Uri("https://example.invalid/v1"), transport.Endpoint);
        Assert.Equal(0, transport.HiddenRetryCount);
    }

    [Fact]
    public void CreateTransportOptions_DefaultEndpoint_RemainsUnset()
    {
        var transport = LlmClientFactory.CreateTransportOptions(endpoint: null);

        Assert.Null(transport.Endpoint);
        Assert.Equal(0, transport.HiddenRetryCount);
    }

    [Theory]
    [InlineData("openai", "gpt-4.1")]
    [InlineData("anthropic", "claude-sonnet-4-5")]
    [InlineData("claude", "claude-sonnet-4-5")]
    [InlineData("gemini", "gemini-2.5-flash")]
    [InlineData("google", "gemini-2.5-flash")]
    public void CreateChatClient_BuiltInProviders_CreateNativeClients(string provider, string model)
    {
        LlmClientFactory.ResetDynamicProviders();

        var client = LlmClientFactory.CreateChatClient(new LlmProviderConfig
        {
            Provider = provider,
            Model = model,
            ApiKey = "test-key"
        });

        Assert.NotNull(client);
    }
}
