using Microsoft.AspNetCore.HttpOverrides;
using Hangfire;
using ticolinea.stream.service;
using ticolinea.stream.service.Config;
using ticolinea.stream.service.Constantes;
using ticolinea.stream.service.Helpers;
using Hangfire.InMemory;

var builder = WebApplication.CreateBuilder(args);

// Support provider-specific configuration via PROVIDER environment variable
// Usage: PROVIDER=fibraencasa dotnet run
var provider = Environment.GetEnvironmentVariable("PROVIDER") ?? "main";
if (!string.IsNullOrEmpty(provider))
{
    var providerConfigFile = $"appsettings.{provider}.json";
    var configPath = Path.Combine(builder.Environment.ContentRootPath, providerConfigFile);
    var fileExists = File.Exists(configPath);
    
    Console.WriteLine($"=== Configuration Loading ===");
    Console.WriteLine($"Provider: {provider}");
    Console.WriteLine($"Config file: {providerConfigFile}");
    Console.WriteLine($"Content root: {builder.Environment.ContentRootPath}");
    Console.WriteLine($"Config path: {configPath}");
    Console.WriteLine($"Config file exists: {fileExists}");
    
    builder.Configuration.AddJsonFile(providerConfigFile, optional: true, reloadOnChange: true);
    
    if (!fileExists)
    {
        Console.WriteLine($"WARNING: {providerConfigFile} not found at {configPath}");
        Console.WriteLine($"This may cause configuration values to be missing!");
    }
    Console.WriteLine();
}

// Configure settings from appsettings
var streamingSettings = builder.Configuration.GetSection(StreamingSettings.SectionName).Get<StreamingSettings>() ?? new StreamingSettings();
var databaseSettings = builder.Configuration.GetSection(DatabaseSettings.SectionName).Get<DatabaseSettings>() ?? new DatabaseSettings();
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

// Debug: Check raw configuration values
var dbSection = builder.Configuration.GetSection(DatabaseSettings.SectionName);
var jwtSection = builder.Configuration.GetSection(JwtSettings.SectionName);
Console.WriteLine($"=== Configuration Debug ===");
Console.WriteLine($"Database section exists: {dbSection.Exists()}");
Console.WriteLine($"Database ConnectionString from config: {(string.IsNullOrEmpty(dbSection["ConnectionString"]) ? "(empty)" : "***configured***")}");
Console.WriteLine($"JWT section exists: {jwtSection.Exists()}");
Console.WriteLine($"JWT Issuer from config: {(string.IsNullOrEmpty(jwtSection["Issuer"]) ? "(empty)" : jwtSection["Issuer"])}");
Console.WriteLine($"JWT Audience from config: {(string.IsNullOrEmpty(jwtSection["Audience"]) ? "(empty)" : jwtSection["Audience"])}");
Console.WriteLine($"JWT NodeProviderId from config: {(string.IsNullOrEmpty(jwtSection["NodeProviderId"]) ? "(empty)" : jwtSection["NodeProviderId"])}");
Console.WriteLine($"JWT PublicKey from config: {(string.IsNullOrEmpty(jwtSection["PublicKey"]) ? "(empty)" : "***configured***")}");
Console.WriteLine($"JWT PanelApiUrl from config: {(string.IsNullOrEmpty(jwtSection["PanelApiUrl"]) ? "(empty)" : jwtSection["PanelApiUrl"])}");
Console.WriteLine();

// Initialize global settings
Global.Initialize(streamingSettings, databaseSettings);
TokenValidation.Initialize(jwtSettings);

