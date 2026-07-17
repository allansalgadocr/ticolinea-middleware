using System.Net.Http.Json;
using ticolinea.stream.service.Modelos;

namespace ticolinea.stream.service.Helpers;

// Fetches the provider's catalog from the panel. Returns null on ANY failure
// (non-2xx, timeout, exception) — the caller treats null as "keep current data".
public class CatalogClient
{
    private readonly HttpClient _http;
    private readonly string _panelApiUrl;
    private readonly string _apiKey;
    private readonly string _slug;

    public CatalogClient(HttpClient http, string panelApiUrl, string apiKey, string slug)
    {
        _http = http;
        _panelApiUrl = panelApiUrl.TrimEnd('/');
        _apiKey = apiKey;
        _slug = slug;
    }

    public async Task<List<CatalogStream>?> FetchAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_panelApiUrl}/providers/{_slug}/catalog");
            req.Headers.Add("X-Auth-API-Key", _apiKey);
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<List<CatalogStream>>();
        }
        catch
        {
            return null; // network/parse failure → keep stale data
        }
    }
}
