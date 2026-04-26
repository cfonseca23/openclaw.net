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
    private static void MapAuthEndpoints(WebApplication app, AdminEndpointServices services)
    {
        var startup = services.Startup;
        var runtime = services.Runtime;
        var browserSessions = services.BrowserSessions;
        var operatorAccounts = services.OperatorAccounts;
        var organizationPolicy = services.OrganizationPolicy;
        var toolPresetResolver = services.ToolPresetResolver;
        var operations = services.Operations;

        app.MapGet("/auth/session", (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            return Results.Json(
                MapAuthSessionResponse(auth, startup, runtime, organizationPolicy.GetSnapshot(), toolPresetResolver),
                CoreJsonContext.Default.AuthSessionResponse);
        });

        app.MapPost("/auth/session", async (HttpContext ctx) =>
        {
            AuthSessionRequest? request = null;
            if (ctx.Request.ContentLength is > 0)
            {
                var authRequest = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.AuthSessionRequest);
                if (authRequest.Failure is not null)
                    return authRequest.Failure;

                request = authRequest.Value;
            }

            var policy = organizationPolicy.GetSnapshot();
            if (!policy.AllowedAuthModes.Any(mode => string.Equals(mode, OrganizationAuthModeNames.BrowserSession, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "Browser sessions are disabled by organization policy."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            OperatorIdentitySnapshot? identity = null;
            if (!string.IsNullOrWhiteSpace(request?.Username) || !string.IsNullOrWhiteSpace(request?.Password))
            {
                if (!operatorAccounts.TryAuthenticatePassword(request?.Username ?? "", request?.Password ?? "", out identity))
                    return Results.Unauthorized();
            }
            else if (!string.IsNullOrWhiteSpace(request?.AccountToken))
            {
                if (!policy.AllowedAuthModes.Any(mode => string.Equals(mode, OrganizationAuthModeNames.AccountToken, StringComparison.OrdinalIgnoreCase)))
                {
                    return Results.Json(
                        new OperationStatusResponse
                        {
                            Success = false,
                            Error = "Account token login is disabled by organization policy."
                        },
                        CoreJsonContext.Default.OperationStatusResponse,
                        statusCode: StatusCodes.Status403Forbidden);
                }

                if (!operatorAccounts.TryAuthenticateToken(request.AccountToken, out identity))
                    return Results.Unauthorized();
            }
            else
            {
                var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: false);
                if (!auth.IsAuthorized)
                    return Results.Unauthorized();

                identity = auth.ToIdentity();
            }

            var ticket = browserSessions.Create(request?.Remember ?? false, identity);
            browserSessions.WriteCookie(ctx, ticket);
            var issuedAuth = new EndpointHelpers.OperatorAuthorizationResult(
                true,
                "browser-session",
                UsedBrowserSession: true,
                BrowserSession: ticket,
                Role: ticket.Role,
                AccountId: ticket.AccountId,
                Username: ticket.Username,
                DisplayName: ticket.DisplayName,
                IsBootstrapAdmin: ticket.IsBootstrapAdmin);

            return Results.Json(
                MapAuthSessionResponse(issuedAuth, startup, runtime, policy, toolPresetResolver),
                CoreJsonContext.Default.AuthSessionResponse);
        });

        app.MapPost("/auth/operator-token", async (HttpContext ctx) =>
        {
            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.OperatorTokenExchangeRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            if (requestPayload.Value is null)
                return Results.BadRequest(new OperationStatusResponse { Success = false, Error = "Username and password are required." });

            var policy = organizationPolicy.GetSnapshot();
            if (!policy.AllowedAuthModes.Any(mode => string.Equals(mode, OrganizationAuthModeNames.AccountToken, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(
                    new OperationStatusResponse
                    {
                        Success = false,
                        Error = "Account token auth is disabled by organization policy."
                    },
                    CoreJsonContext.Default.OperationStatusResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            OperatorTokenExchangeResponse? created;
            try
            {
                created = operatorAccounts.CreateTokenFromCredentials(requestPayload.Value);
            }
            catch (InvalidOperationException)
            {
                created = null;
            }

            if (created is null || created.Account is null)
                return Results.Unauthorized();

            operations.OperatorAudit.Append(new OperatorAuditEntry
            {
                Id = $"audit_{Guid.NewGuid():N}"[..20],
                TimestampUtc = DateTimeOffset.UtcNow,
                ActorId = $"account:{created.Account.Id}",
                ActorRole = created.Account.Role,
                ActorDisplayName = string.IsNullOrWhiteSpace(created.Account.DisplayName) ? created.Account.Username : created.Account.DisplayName,
                AuthMode = OrganizationAuthModeNames.AccountToken,
                ActionType = "operator_token_exchange",
                TargetId = created.Account.Id,
                Summary = $"Issued operator token '{created.TokenInfo?.Id}' for '{created.Account.Username}'.",
                Success = true
            });

            return Results.Json(created, CoreJsonContext.Default.OperatorTokenExchangeResponse);
        });

        app.MapDelete("/auth/session", (HttpContext ctx) =>
        {
            var auth = EndpointHelpers.AuthorizeOperatorRequest(ctx, startup, browserSessions, requireCsrf: true);
            if (!auth.IsAuthorized)
                return Results.Unauthorized();

            browserSessions.Revoke(ctx);
            browserSessions.ClearCookie(ctx);
            return Results.Ok(new OperationStatusResponse
            {
                Success = true,
                Message = "Browser session cleared."
            });
        });

        app.MapGet("/admin/operator-accounts", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.operator-accounts");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new OperatorAccountListResponse { Items = operatorAccounts.List() },
                CoreJsonContext.Default.OperatorAccountListResponse);
        });

        app.MapPost("/admin/operator-accounts", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.operator-accounts.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.OperatorAccountCreateRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            if (requestPayload.Value is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Request body is required." });

            try
            {
                var created = operatorAccounts.Create(requestPayload.Value);
                RecordOperatorAudit(ctx, operations, auth, "operator_account_create", created.Id, $"Created operator account '{created.Username}'.", success: true, before: null, after: created);
                return Results.Json(
                    new OperatorAccountDetailResponse
                    {
                        Account = created,
                        Tokens = []
                    },
                    CoreJsonContext.Default.OperatorAccountDetailResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "operator_account_create", requestPayload.Value.Username ?? "unknown", ex.Message, success: false, before: null, after: requestPayload.Value);
                return Results.Json(
                    new MutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/admin/operator-accounts/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.operator-accounts");
            if (authResult.Failure is not null)
                return authResult.Failure;

            var detail = operatorAccounts.Get(id);
            if (detail is null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Operator account not found." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(detail, CoreJsonContext.Default.OperatorAccountDetailResponse);
        });

        app.MapPut("/admin/operator-accounts/{id}", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.operator-accounts.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = operatorAccounts.Get(id);
            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.OperatorAccountUpdateRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            if (requestPayload.Value is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Request body is required." });

            try
            {
                var updated = operatorAccounts.Update(id, requestPayload.Value);
                if (updated is null)
                {
                    return Results.Json(
                        new MutationResponse { Success = false, Error = "Operator account not found." },
                        CoreJsonContext.Default.MutationResponse,
                        statusCode: StatusCodes.Status404NotFound);
                }

                RecordOperatorAudit(ctx, operations, auth, "operator_account_update", id, $"Updated operator account '{updated.Username}'.", success: true, before, after: updated);
                return Results.Json(
                    new OperatorAccountDetailResponse
                    {
                        Account = updated,
                        Tokens = operatorAccounts.Get(id)?.Tokens ?? []
                    },
                    CoreJsonContext.Default.OperatorAccountDetailResponse);
            }
            catch (Exception ex)
            {
                RecordOperatorAudit(ctx, operations, auth, "operator_account_update", id, ex.Message, success: false, before, after: requestPayload.Value);
                return Results.Json(
                    new MutationResponse { Success = false, Error = ex.Message },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapDelete("/admin/operator-accounts/{id}", (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.operator-accounts.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var before = operatorAccounts.Get(id);
            var deleted = operatorAccounts.Delete(id);
            RecordOperatorAudit(ctx, operations, auth, "operator_account_delete", id, deleted ? $"Deleted operator account '{id}'." : $"Operator account '{id}' was not found.", deleted, before, after: null);
            return Results.Json(
                new MutationResponse { Success = deleted, Error = deleted ? null : "Operator account not found." },
                CoreJsonContext.Default.MutationResponse,
                statusCode: deleted ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });

        app.MapPost("/admin/operator-accounts/{id}/tokens", async (HttpContext ctx, string id) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.operator-accounts.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.OperatorAccountTokenCreateRequest);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            if (requestPayload.Value is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Request body is required." });

            var created = operatorAccounts.CreateToken(id, requestPayload.Value);
            if (created is null)
            {
                return Results.Json(
                    new MutationResponse { Success = false, Error = "Operator account not found." },
                    CoreJsonContext.Default.MutationResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            RecordOperatorAudit(ctx, operations, auth, "operator_account_token_create", id, $"Created operator token '{created.TokenInfo?.Id}' for '{id}'.", success: true, before: null, after: created.TokenInfo);
            return Results.Json(created, CoreJsonContext.Default.OperatorAccountTokenCreateResponse, statusCode: StatusCodes.Status201Created);
        });

        app.MapDelete("/admin/operator-accounts/{id}/tokens/{tokenId}", (HttpContext ctx, string id, string tokenId) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.operator-accounts.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var revoked = operatorAccounts.RevokeToken(id, tokenId);
            RecordOperatorAudit(ctx, operations, auth, "operator_account_token_revoke", id, revoked ? $"Revoked operator token '{tokenId}'." : $"Operator token '{tokenId}' was not found.", revoked, before: null, after: null);
            return Results.Json(
                new MutationResponse { Success = revoked, Error = revoked ? null : "Operator token not found." },
                CoreJsonContext.Default.MutationResponse,
                statusCode: revoked ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });

        app.MapGet("/admin/organization-policy", (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: false, endpointScope: "admin.organization-policy");
            if (authResult.Failure is not null)
                return authResult.Failure;

            return Results.Json(
                new OrganizationPolicyResponse
                {
                    Policy = organizationPolicy.GetSnapshot(),
                    Message = "Organization policy loaded."
                },
                CoreJsonContext.Default.OrganizationPolicyResponse);
        });

        app.MapPut("/admin/organization-policy", async (HttpContext ctx) =>
        {
            var authResult = AuthorizeOperator(ctx, startup, browserSessions, operations, requireCsrf: true, endpointScope: "admin.organization-policy.mutate");
            if (authResult.Failure is not null)
                return authResult.Failure;
            var auth = authResult.Authorization!;

            var requestPayload = await ReadJsonBodyAsync(ctx, CoreJsonContext.Default.OrganizationPolicySnapshot);
            if (requestPayload.Failure is not null)
                return requestPayload.Failure;
            if (requestPayload.Value is null)
                return Results.BadRequest(new MutationResponse { Success = false, Error = "Request body is required." });

            var before = organizationPolicy.GetSnapshot();
            var updated = organizationPolicy.Update(requestPayload.Value);
            RecordOperatorAudit(ctx, operations, auth, "organization_policy_update", "organization-policy", "Updated organization policy.", success: true, before, after: updated);
            return Results.Json(
                new OrganizationPolicyResponse
                {
                    Policy = updated,
                    Message = "Organization policy updated."
                },
                CoreJsonContext.Default.OrganizationPolicyResponse);
        });
    }
}