// Log startup configuration
Console.WriteLine("========================================");
Console.WriteLine($"  STREAMING NODE STARTING");
Console.WriteLine($"  Provider: {streamingSettings.ProviderId} ({streamingSettings.ProviderName})");
Console.WriteLine("========================================");
Console.WriteLine();
Console.WriteLine("=== Database Settings ===");
Console.WriteLine($"  ConnectionString: {(string.IsNullOrEmpty(databaseSettings.ConnectionString) ? "(not set)" : "configured")}");
Console.WriteLine();
Console.WriteLine("=== Streaming Settings ===");
Console.WriteLine($"  StreamsFolder: {streamingSettings.StreamsFolder}");
Console.WriteLine($"  SegmentBaseUrl: {streamingSettings.SegmentBaseUrl}");
Console.WriteLine($"  StreamsBaseUrl: {streamingSettings.StreamsBaseUrl}");
Console.WriteLine($"  EnableStreamExecution: {streamingSettings.EnableStreamExecution}");
Console.WriteLine();
Console.WriteLine("=== JWT Settings ===");
Console.WriteLine($"  Issuer: {(string.IsNullOrEmpty(jwtSettings.Issuer) ? "(not set)" : jwtSettings.Issuer)}");
Console.WriteLine($"  Audience: {(string.IsNullOrEmpty(jwtSettings.Audience) ? "(not set)" : jwtSettings.Audience)}");
Console.WriteLine($"  NodeProviderId: {(string.IsNullOrEmpty(jwtSettings.NodeProviderId) ? "(not set)" : jwtSettings.NodeProviderId)}");
Console.WriteLine($"  PublicKey: {(string.IsNullOrEmpty(jwtSettings.PublicKey) ? "(not set)" : "configured (" + jwtSettings.PublicKey.Length + " chars)")}");
Console.WriteLine($"  PanelApiUrl: {(string.IsNullOrEmpty(jwtSettings.PanelApiUrl) ? "(not set)" : jwtSettings.PanelApiUrl)}");
Console.WriteLine("========================================");

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Apagado ordenado. El default del host genérico es 5s, PERO el default de
// Hangfire (BackgroundJobServerOptions.ShutdownTimeout) es 15s: el host cancelaba
// el drenaje de Hangfire a los 5s, StopAsync lanzaba OperationCanceledException
// sin capturar, y .NET abortaba el proceso — SIGABRT + core dump en CADA parada
// (visto en producción: "Main process exited, code=dumped, status=6/ABRT" en el
// reinicio nocturno de las 03:00 y en cada deploy).
//
// El core dump no era cosmético: mataba los ~95 ffmpeg de golpe y todos volvían a
// hacer handshake a la vez, justo el patrón que el borde de red del cliente no
// aguanta. Host 30s > Hangfire 15s deja que el drenaje termine dentro de su
// ventana; el orden entre ambos es lo que importa, no los valores exactos.
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});

