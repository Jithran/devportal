namespace DevPortal.Security;

/// <summary>
/// Defence in depth on top of the loopback-only bind:
/// a Host allowlist against DNS rebinding, same-origin enforcement on the API, read-only methods, and security headers.
/// </summary>
public sealed class LocalOnlyGuard(RequestDelegate next, int port)
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];
    private static readonly string[] ReadOnlyMethods = [HttpMethods.Get, HttpMethods.Head];
    private static readonly string[] TrustedFetchSites = ["same-origin", "none"];

    private readonly HashSet<string> allowedHosts = LoopbackHosts.Select(host => $"{host}:{port}").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> allowedOrigins = LoopbackHosts.Select(host => $"http://{host}:{port}").ToHashSet(StringComparer.OrdinalIgnoreCase);

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        AddSecurityHeaders(context.Response.Headers);

        if (!allowedHosts.Contains(request.Host.Value ?? ""))
            return Reject(context, StatusCodes.Status403Forbidden, "Host not allowed");

        if (!ReadOnlyMethods.Contains(request.Method))
            return Reject(context, StatusCodes.Status405MethodNotAllowed, "Read-only");

        if (request.Path.StartsWithSegments("/api") && IsCrossSite(request))
            return Reject(context, StatusCodes.Status403Forbidden, "Cross-site request rejected");

        return next(context);
    }

    private bool IsCrossSite(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !allowedOrigins.Contains(origin))
            return true;
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        return fetchSite.Length > 0 && !TrustedFetchSites.Contains(fetchSite);
    }

    private static void AddSecurityHeaders(IHeaderDictionary headers)
    {
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
    }

    private static Task Reject(HttpContext context, int status, string reason)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(reason);
    }
}
