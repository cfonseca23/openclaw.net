using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal static class ChannelReadinessEvaluator
{
    public static IReadOnlyList<ChannelReadinessState> Evaluate(GatewayConfig config, bool isNonLoopbackBind)
    {
        return
        [
            EvaluateSms(config, isNonLoopbackBind),
            EvaluateTelegram(config, isNonLoopbackBind),
            EvaluateWhatsApp(config, isNonLoopbackBind)
        ];
    }

    private static ChannelReadinessState EvaluateSms(GatewayConfig config, bool isNonLoopbackBind)
    {
        var sms = config.Channels.Sms.Twilio;
        if (!sms.Enabled)
            return ChannelReadinessState.Disabled("sms", "Twilio SMS", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable SMS channel",
                    Href = "#sms-enabled-input",
                    Reference = "OpenClaw:Channels:Sms:Twilio:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveSecretRefOrNull(sms.AuthTokenRef)))
        {
            missing.Add("Twilio AuthTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Twilio auth token ref",
                Href = "#setup-ref-sms-auth-token",
                Reference = "OpenClaw:Channels:Sms:Twilio:AuthTokenRef = env:TWILIO_AUTH_TOKEN"
            });
        }
        if (string.IsNullOrWhiteSpace(sms.AccountSid))
        {
            missing.Add("Twilio AccountSid");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Twilio account SID",
                Href = "#setup-ref-sms-account-sid",
                Reference = "OpenClaw:Channels:Sms:Twilio:AccountSid"
            });
        }
        if (string.IsNullOrWhiteSpace(sms.MessagingServiceSid) && string.IsNullOrWhiteSpace(sms.FromNumber))
        {
            missing.Add("Twilio MessagingServiceSid or FromNumber");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Twilio sender",
                Href = "#setup-ref-sms-sender",
                Reference = "OpenClaw:Channels:Sms:Twilio:MessagingServiceSid or OpenClaw:Channels:Sms:Twilio:FromNumber"
            });
        }
        if (sms.ValidateSignature && string.IsNullOrWhiteSpace(sms.WebhookPublicBaseUrl))
        {
            missing.Add("Twilio WebhookPublicBaseUrl (required when signature validation is enabled)");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set SMS webhook public base URL",
                Href = "#setup-ref-sms-webhook-public-base-url",
                Reference = "OpenClaw:Channels:Sms:Twilio:WebhookPublicBaseUrl"
            });
        }
        if (!sms.ValidateSignature)
        {
            warnings.Add(isNonLoopbackBind
                ? "SMS webhook signature validation is disabled on a public bind."
                : "SMS webhook signature validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable SMS signature validation",
                Href = "#sms-validate-signature-input",
                Reference = "OpenClaw:Channels:Sms:Twilio:ValidateSignature"
            });
        }

        return ChannelReadinessState.From("sms", "Twilio SMS", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateTelegram(GatewayConfig config, bool isNonLoopbackBind)
    {
        var telegram = config.Channels.Telegram;
        if (!telegram.Enabled)
            return ChannelReadinessState.Disabled("telegram", "Telegram", "official", [
                new ChannelFixGuidance
                {
                    Label = "Enable Telegram channel",
                    Href = "#telegram-enabled-input",
                    Reference = "OpenClaw:Channels:Telegram:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.IsNullOrWhiteSpace(ResolveTelegramToken(telegram)))
        {
            missing.Add("Telegram BotToken or BotTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Telegram bot token",
                Href = "#setup-ref-telegram-bot-token",
                Reference = "OpenClaw:Channels:Telegram:BotTokenRef = env:TELEGRAM_BOT_TOKEN"
            });
        }
        if (telegram.ValidateSignature && string.IsNullOrWhiteSpace(ResolveTelegramSecret(telegram)))
        {
            missing.Add("Telegram WebhookSecretToken or WebhookSecretTokenRef");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Set Telegram webhook secret",
                Href = "#setup-ref-telegram-webhook-secret",
                Reference = "OpenClaw:Channels:Telegram:WebhookSecretTokenRef = env:TELEGRAM_WEBHOOK_SECRET"
            });
        }
        if (!telegram.ValidateSignature)
        {
            warnings.Add(isNonLoopbackBind
                ? "Telegram webhook secret validation is disabled on a public bind."
                : "Telegram webhook secret validation is disabled.");
            guidance.Add(new ChannelFixGuidance
            {
                Label = "Enable Telegram webhook secret validation",
                Href = "#telegram-validate-signature-input",
                Reference = "OpenClaw:Channels:Telegram:ValidateSignature"
            });
        }

        return ChannelReadinessState.From("telegram", "Telegram", "official", missing, warnings, guidance);
    }

    private static ChannelReadinessState EvaluateWhatsApp(GatewayConfig config, bool isNonLoopbackBind)
    {
        var whatsapp = config.Channels.WhatsApp;
        if (!whatsapp.Enabled)
            return ChannelReadinessState.Disabled("whatsapp", "WhatsApp", whatsapp.Type, [
                new ChannelFixGuidance
                {
                    Label = "Enable WhatsApp channel",
                    Href = "#whatsapp-enabled-input",
                    Reference = "OpenClaw:Channels:WhatsApp:Enabled"
                }
            ]);

        var missing = new List<string>();
        var warnings = new List<string>();
        var guidance = new List<ChannelFixGuidance>();

        if (string.Equals(whatsapp.Type, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(whatsapp.BridgeUrl))
            {
                missing.Add("WhatsApp BridgeUrl");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp bridge URL",
                    Href = "#setup-ref-whatsapp-bridge-url",
                    Reference = "OpenClaw:Channels:WhatsApp:BridgeUrl"
                });
            }

            var bridgeToken = ResolveSecretRefOrNull(whatsapp.BridgeTokenRef) ?? whatsapp.BridgeToken;
            if (isNonLoopbackBind && string.IsNullOrWhiteSpace(bridgeToken))
            {
                missing.Add("WhatsApp BridgeToken or BridgeTokenRef for public bind");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp bridge token",
                    Href = "#setup-ref-whatsapp-bridge-token",
                    Reference = "OpenClaw:Channels:WhatsApp:BridgeTokenRef = env:WHATSAPP_BRIDGE_TOKEN"
                });
            }
            else if (string.IsNullOrWhiteSpace(bridgeToken))
            {
                warnings.Add("WhatsApp bridge inbound authentication token is not configured.");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp bridge token",
                    Href = "#setup-ref-whatsapp-bridge-token",
                    Reference = "OpenClaw:Channels:WhatsApp:BridgeTokenRef = env:WHATSAPP_BRIDGE_TOKEN"
                });
            }
        }
        else if (string.Equals(whatsapp.Type, "first_party_worker", StringComparison.OrdinalIgnoreCase))
        {
            if (whatsapp.FirstPartyWorker.Accounts.Count == 0)
            {
                missing.Add("WhatsApp first-party worker account configuration");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Add a first-party worker account",
                    Href = "#wa-first-party-worker-config-json-input",
                    Reference = "OpenClaw:Channels:WhatsApp:FirstPartyWorker:Accounts"
                });
            }

            if (string.IsNullOrWhiteSpace(whatsapp.FirstPartyWorker.ExecutablePath))
            {
                warnings.Add("WhatsApp first-party worker executable path is not configured; runtime will rely on colocated auto-discovery.");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set first-party worker executable path",
                    Href = "#wa-first-party-worker-config-json-input",
                    Reference = "OpenClaw:Channels:WhatsApp:FirstPartyWorker:ExecutablePath"
                });
            }
        }
        else
        {
            var cloudToken = ResolveSecretRefOrNull(whatsapp.CloudApiTokenRef) ?? whatsapp.CloudApiToken;
            if (string.IsNullOrWhiteSpace(cloudToken))
            {
                missing.Add("WhatsApp CloudApiToken or CloudApiTokenRef");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp Cloud API token",
                    Href = "#setup-ref-whatsapp-cloud-token",
                    Reference = "OpenClaw:Channels:WhatsApp:CloudApiTokenRef = env:WHATSAPP_CLOUD_API_TOKEN"
                });
            }
            if (string.IsNullOrWhiteSpace(whatsapp.PhoneNumberId))
            {
                missing.Add("WhatsApp PhoneNumberId");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Set WhatsApp phone number ID",
                    Href = "#setup-ref-whatsapp-phone-number-id",
                    Reference = "OpenClaw:Channels:WhatsApp:PhoneNumberId"
                });
            }
            if (whatsapp.ValidateSignature)
            {
                var appSecret = ResolveSecretRefOrNull(whatsapp.WebhookAppSecretRef) ?? whatsapp.WebhookAppSecret;
                if (string.IsNullOrWhiteSpace(appSecret))
                {
                    missing.Add("WhatsApp WebhookAppSecret or WebhookAppSecretRef");
                    guidance.Add(new ChannelFixGuidance
                    {
                        Label = "Set WhatsApp webhook app secret",
                        Href = "#setup-ref-whatsapp-app-secret",
                        Reference = "OpenClaw:Channels:WhatsApp:WebhookAppSecretRef = env:WHATSAPP_APP_SECRET"
                    });
                }
            }
            else
            {
                warnings.Add(isNonLoopbackBind
                    ? "WhatsApp official signature validation is disabled on a public bind."
                    : "WhatsApp official signature validation is disabled.");
                guidance.Add(new ChannelFixGuidance
                {
                    Label = "Enable WhatsApp signature validation",
                    Href = "#whatsapp-validate-signature-input",
                    Reference = "OpenClaw:Channels:WhatsApp:ValidateSignature"
                });
            }
        }

        return ChannelReadinessState.From("whatsapp", "WhatsApp", whatsapp.Type, missing, warnings, guidance);
    }

    private static string? ResolveSecretRefOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
        {
            return SecretResolver.Resolve(value);
        }

        return value;
    }

    private static string? ResolveTelegramToken(TelegramChannelConfig telegram)
    {
        if (!string.IsNullOrWhiteSpace(telegram.BotToken))
            return telegram.BotToken;

        if (telegram.BotTokenRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(telegram.BotTokenRef[4..]);

        return null;
    }

    private static string? ResolveTelegramSecret(TelegramChannelConfig telegram)
    {
        if (!string.IsNullOrWhiteSpace(telegram.WebhookSecretToken))
            return telegram.WebhookSecretToken;

        return ResolveSecretRefOrNull(telegram.WebhookSecretTokenRef);
    }
}

