using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using ticolinea.stream.service.Attributes;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class NodeApiKeyAttributeTests
{
    private static AuthorizationFilterContext Ctx(string? key)
    {
        var http = new DefaultHttpContext();
        if (key != null) http.Request.Headers["X-Auth-API-Key"] = key;
        var actionCtx = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionCtx, new List<IFilterMetadata>());
    }

    [Fact]
    public async Task Missing_key_is_unauthorized()
    {
        var ctx = Ctx(null);
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Wrong_key_is_forbidden()
    {
        ticolinea.stream.service.Constantes.Global.TestSetPanelApiKey("REALKEY");
        var ctx = Ctx("WRONG");
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Correct_key_passes()
    {
        ticolinea.stream.service.Constantes.Global.TestSetPanelApiKey("REALKEY");
        var ctx = Ctx("REALKEY");
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task Empty_expected_key_forbids_even_a_present_header()
    {
        // Node misconfig: no key set -> must reject, not accept-anything.
        ticolinea.stream.service.Constantes.Global.TestSetPanelApiKey("");
        var ctx = Ctx("anything");
        await new NodeApiKeyAttribute().OnAuthorizationAsync(ctx);
        ctx.Result.Should().BeOfType<ForbidResult>();
    }
}
