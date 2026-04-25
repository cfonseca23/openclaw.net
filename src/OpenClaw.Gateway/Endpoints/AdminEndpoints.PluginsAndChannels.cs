using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Agent.Execution;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Models;
using QRCoder;

namespace OpenClaw.Gateway.Endpoints;

internal static partial class AdminEndpoints
{
    private static void MapPluginAndChannelEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var adminSettings = services.AdminSettings;
        var pluginAdminSettings = services.PluginAdminSettings;
        var facade = services.Facade;
        var operations = services.Operations;

        app.MapGet("/admin/plugins", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.plugins");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new PluginListResponse
            {
                Items = operations.PluginHealth.ListSnapshots()
            }, CoreJsonContext.Default.PluginListResponse);
        });

        app.MapGet("/admin/skills", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.skills");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var loadedSkills = LoadCurrentSkillDefinitions(startup, runtime);
            runtime.LoadedSkills = loadedSkills;
            return Results.Json(new SkillListResponse
            {
                Items = loadedSkills
                    .Select(MapSkillHealthSnapshot)
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            }, CoreJsonContext.Default.SkillListResponse);
        });

        app.MapGet("/admin/compatibility/catalog", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.compatibility");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var compatibilityStatus = ctx.Request.Query.TryGetValue("compatibilityStatus", out var statusValue)
                ? statusValue.ToString()
                : null;
            var kind = ctx.Request.Query.TryGetValue("kind", out var kindValue)
                ? kindValue.ToString()
                : null;
            var category = ctx.Request.Query.TryGetValue("category", out var categoryValue)
                ? categoryValue.ToString()
                : null;

            return Results.Json(
                facade.GetCompatibilityCatalog(compatibilityStatus, kind, category),
                CoreJsonContext.Default.IntegrationCompatibilityCatalogResponse);
        });

        app.MapGet("/admin/compatibility/export", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.compatibility");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                facade.GetCompatibilityExport(),
                CoreJsonContext.Default.IntegrationCompatibilityExportResponse);
        });

        app.MapGet("/admin/plugins/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.plugins");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var item = operations.PluginHealth.ListSnapshots().FirstOrDefault(snapshot => string.Equals(snapshot.PluginId, id, StringComparison.Ordinal));
            return item is null
                ? Results.NotFound(new MutationResponse { Success = false, Error = "Plugin not found." })
                : Results.Json(item, CoreJsonContext.Default.PluginHealthSnapshot);
        });

        app.MapPost("/admin/plugins/{id}/disable", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetDisabled(id, disabled: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_disable", id, $"Disabled plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin disabled.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/enable", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetDisabled(id, disabled: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_enable", id, $"Enabled plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin enabled.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/review", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetReviewed(id, reviewed: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_review", id, $"Marked plugin '{id}' as reviewed.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin marked as reviewed.", RestartRequired = false }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/unreview", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetReviewed(id, reviewed: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_unreview", id, $"Removed review mark from plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin review mark cleared.", RestartRequired = false }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/quarantine", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetQuarantined(id, quarantined: true, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_quarantine", id, $"Quarantined plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin quarantined.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });

        app.MapPost("/admin/plugins/{id}/clear-quarantine", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.plugins.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.PluginMutationRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;

            var request = requestPayload.Value;
            var state = operations.PluginHealth.SetQuarantined(id, quarantined: false, request?.Reason);
            RecordOperatorAudit(ctx, operations, auth, "plugin_clear_quarantine", id, $"Cleared quarantine for plugin '{id}'.", success: true, before: null, after: state);
            return Results.Json(new MutationResponse { Success = true, Message = "Plugin quarantine cleared.", RestartRequired = true }, CoreJsonContext.Default.MutationResponse);
        });
        // ── Channel Auth Events ──────────────────────────────────────
        var authEventStore = runtime.ChannelAuthEvents;

        app.MapGet("/admin/channels/auth", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = authEventStore.GetAll().Select(MapChannelAuthStatusItem).ToArray()
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/{channelId}/auth", (HttpContext ctx, string channelId, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = accountId is not null
                ? authEventStore.GetLatest(channelId, accountId) is { } evt ? [MapChannelAuthStatusItem(evt)] : []
                : authEventStore.GetAll(channelId).Select(MapChannelAuthStatusItem).ToArray();
            if (items.Length == 0)
                return Results.NotFound(new MutationResponse { Success = false, Error = "No auth event recorded for this channel." });

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = items
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/{channelId}/auth/stream", async (HttpContext ctx, string channelId, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamChannelAuthEventsAsync(ctx, authEventStore, channelId, accountId);
        });

        app.MapGet("/admin/channels/whatsapp/auth", (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var items = accountId is not null
                ? authEventStore.GetLatest("whatsapp", accountId) is { } evt ? [MapChannelAuthStatusItem(evt)] : []
                : authEventStore.GetAll("whatsapp").Select(MapChannelAuthStatusItem).ToArray();
            if (items.Length == 0)
                return Results.NotFound(new MutationResponse { Success = false, Error = "No WhatsApp auth event recorded." });

            return Results.Json(new ChannelAuthStatusResponse
            {
                Items = items
            }, CoreJsonContext.Default.ChannelAuthStatusResponse);
        });

        app.MapGet("/admin/channels/whatsapp/auth/stream", async (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamChannelAuthEventsAsync(ctx, authEventStore, "whatsapp", accountId);
        });

        app.MapGet("/admin/channels/whatsapp/auth/qr.svg", (HttpContext ctx, string? accountId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var evt = accountId is not null
                ? authEventStore.GetLatest("whatsapp", accountId)
                : authEventStore.GetAll("whatsapp").FirstOrDefault(static item =>
                    string.Equals(item.State, "qr_code", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(item.Data));
            if (evt is null || !string.Equals(evt.State, "qr_code", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(evt.Data))
            {
                return Results.NotFound(new MutationResponse
                {
                    Success = false,
                    Error = "No active WhatsApp QR code is available."
                });
            }

            using var generator = new QRCodeGenerator();
            using var qrData = generator.CreateQrCode(evt.Data, QRCodeGenerator.ECCLevel.Q);
            var svg = new SvgQRCode(qrData).GetGraphic(6);
            return Results.Text(svg, "image/svg+xml", Encoding.UTF8);
        });

        app.MapGet("/admin/channels/whatsapp/setup", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.channels.auth");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var response = BuildWhatsAppSetupResponse(startup, runtime, adminSettings, pluginAdminSettings, message: "WhatsApp setup loaded.");
            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
        });

        app.MapPut("/admin/channels/whatsapp/setup", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.channels.auth.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.WhatsAppSetupRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            var request = requestPayload.Value;
            if (request is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "WhatsApp setup payload is required." });

            var normalizedRequestResult = NormalizeWhatsAppSetupRequest(request);
            if (normalizedRequestResult.Errors.Count > 0)
            {
                var invalidResponse = BuildWhatsAppSetupResponse(
                    startup,
                    runtime,
                    adminSettings,
                    pluginAdminSettings,
                    message: "WhatsApp setup validation failed.",
                    validationErrors: normalizedRequestResult.Errors);
                return Results.Json(invalidResponse, CoreJsonContext.Default.WhatsAppSetupResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var normalizedRequest = normalizedRequestResult.Request;
            var builtInResult = adminSettings.UpdateWhatsAppSettings(normalizedRequest);
            var validationErrors = ValidateWhatsAppPluginConfig(startup, runtime, normalizedRequest, out var pluginId, out var pluginConfig, out var pluginWarning);
            var pluginChanged = false;
            if (builtInResult.Success && validationErrors.Count == 0 && pluginId is not null)
            {
                pluginAdminSettings.Upsert(pluginId, pluginConfig, enabled: true);
                pluginChanged = true;
            }
            var response = BuildWhatsAppSetupResponse(
                startup,
                runtime,
                adminSettings,
                pluginAdminSettings,
                message: builtInResult.Success && validationErrors.Count == 0 ? "WhatsApp setup saved." : "WhatsApp setup validation failed.",
                restartRequired: builtInResult.RestartRequired || pluginChanged,
                validationErrors: [.. builtInResult.Errors, .. validationErrors],
                pluginWarningOverride: pluginWarning);

            if (builtInResult.Success && validationErrors.Count == 0)
            {
                RecordOperatorAudit(
                    ctx,
                    operations,
                    auth,
                    "whatsapp_setup_update",
                    "whatsapp",
                    "Updated WhatsApp setup.",
                    success: true,
                    before: null,
                    after: normalizedRequest);
                return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
            }

            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse, statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapPost("/admin/channels/whatsapp/restart", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.channels.auth.restart");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            if (!runtime.ChannelAdapters.TryGetValue("whatsapp", out var adapter) || adapter is not IRestartableChannelAdapter restartable)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Runtime restart is only available for plugin-backed WhatsApp channels." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            authEventStore.ClearChannel("whatsapp");
            await restartable.RestartAsync(ctx.RequestAborted);
            RecordOperatorAudit(ctx, operations, auth, "whatsapp_restart", "whatsapp", "Restarted WhatsApp channel.", success: true, before: null, after: null);

            var response = BuildWhatsAppSetupResponse(startup, runtime, adminSettings, pluginAdminSettings, message: "WhatsApp channel restarted.");
            return Results.Json(response, CoreJsonContext.Default.WhatsAppSetupResponse);
        });
    }
}
