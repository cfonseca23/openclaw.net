using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Manage scheduled cron jobs. List, inspect, and trigger jobs.
/// </summary>
internal sealed class CronTool : IToolWithContext
{
    private readonly ICronJobSource _cronSource;
    private readonly MessagePipeline _pipeline;

    public CronTool(ICronJobSource cronSource, MessagePipeline pipeline)
    {
        _cronSource = cronSource;
        _pipeline = pipeline;
    }

    public string Name => "cron";
    public string Description => "Manage scheduled cron jobs. List configured jobs, get details, or trigger immediate execution.";
    public string ParameterSchema => """{"type":"object","properties":{"action":{"type":"string","enum":["list","get","run"],"description":"Action to perform"},"name":{"type":"string","description":"Job name (required for get/run)"}},"required":["action"]}""";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: cron requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = args.RootElement;

        var action = GetString(root, "action") ?? "list";

        return action switch
        {
            "list" => ListJobs(),
            "get" => GetJob(root),
            "run" => await RunJobAsync(root, ct),
            _ => $"Error: Unknown action '{action}'. Use list, get, or run."
        };
    }

    private string ListJobs()
    {
        var jobs = _cronSource.GetJobs();
        if (jobs.Count == 0)
            return "No cron jobs configured.";

        var sb = new StringBuilder();
        sb.AppendLine($"Cron jobs ({jobs.Count}):");
        foreach (var job in jobs)
        {
            sb.AppendLine($"  [{job.Name}] {job.CronExpression}");
            var promptPreview = job.Prompt.Length > 60 ? job.Prompt[..60] + "…" : job.Prompt;
            sb.AppendLine($"    Prompt: {promptPreview}");
            if (job.RunOnStartup)
                sb.AppendLine("    RunOnStartup: true");
        }
        return sb.ToString().TrimEnd();
    }

    private string GetJob(JsonElement root)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for get action.";

        var jobs = _cronSource.GetJobs();
        var job = jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return $"Job '{name}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Job: {job.Name}");
        sb.AppendLine($"  Schedule: {job.CronExpression}");
        sb.AppendLine($"  Prompt: {job.Prompt}");
        sb.AppendLine($"  RunOnStartup: {job.RunOnStartup}");
        if (job.SessionId is not null) sb.AppendLine($"  SessionId: {job.SessionId}");
        if (job.ChannelId is not null) sb.AppendLine($"  ChannelId: {job.ChannelId}");
        if (job.RecipientId is not null) sb.AppendLine($"  RecipientId: {job.RecipientId}");
        if (job.Timezone is not null) sb.AppendLine($"  Timezone: {job.Timezone}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> RunJobAsync(JsonElement root, CancellationToken ct)
    {
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return "Error: 'name' is required for run action.";

        var jobs = _cronSource.GetJobs();
        var job = jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return $"Job '{name}' not found.";

        var message = new InboundMessage
        {
            ChannelId = job.ChannelId ?? "cron",
            SenderId = "cron",
            SessionId = job.SessionId ?? $"cron:{job.Name}",
            CronJobName = job.Name,
            Text = job.Prompt,
            Subject = job.Subject,
            IsSystem = true,
        };

        await _pipeline.InboundWriter.WriteAsync(message, ct);
        return $"Job '{name}' triggered for immediate execution.";
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
