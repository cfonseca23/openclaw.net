using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway;

/// <summary>
/// Tracks the latest auth event per channel and provides a broadcast stream for live updates.
/// Used to surface QR codes and connection state to the admin UI and Companion app.
/// </summary>
internal sealed class ChannelAuthEventStore
{
    private readonly ConcurrentDictionary<string, BridgeChannelAuthEvent> _latest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Channel<BridgeChannelAuthEvent>> _subscribers = new();
    private int _nextSubscriberId;

    /// <summary>
    /// Records an auth event and broadcasts it to all subscribers.
    /// </summary>
    public void Record(BridgeChannelAuthEvent evt)
    {
        _latest[BuildKey(evt.ChannelId, evt.AccountId)] = evt;

        foreach (var sub in _subscribers.Values)
        {
            sub.Writer.TryWrite(evt);
        }
    }

    /// <summary>
    /// Gets the latest auth event for a channel, or null if none recorded.
    /// </summary>
    public BridgeChannelAuthEvent? GetLatest(string channelId, string? accountId = null)
        => _latest.TryGetValue(BuildKey(channelId, accountId), out var evt) ? evt : null;

    /// <summary>
    /// Gets the latest auth events for a channel.
    /// </summary>
    public IReadOnlyList<BridgeChannelAuthEvent> GetAll(string? channelId = null)
        => _latest.Values
            .Where(evt => channelId is null || string.Equals(evt.ChannelId, channelId, StringComparison.Ordinal))
            .OrderByDescending(static evt => evt.UpdatedAtUtc)
            .ToList();

    public void ClearChannel(string channelId)
    {
        foreach (var key in _latest.Keys.Where(key => key.StartsWith(channelId + "\n", StringComparison.Ordinal)).ToArray())
        {
            _latest.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Subscribes to live auth event updates. Dispose the returned object to unsubscribe.
    /// </summary>
    public AuthEventSubscription Subscribe()
    {
        var id = Interlocked.Increment(ref _nextSubscriberId);
        var channel = Channel.CreateBounded<BridgeChannelAuthEvent>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _subscribers[id] = channel;
        return new AuthEventSubscription(id, channel.Reader, this);
    }

    private void Unsubscribe(int id)
    {
        if (_subscribers.TryRemove(id, out var channel))
            channel.Writer.TryComplete();
    }

    private static string BuildKey(string channelId, string? accountId)
        => $"{channelId}\n{accountId ?? string.Empty}";

    internal sealed class AuthEventSubscription : IDisposable
    {
        private readonly int _id;
        private readonly ChannelAuthEventStore _store;

        public ChannelReader<BridgeChannelAuthEvent> Reader { get; }

        public AuthEventSubscription(int id, ChannelReader<BridgeChannelAuthEvent> reader, ChannelAuthEventStore store)
        {
            _id = id;
            Reader = reader;
            _store = store;
        }

        public void Dispose() => _store.Unsubscribe(_id);
    }
}
