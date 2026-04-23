using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ticolinea.stream.service.Helpers;

namespace ticolinea.stream.service.Services;

public class ActivityTrackingService : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _panelApiUrl;
    private readonly string _panelApiKey;
    private readonly ConcurrentDictionary<string, long> _lastReportTimes = new();
    private readonly Timer _evictionTimer;
    private const int ThrottleSeconds = 30;
    private const int EvictionIntervalMs = 300_000; // 5 minutes
    private const int EvictionMaxAgeSeconds = 300;   // 5 minutes

    public ActivityTrackingService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _panelApiUrl = configuration["Jwt:PanelApiUrl"]?.TrimEnd('/') ?? "";
        _panelApiKey = configuration["Jwt:PanelApiKey"] ?? "";

        _evictionTimer = new Timer(EvictStaleEntries, null, EvictionIntervalMs, EvictionIntervalMs);

        if (string.IsNullOrEmpty(_panelApiUrl))
        {
            Console.WriteLine("[ActivityTracking] WARNING: PanelApiUrl not configured. Activity tracking disabled.");
        }
        else
        {
            Console.WriteLine($"[ActivityTracking] Initialized. Reporting to {_panelApiUrl}/clients/activity/track (throttle: {ThrottleSeconds}s)");
        }
    }

    public void TrackIfNeeded(TokenValidationResult validation, int streamId, HttpRequest request, bool isMobile = false)
    {
        if (string.IsNullOrEmpty(_panelApiUrl))
            return;

        if (!int.TryParse(validation.Sub, out var clientId) || clientId <= 0)
            return;

        var key = $"{clientId}:{streamId}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_lastReportTimes.TryGetValue(key, out var lastTime) && (now - lastTime) < ThrottleSeconds)
            return;

        _lastReportTimes[key] = now;

        // Extract request data synchronously (before request is disposed)
        var clientIp = "";
        if (request.Headers.TryGetValue("X-Real-IP", out var realIp))
            clientIp = realIp.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(clientIp))
            clientIp = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        var userAgent = request.Headers["User-Agent"].FirstOrDefault() ?? "";

        // Fire-and-forget
        _ = SendActivityAsync(clientId, streamId, validation.Mac, clientIp, userAgent, isMobile, (int)now);
    }

    /// <summary>
    /// Overload for MAC-based auth (StreamingByMac) — uses ClientValidationResult instead of JWT token.
    /// Reports to the same main Panel API regardless of which tenant middleware is running.
    /// </summary>
    public void TrackIfNeeded(ClientValidationResult validation, int streamId, string macAddress, HttpRequest request, bool isMobile = false)
    {
        if (string.IsNullOrEmpty(_panelApiUrl))
            return;

        if (validation.ClientId <= 0)
            return;

        var key = $"{validation.ClientId}:{streamId}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (_lastReportTimes.TryGetValue(key, out var lastTime) && (now - lastTime) < ThrottleSeconds)
            return;

        _lastReportTimes[key] = now;

        var clientIp = "";
        if (request.Headers.TryGetValue("X-Real-IP", out var realIp))
            clientIp = realIp.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(clientIp))
            clientIp = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        var userAgent = request.Headers["User-Agent"].FirstOrDefault() ?? "";

        _ = SendActivityAsync(validation.ClientId, streamId, macAddress, clientIp, userAgent, isMobile, (int)now);
    }

    private async Task SendActivityAsync(int clientId, int streamId, string? mac, string clientIp, string userAgent, bool isMobile, int timestamp)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("PanelApi");
            client.Timeout = TimeSpan.FromSeconds(5);

            var payload = new
            {
                clientId,
                streamId,
                macAddress = mac,
                clientIp,
                userAgent,
                format = "HLS",
                startDate = timestamp,
                isMobile,
                type = (sbyte)1
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_panelApiUrl}/clients/activity/track")
            {
                Content = content
            };
            request.Headers.Add("X-Auth-API-Key", _panelApiKey);

            await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ActivityTracking] Report failed (non-critical): {ex.Message}");
        }
    }

    private void EvictStaleEntries(object? state)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var evicted = 0;

        foreach (var kvp in _lastReportTimes)
        {
            if ((now - kvp.Value) > EvictionMaxAgeSeconds)
            {
                _lastReportTimes.TryRemove(kvp.Key, out _);
                evicted++;
            }
        }

        if (evicted > 0)
            Console.WriteLine($"[ActivityTracking] Evicted {evicted} stale throttle entries");
    }

    public void Dispose()
    {
        _evictionTimer?.Dispose();
    }
}
