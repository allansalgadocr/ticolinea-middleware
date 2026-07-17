using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class CatalogClientTests
{
    private class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code; private readonly string _body;
        public HttpRequestMessage? Seen;
        public StubHandler(HttpStatusCode code, string body) { _code = code; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken _)
        { Seen = r; return Task.FromResult(new HttpResponseMessage(_code) { Content = new StringContent(_body, Encoding.UTF8, "application/json") }); }
    }

    private static CatalogClient Make(StubHandler h) =>
        new(new HttpClient(h), "http://panel/api/v2", "KEY", "acme");

    [Fact]
    public async Task Parses_ok_response_and_sends_key_and_slug()
    {
        var h = new StubHandler(HttpStatusCode.OK, "[{\"Id\":10,\"NombreStream\":\"A\",\"FuenteStream\":\"u\"}]");
        var result = await Make(h).FetchAsync();
        result.Should().NotBeNull();
        result!.Single().Id.Should().Be(10);
        h.Seen!.RequestUri!.ToString().Should().Be("http://panel/api/v2/providers/acme/catalog");
        h.Seen.Headers.GetValues("X-Auth-API-Key").Single().Should().Be("KEY");
    }

    [Fact]
    public async Task Parses_camelCase_panel_response()
    {
        // ASP.NET Core serializes camelCase — prove the client deserializes it (not silently empty).
        var body = "[{\"id\":10,\"nombreStream\":\"A\",\"fuenteStream\":\"http://src/10\",\"tipo\":1,\"intervalo\":4,\"segmentos\":4}]";
        var h = new StubHandler(System.Net.HttpStatusCode.OK, body);
        var result = await Make(h).FetchAsync();
        result.Should().NotBeNull();
        var s = result!.Single();
        s.Id.Should().Be(10);
        s.NombreStream.Should().Be("A");
        s.FuenteStream.Should().Be("http://src/10");
        s.Tipo.Should().Be((sbyte)1);
        s.Intervalo.Should().Be((sbyte)4);
    }

    [Fact]
    public async Task Non_success_returns_null()
        => (await Make(new StubHandler(HttpStatusCode.InternalServerError, "")).FetchAsync()).Should().BeNull();

    [Fact]
    public async Task Exception_returns_null()
    {
        var client = new CatalogClient(new HttpClient(new ThrowingHandler()), "http://panel/api/v2", "KEY", "acme");
        (await client.FetchAsync()).Should().BeNull();
    }
    private class ThrowingHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => throw new HttpRequestException("down"); }
}
