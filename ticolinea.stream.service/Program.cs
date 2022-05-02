using Microsoft.AspNetCore.HttpOverrides;
using Hangfire;
using Hangfire.InMemory;
using ticolinea.stream.service;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddHangfire(x => x.UseInMemoryStorage());
builder.Services.AddHangfireServer();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

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
