namespace OpenClaw.Client;

public static class GatewayEndpointResolver
{
    public static bool TryResolveHttpBaseUrl(string serverUrl, out string? httpBaseUrl)
    {
        httpBaseUrl = null;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return false;

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme switch
            {
                "ws" => "http",
                "wss" => "https",
                "http" or "https" => uri.Scheme,
                _ => uri.Scheme
            }
        };

        if (builder.Scheme is "http")
            builder.Port = uri.IsDefaultPort && uri.Scheme == "ws" ? 80 : builder.Port;
        else if (builder.Scheme is "https")
            builder.Port = uri.IsDefaultPort && uri.Scheme == "wss" ? 443 : builder.Port;

        var path = builder.Path;
        if (path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            path = path[..^3];
        if (string.IsNullOrWhiteSpace(path))
            path = "/";
        builder.Path = path;
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        httpBaseUrl = builder.Uri.GetLeftPart(UriPartial.Authority) + builder.Path.TrimEnd('/');
        return true;
    }
}
