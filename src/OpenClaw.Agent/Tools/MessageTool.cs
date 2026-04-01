using System.Threading.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Send messages across channels. Routes outbound messages through the pipeline.
/// </summary>
public sealed class MessageTool : ITool
{
    private readonly ChannelWriter<OutboundMessage> _outbound;

    public MessageTool(MessagePipeline pipeline)
    {
        _outbound = pipeline.OutboundWriter;
    }

    public string Name => "message";
    public string Description => "Send a message to a specific channel and recipient. Use to communicate across channels.";
    public string ParameterSchema => """{"type":"object","properties":{"channel_id":{"type":"string","description":"Target channel (e.g. 'telegram', 'slack', 'discord', 'sms', 'email', 'websocket')"},"recipient_id":{"type":"string","description":"Recipient identifier (chat ID, user ID, phone number, etc.)"},"text":{"type":"string","description":"Message text to send"},"reply_to":{"type":"string","description":"Optional message ID to reply to"}},"required":["channel_id","recipient_id","text"]}""";

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = System.Text.Json.JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var channelId = GetString(root, "channel_id");
        if (string.IsNullOrWhiteSpace(channelId))
            return "Error: 'channel_id' is required.";

        var recipientId = GetString(root, "recipient_id");
        if (string.IsNullOrWhiteSpace(recipientId))
            return "Error: 'recipient_id' is required.";

        var text = GetString(root, "text");
        if (string.IsNullOrWhiteSpace(text))
            return "Error: 'text' is required.";

        var replyTo = GetString(root, "reply_to");

        var message = new OutboundMessage
        {
            ChannelId = channelId,
            RecipientId = recipientId,
            Text = text,
            ReplyToMessageId = replyTo,
        };

        await _outbound.WriteAsync(message, ct);
        return $"Message queued for delivery to {channelId}:{recipientId}.";
    }

    private static string? GetString(System.Text.Json.JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;
}
