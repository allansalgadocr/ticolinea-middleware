namespace ticolinea.stream.service.NodeConsole;

// Keeps the console's startup wiring out of Program.cs, which is already long.
public static class ConsoleHosting
{
    private static readonly log4net.ILog _log = log4net.LogManager.GetLogger(typeof(ConsoleHosting));

    /// <summary>
    /// Creates the console's node-local tables and seeds the bootstrap admin once.
    /// Never throws: the console is an add-on, and a console failure must not stop
    /// a node from streaming.
    /// </summary>
    public static async Task InitializeAsync(IConfiguration configuration)
    {
        try
        {
            await ConsoleSchema.EnsureAsync(configuration["NodeConsole:SeedPassword"]);
        }
        catch (Exception ex)
        {
            _log.Error("Node console schema init failed; the console will be unavailable.", ex);
        }
    }

    /// <summary>
    /// Serves the built SPA's files (wwwroot/admin) on the node's existing port,
    /// so a client box needs no extra listener, firewall rule or vhost.
    ///
    /// MUST be called BEFORE routing/MapControllers. StaticFileMiddleware
    /// deliberately does nothing once routing has selected an endpoint, so
    /// registering it after MapControllers let the SPA fallback below answer
    /// asset requests with index.html — the browser then refused the bundle
    /// ("MIME type text/html") and the app rendered blank.
    /// </summary>
    public static void UseConsoleStaticFiles(WebApplication app) => app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            // Vite fingerprints every asset (index-<hash>.js/css), so the URL
            // changes whenever the content does. They can therefore be cached
            // hard — and must be, or each deploy re-downloads the whole bundle.
            if (ctx.Context.Request.Path.StartsWithSegments("/admin/assets"))
                ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        },
    });

    /// <summary>
    /// Client-side routing: any /admin/* path that isn't a real file returns
    /// index.html, or refreshing on /admin/canales 404s. Call AFTER
    /// MapControllers so it can never shadow an /api route.
    /// </summary>
    public static void MapConsoleSpa(WebApplication app)
    {
        app.MapFallback("/admin/{*path}", async context =>
        {
            var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin", "index.html");
            if (!File.Exists(index))
            {
                context.Response.StatusCode = 404;
                return;
            }
            // index.html is the ONLY unfingerprinted file, and it names which
            // asset hashes to load. If a browser caches it, the client keeps
            // fetching the previous build's bundle after a deploy and the update
            // silently never arrives. It must always be revalidated.
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(index);
        });
    }
}