// Add services to the container.
builder.Services.AddHangfire(x => x.UseInMemoryStorage());
builder.Services.AddHangfireServer(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHealthChecks();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddLog4net(builder.Configuration);

// Add memory cache for performance optimization
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient("PanelApi");
builder.Services.AddSingleton<ticolinea.stream.service.Services.ActivityTrackingService>();

// Output-progress watchdog (detects ffmpeg ALIVE but producing no new HLS output).
// Registered unconditionally; the gate is INTERNAL (Watchdog:Enabled read every
// cycle) so a config toggle via reloadOnChange applies without a service restart.
// Default OFF — only the provider deploy template turns it on.
builder.Services.AddHostedService<ticolinea.stream.service.Services.OutputWatchdogService>();

var app = builder.Build();

// Static Hangfire jobs (Jobs class) have no DI container of their own; expose the
// named "PanelApi" HttpClient through Global the same way other runtime settings
// (Global.Initialize / TokenValidation.Initialize) are made available to them.
Global.HttpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

// Node console: create its own tables (node-local, not part of the panel's
// schema.sql) and seed the bootstrap admin the first time only. Awaited before
// the app serves traffic so /admin can never hit a missing table.
await ticolinea.stream.service.NodeConsole.ConsoleHosting.InitializeAsync(builder.Configuration);

// Configure the HTTP request pipeline.
/*if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}*/

app.UseDeveloperExceptionPage();

app.UseSwagger();
app.UseSwaggerUI();

DashboardOptions dashboardOptions = new DashboardOptions
{
    Authorization = new[] { new DashboardNoAuthorizationFilter() }
};

app.UseHangfireDashboard("/dashboard", dashboardOptions);

// 🚀 OPTIMIZED JOB SCHEDULING - Reduced database load
// Changed from every minute to every 2 minutes (50% reduction in DB queries)
RecurringJob.AddOrUpdate("check_streams", () => Jobs.RevisarStreams(), "*/2 * * * *");
RecurringJob.AddOrUpdate("stop_not_inuse_streams", () => Jobs.DetenerStreamsSinUso(), "*/10 * * * *");
RecurringJob.AddOrUpdate("remove_old_streams", () => Jobs.EliminarArchivosViejos(), "*/30 * * * *");
// Changed from every 5 minutes to every 10 minutes (50% reduction in DB queries)
RecurringJob.AddOrUpdate("kill_connections", () => Jobs.MataConexionesSinUso(), "*/10 * * * *");
RecurringJob.AddOrUpdate("check_offline_streams", () => Jobs.VerificarStreamsCaidos(), "*/35 * * * *");
// Changed from every 5 minutes to every 15 minutes (67% reduction in DB queries)
RecurringJob.AddOrUpdate("remove_large_files", () => Jobs.EliminarArchivosGrandes(), "*/15 * * * *");
// Daily at 04:10 local (after the 03:00 restart window): prune TL.* log files
// older than Logging:RetentionDays (default 14) — date rolling never does.
RecurringJob.AddOrUpdate("clean_old_logs", () => Jobs.LimpiarLogsViejos(), "10 4 * * *");
RecurringJob.AddOrUpdate("remove_stream_errors", () => Jobs.LimpiaErrores(), Cron.Daily);
RecurringJob.AddOrUpdate("monitor_system_resources", () => Jobs.MonitorearRecursosSistema(), "*/10 * * * *"); // Every 10 minutes

RecurringJob.AddOrUpdate("cleanup", () => Jobs.CleanUpOldJobs(), Cron.Hourly);

// Package sync (Spec B): pull the assigned channel package from the panel on a
// recurring schedule, and once on boot so a freshly (re)started node doesn't
// wait a full interval before it has channels.
var syncHours = builder.Configuration.GetValue<int?>("PackageSync:IntervalHours") ?? 6;
RecurringJob.AddOrUpdate("sync_package_catalog", () => Jobs.SyncPackageCatalog(), $"0 */{syncHours} * * *");
BackgroundJob.Enqueue(() => Jobs.SyncPackageCatalog()); // run once on boot

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Console SPA assets, then an EXPLICIT UseRouting.
// WebApplication injects UseRouting at the very start of the pipeline unless the
// app calls it itself. With the implicit one, routing matched the SPA fallback
// before StaticFileMiddleware ever ran, and static files are skipped once an
// endpoint is selected — so every JS/CSS asset came back as index.html.
// Calling UseRouting here pins it after static files and keeps CORS/authorization
// in their required position between routing and the endpoints.
ticolinea.stream.service.NodeConsole.ConsoleHosting.UseConsoleStaticFiles(app);

app.UseRouting();

app.UseCors(policyBuilder => {
    policyBuilder.AllowAnyOrigin();
    policyBuilder.AllowAnyMethod();
    policyBuilder.AllowAnyHeader();
});

app.UseAuthorization();

app.MapControllers();

// Node console SPA (wwwroot/admin, built by admin-ui). Served from this same
// port so a client node needs no extra listener, firewall rule or vhost.
// Registered AFTER MapControllers so it can never shadow an /api route.
ticolinea.stream.service.NodeConsole.ConsoleHosting.MapConsoleSpa(app);

// Defensa en profundidad para el apagado. El timeout de arriba resuelve la causa
// (host 30s > Hangfire 15s), pero si algún hosted service llegara a pasarse igual,
// la excepción sale sin capturar de RunAsync y .NET aborta el proceso: SIGABRT,
// core dump, y systemd reporta 'core-dump' en vez de una parada limpia.
//
// Un drenaje lento en el apagado no es una falla del nodo — el trabajo ya terminó.
// Se registra y se sale con 0: systemd ve una parada normal, el reinicio nocturno
// no queda marcado como fallo, y no se escribe un core de un proceso de ~17 GB.
try
{
    await app.RunAsync();
}
catch (OperationCanceledException)
{
    Console.WriteLine("[Shutdown] Un hosted service excedió la ventana de apagado; saliendo limpio.");
}
