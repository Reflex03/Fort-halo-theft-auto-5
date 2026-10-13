namespace CthExtract.Fortnite.Net;

/// <summary>
/// Every HTTP request the Fortnite module makes goes through this handler. It only lets through GET requests to the
/// three community key/mapping services (and their file hosts), and refuses anything that looks like a native
/// library or an archive of one, so no code path can download a DLL. Redirects are followed here (one hop at a time)
/// so a redirect to another host is checked too.
/// </summary>
public sealed class HttpGuard : DelegatingHandler
{
    public static readonly IReadOnlyList<string> AllowedHosts =
    [
        "api.fortniteporting.app",
        "supabase.fortniteporting.app",
        "uedb.dev",
        "data.uedb.dev",
        "fortnite-api.com",
    ];

    private static readonly string[] ForbiddenExtensions =
        [".dll", ".so", ".dylib", ".exe", ".zip", ".gz", ".7z", ".rar", ".tar", ".msi", ".pdb", ".bin"];

    private readonly Action<string>? _log;
    private readonly List<string> _requested = new();

    public HttpGuard(HttpMessageHandler inner, Action<string>? log = null) : base(inner) => _log = log;

    /// <summary>Every URL that was requested (allowed or refused), for diagnostics and tests.</summary>
    public IReadOnlyList<string> Requested { get { lock (_requested) return _requested.ToList(); } }

    public static bool IsAllowed(Uri uri, out string why)
    {
        if (!uri.IsAbsoluteUri) { why = "relative URL"; return false; }
        if (uri.Scheme != Uri.UriSchemeHttps) { why = "not https"; return false; }
        var host = uri.Host.ToLowerInvariant();
        if (!AllowedHosts.Contains(host)) { why = $"host {host} is not one of the key/mapping services"; return false; }
        var path = uri.AbsolutePath.ToLowerInvariant();
        foreach (var ext in ForbiddenExtensions)
            if (path.EndsWith(ext, StringComparison.Ordinal)) { why = $"refusing to download a '{ext}' file"; return false; }
        why = "";
        return true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            var uri = request.RequestUri ?? throw new HttpRequestException("request without URL");
            lock (_requested) _requested.Add(uri.ToString());
            if (request.Method != HttpMethod.Get) throw new HttpRequestException($"refused {request.Method} {uri}: only GET is used");
            if (!IsAllowed(uri, out var why))
            {
                _log?.Invoke($"[fortnite] refused request {uri}: {why}");
                throw new HttpRequestException($"refused {uri}: {why}");
            }

            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is >= 300 and < 400 && response.Headers.Location is { } loc)
            {
                if (hop >= 4) { response.Dispose(); throw new HttpRequestException($"too many redirects from {uri}"); }
                var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                response.Dispose();
                request = new HttpRequestMessage(HttpMethod.Get, next);
                continue;
            }
            return response;
        }
    }

    /// <summary>The module's HTTP client: guard + automatic decompression, no cookies, redirects handled by the guard.</summary>
    public static (HttpClient Client, HttpGuard Guard) CreateClient(HttpMessageHandler? inner, TimeSpan timeout, Action<string>? log)
    {
        inner ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var guard = new HttpGuard(inner, log);
        var client = new HttpClient(guard, disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CraftTheftHalo-extractor/0.1 (+fortnite keys/mappings)");
        return (client, guard);
    }
}
