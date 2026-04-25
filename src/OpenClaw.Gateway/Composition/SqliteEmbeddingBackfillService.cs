using Microsoft.Extensions.Hosting;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Composition;

internal sealed class SqliteEmbeddingBackfillService : BackgroundService
{
    private readonly GatewayConfig _config;
    private readonly IMemoryStore _memoryStore;
    private readonly ILogger<SqliteEmbeddingBackfillService> _logger;

    public SqliteEmbeddingBackfillService(
        GatewayConfig config,
        IMemoryStore memoryStore,
        ILogger<SqliteEmbeddingBackfillService> logger)
    {
        _config = config;
        _memoryStore = memoryStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(_config.Memory.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
            return;

        if (!_config.Memory.Sqlite.EnableVectors ||
            string.IsNullOrWhiteSpace(_config.Memory.Sqlite.EmbeddingModel) ||
            _memoryStore is not SqliteMemoryStore sqliteStore)
        {
            return;
        }

        try
        {
            await sqliteStore.BackfillEmbeddingsAsync(ct: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Embedding backfill failed ({Type}): {Reason}. Memory vector search may be incomplete until resolved.",
                ex.GetType().Name,
                ex.Message);
        }
    }
}
