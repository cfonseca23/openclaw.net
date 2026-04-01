using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ChannelAdapterSecurityTests
{
    [Fact]
    public void Ed25519Verify_AcceptsValidSignature()
    {
        var privateKey = Enumerable.Range(0, 32).Select(static i => (byte)(i + 1)).ToArray();
        var publicKey = new Ed25519PrivateKeyParameters(privateKey, 0).GeneratePublicKey().GetEncoded();
        var message = Encoding.UTF8.GetBytes("1234567890{\"type\":1}");

        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(message, 0, message.Length);
        var signature = signer.GenerateSignature();

        Assert.True(Ed25519Verify.Verify(signature, message, publicKey));
    }

    [Fact]
    public async Task DiscordWebhookHandler_RejectsDisallowedGuild()
    {
        var handler = new DiscordWebhookHandler(
            new DiscordChannelConfig
            {
                ValidateSignature = false,
                AllowedGuildIds = ["allowed-guild"]
            },
            NullLogger<DiscordWebhookHandler>.Instance);

        var payload = """
            {
              "id":"1",
              "type":2,
              "guild_id":"blocked-guild",
              "channel_id":"channel-1",
              "member":{"user":{"id":"user-1","username":"tester"}},
              "data":{"name":"claw","options":[{"name":"message","value":"hello"}]}
            }
            """;
        var enqueued = false;

        var result = await handler.HandleAsync(
            payload,
            signatureHeader: null,
            timestampHeader: null,
            (msg, ct) =>
            {
                enqueued = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(403, result.StatusCode);
        Assert.False(enqueued);
    }

    [Fact]
    public async Task SlackWebhookHandler_SlashCommand_RejectsDisallowedWorkspace()
    {
        var handler = new SlackWebhookHandler(
            new SlackChannelConfig
            {
                ValidateSignature = false,
                AllowedWorkspaceIds = ["allowed-workspace"]
            },
            new AllowlistManager(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                NullLogger<AllowlistManager>.Instance),
            new RecentSendersStore(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                NullLogger<RecentSendersStore>.Instance),
            AllowlistSemantics.Legacy,
            NullLogger<SlackWebhookHandler>.Instance);

        var result = await handler.HandleSlashCommandAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["user_id"] = "user-1",
                ["team_id"] = "blocked-workspace",
                ["channel_id"] = "C123",
                ["command"] = "/claw",
                ["text"] = "hello"
            },
            timestampHeader: null,
            signatureHeader: null,
            rawBody: "user_id=user-1",
            (msg, ct) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task DiscordChannel_SendAsync_RecreatesRequestAfterRateLimit()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage((HttpStatusCode)429)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);
        var requestCount = 0;
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            Interlocked.Increment(ref requestCount);
            return responses.Dequeue();
        }));

        var channel = new DiscordChannel(
            new DiscordChannelConfig
            {
                BotToken = "token",
                RegisterSlashCommands = false
            },
            NullLogger<DiscordChannel>.Instance,
            http);

        await channel.SendAsync(
            new OutboundMessage
            {
                ChannelId = "discord",
                RecipientId = "123",
                Text = "hello"
            },
            CancellationToken.None);

        Assert.Equal(2, requestCount);
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
