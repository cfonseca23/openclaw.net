using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Integrations;

/// <summary>
/// Handles Gmail Pub/Sub push notifications and injects messages into the pipeline.
/// Google Pub/Sub sends a POST with a base64-encoded notification when new emails arrive.
/// </summary>
internal sealed class GmailPubSubBridge
{
    private readonly GmailPubSubConfig _config;
    private readonly MessagePipeline _pipeline;
    private readonly ILogger<GmailPubSubBridge> _logger;

    public GmailPubSubBridge(GmailPubSubConfig config, MessagePipeline pipeline, ILogger<GmailPubSubBridge> logger)
    {
        _config = config;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <summary>
    /// Handles the incoming Pub/Sub push notification webhook.
    /// </summary>
    public async ValueTask<(int StatusCode, string Body)> HandlePushAsync(string bodyText, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;

            // Extract the Pub/Sub message
            if (!root.TryGetProperty("message", out var message))
                return (400, "Missing 'message' field.");

            string? emailAddress = null;
            string? historyId = null;

            if (message.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
            {
                var dataStr = dataEl.GetString();
                if (!string.IsNullOrWhiteSpace(dataStr))
                {
                    try
                    {
                        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(dataStr));
                        using var innerDoc = JsonDocument.Parse(decoded);
                        var inner = innerDoc.RootElement;
                        emailAddress = inner.TryGetProperty("emailAddress", out var ea) ? ea.GetString() : null;
                        historyId = inner.TryGetProperty("historyId", out var hi) ? hi.ToString() : null;
                    }
                    catch
                    {
                        // Data may not be JSON — just use the raw notification
                    }
                }
            }

            var promptText = _config.Prompt;
            if (!string.IsNullOrWhiteSpace(emailAddress))
                promptText += $"\nEmail: {emailAddress}";
            if (!string.IsNullOrWhiteSpace(historyId))
                promptText += $"\nHistory ID: {historyId}";

            var inbound = new InboundMessage
            {
                ChannelId = "gmail",
                SenderId = emailAddress ?? "gmail-pubsub",
                SessionId = _config.SessionId ?? "gmail:triage",
                Text = promptText,
                IsSystem = true,
            };

            await _pipeline.InboundWriter.WriteAsync(inbound, ct);
            _logger.LogInformation("Gmail Pub/Sub notification processed for {Email}.", emailAddress ?? "unknown");

            return (200, "OK");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process Gmail Pub/Sub notification.");
            return (500, "Processing failed.");
        }
    }
}