internal sealed class ChannelReadinessState
{
    public required string ChannelId { get; init; }
    public required string DisplayName { get; init; }
    public required string Mode { get; init; }
    public required string Status { get; init; }
    public bool Enabled { get; init; }
    public bool Ready => string.Equals(Status, "ready", StringComparison.Ordinal);
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<ChannelFixGuidance> FixGuidance { get; init; } = [];

    public static ChannelReadinessState Disabled(
        string channelId,
        string displayName,
        string mode = "official",
        IReadOnlyList<ChannelFixGuidance>? fixGuidance = null)
        => new()
        {
            ChannelId = channelId,
            DisplayName = displayName,
            Mode = mode,
            Status = "disabled",
            Enabled = false,
            MissingRequirements = [],
            Warnings = [],
            FixGuidance = fixGuidance ?? []
        };

    public static ChannelReadinessState From(
        string channelId,
        string displayName,
        string mode,
        IReadOnlyList<string> missingRequirements,
        IReadOnlyList<string> warnings,
        IReadOnlyList<ChannelFixGuidance> fixGuidance)
        => new()
        {
            ChannelId = channelId,
            DisplayName = displayName,
            Mode = mode,
            Status = missingRequirements.Count > 0 ? "misconfigured" : warnings.Count > 0 ? "degraded" : "ready",
            Enabled = true,
            MissingRequirements = missingRequirements,
            Warnings = warnings,
            FixGuidance = fixGuidance
        };
}

internal sealed class ChannelFixGuidance
{
    public required string Label { get; init; }
    public required string Href { get; init; }
    public required string Reference { get; init; }
}
