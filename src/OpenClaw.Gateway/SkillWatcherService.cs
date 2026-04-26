using OpenClaw.Agent;
using OpenClaw.Core.Skills;
using System.Threading.Channels;

namespace OpenClaw.Gateway;

internal sealed class SkillWatcherService : IAsyncDisposable, IDisposable
{
    private readonly IAgentRuntime _agentRuntime;
    private readonly ILogger<SkillWatcherService> _logger;
    private readonly Channel<byte> _reloadRequests = Channel.CreateUnbounded<byte>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _gate = new();
    private readonly string[] _watchRoots;
    private CancellationTokenSource? _reloadLoopCts;
    private Task? _reloadLoopTask;
    private CancellationToken _stoppingToken;
    private bool _started;
    private bool _disposed;

    public SkillWatcherService(
        SkillsConfig config,
        string? workspacePath,
        IReadOnlyList<string>? pluginSkillDirs,
        IAgentRuntime agentRuntime,
        ILogger<SkillWatcherService> logger)
    {
        _agentRuntime = agentRuntime;
        _logger = logger;
        _watchRoots = GetWatchRoots(config, workspacePath, pluginSkillDirs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Start(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            _started = true;
            _stoppingToken = stoppingToken;
            _reloadLoopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _reloadLoopTask = RunReloadLoopAsync(_reloadLoopCts.Token);
        }

        foreach (var root in _watchRoots)
        {
            try
            {
                Directory.CreateDirectory(root);

                var watcher = new FileSystemWatcher(root, "SKILL.md")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.CreationTime |
                                   NotifyFilters.DirectoryName |
                                   NotifyFilters.FileName |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                watcher.Changed += OnWatcherChanged;
                watcher.Created += OnWatcherChanged;
                watcher.Deleted += OnWatcherChanged;
                watcher.Renamed += OnWatcherRenamed;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to watch skill directory {Path}", root);
            }
        }

        if (_watchers.Count == 0)
        {
            _logger.LogInformation("Skill watcher disabled because no skill directories are available.");
            return;
        }

        _logger.LogInformation("Watching {Count} skill directories for SKILL.md changes.", _watchers.Count);
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        FileSystemWatcher[] watchers;
        CancellationTokenSource? reloadLoopCts;
        Task? reloadLoopTask;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            watchers = [.. _watchers];
            _watchers.Clear();
            reloadLoopCts = _reloadLoopCts;
            _reloadLoopCts = null;
            reloadLoopTask = _reloadLoopTask;
            _reloadLoopTask = null;
        }

        foreach (var watcher in watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnWatcherChanged;
            watcher.Created -= OnWatcherChanged;
            watcher.Deleted -= OnWatcherChanged;
            watcher.Renamed -= OnWatcherRenamed;
            watcher.Dispose();
        }

        _reloadRequests.Writer.TryComplete();
        reloadLoopCts?.Cancel();

        if (reloadLoopTask is not null)
        {
            try
            {
                await reloadLoopTask;
            }
            catch (OperationCanceledException) when (reloadLoopCts?.IsCancellationRequested == true)
            {
            }
        }

        reloadLoopCts?.Dispose();
    }

    private static IEnumerable<string> GetWatchRoots(
        SkillsConfig config,
        string? workspacePath,
        IReadOnlyList<string>? pluginSkillDirs)
    {
        foreach (var dir in config.Load.ExtraDirs)
        {
            if (!string.IsNullOrWhiteSpace(dir))
                yield return dir;
        }

        if (config.Load.IncludeBundled)
            yield return Path.Combine(AppContext.BaseDirectory, "skills");

        if (config.Load.IncludeManaged)
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".openclaw",
                "skills");
        }

        if (pluginSkillDirs is not null)
        {
            foreach (var dir in pluginSkillDirs)
            {
                if (!string.IsNullOrWhiteSpace(dir))
                    yield return dir;
            }
        }

        if (config.Load.IncludeWorkspace && !string.IsNullOrWhiteSpace(workspacePath))
            yield return Path.Combine(workspacePath, "skills");
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) => ScheduleReload();

    internal void NotifySkillChanged() => ScheduleReload();

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed || !_started || _stoppingToken.IsCancellationRequested)
                return;
        }

        _reloadRequests.Writer.TryWrite(0);
    }

    private async Task RunReloadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _reloadRequests.Reader.WaitToReadAsync(ct))
            {
                while (_reloadRequests.Reader.TryRead(out _))
                {
                }

                await WaitForQuietPeriodAsync(ct);
                await TriggerReloadAsync();
            }
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForQuietPeriodAsync(CancellationToken ct)
    {
        while (true)
        {
            var quietPeriodTask = Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            var signalTask = _reloadRequests.Reader.WaitToReadAsync(ct).AsTask();
            var completedTask = await Task.WhenAny(quietPeriodTask, signalTask);
            if (completedTask == quietPeriodTask)
                return;

            if (!await signalTask)
                return;

            while (_reloadRequests.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task TriggerReloadAsync()
    {
        lock (_gate)
        {
            if (_disposed || _stoppingToken.IsCancellationRequested)
                return;
        }

        try
        {
            var loadedSkillNames = await _agentRuntime.ReloadSkillsAsync(_stoppingToken);
            _logger.LogInformation("Reloaded {Count} skills after file change.", loadedSkillNames.Count);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload skills after file change.");
        }
    }
}
