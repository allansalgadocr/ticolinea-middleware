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

// Add services to the container.
builder.Services.AddHangfire(x => x.UseInMemoryStorage());
builder.Services.AddHangfireServer();
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

app.UseCors(policyBuilder => {
    policyBuilder.AllowAnyOrigin();
    policyBuilder.AllowAnyMethod();
    policyBuilder.AllowAnyHeader();
});

app.UseAuthorization();

app.MapControllers();

await app.RunAsync();
