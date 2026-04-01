using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Integrations;

/// <summary>
/// Resolves agent routing for inbound messages based on channel/sender matching.
/// </summary>
internal sealed class AgentRouteResolver
{
    private readonly RoutingConfig _config;

    public AgentRouteResolver(RoutingConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Resolves a route for the given channel and sender.
    /// Returns null if no route matches or routing is disabled.
    /// </summary>
    public AgentRouteConfig? Resolve(string channelId, string senderId)
    {
        if (!_config.Enabled || _config.Routes.Count == 0)
            return null;

        // Try exact match: channel:sender
        var exactKey = $"{channelId}:{senderId}";
        if (_config.Routes.TryGetValue(exactKey, out var exactRoute))
            return exactRoute;

        // Try channel-only match
        foreach (var (_, route) in _config.Routes)
        {
            if (route.ChannelId is null)
                continue;

            if (!string.Equals(route.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
                continue;

            // If sender filter specified, must match
            if (route.SenderId is not null &&
                !string.Equals(route.SenderId, senderId, StringComparison.OrdinalIgnoreCase))
                continue;

            return route;
        }

        // Try wildcard routes (no channel filter)
        foreach (var (_, route) in _config.Routes)
        {
            if (route.ChannelId is not null)
                continue;

            if (route.SenderId is not null &&
                !string.Equals(route.SenderId, senderId, StringComparison.OrdinalIgnoreCase))
                continue;

            return route;
        }

        return null;
    }
}
