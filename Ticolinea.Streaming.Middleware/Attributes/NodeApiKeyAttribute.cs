using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ticolinea.stream.service.Attributes;

// Inbound gate for node admin endpoints. Validates X-Auth-API-Key against the
// shared panel key the node already holds (Global.PANEL_API_KEY).
public class NodeApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Auth-API-Key", out var key))
        { context.Result = new UnauthorizedResult(); return Task.CompletedTask; }

        var expected = Constantes.Global.PANEL_API_KEY;
        if (string.IsNullOrEmpty(expected) || !string.Equals(key, expected, StringComparison.Ordinal))
        { context.Result = new ForbidResult(); return Task.CompletedTask; }

        return Task.CompletedTask;
    }
}
