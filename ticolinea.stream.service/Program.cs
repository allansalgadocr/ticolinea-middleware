using Microsoft.AspNetCore.HttpOverrides;
using Hangfire;
using ticolinea.stream.service;
using Hangfire.InMemory;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddLog4net();

var app = builder.Build();

// Append a log message to verify log4net is working
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Log4net integration is working! Application starting...");

//app.MapHealthChecks("/healthz");
//Jobs.SincronizarS3();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseSwagger();
app.UseSwaggerUI();

DashboardOptions dashboardOptions = new DashboardOptions
{
    Authorization = new[] { new DashboardNoAuthorizationFilter() }
};

app.UseHangfireDashboard("/dashboard", dashboardOptions);

RecurringJob.AddOrUpdate("check_streams", () => Jobs.RevisarStreams(), Cron.Minutely());
RecurringJob.AddOrUpdate("stop_not_inuse_streams", () => Jobs.DetenerStreamsSinUso(), "*/10 * * * *");
RecurringJob.AddOrUpdate("remove_old_streams", () => Jobs.EliminarArchivosViejos(), "*/30 * * * *");
RecurringJob.AddOrUpdate("kill_connections", () => Jobs.MataConexionesSinUso(), "*/5 * * * *");
RecurringJob.AddOrUpdate("check_offline_streams", () => Jobs.VerificarStreamsCaidos(), "*/35 * * * *");
RecurringJob.AddOrUpdate("remove_large_files", () => Jobs.EliminarArchivosGrandes(), "*/5 * * * *");
RecurringJob.AddOrUpdate("remove_stream_errors", () => Jobs.LimpiaErrores(), Cron.Daily);

RecurringJob.AddOrUpdate("cleanup", () => Jobs.CleanUpOldJobs(), Cron.Hourly);

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseCors(builder => {
    builder.AllowAnyOrigin();
    builder.AllowAnyMethod();
    builder.AllowAnyHeader();
});

app.UseAuthorization();

app.MapControllers();

app.Run();
