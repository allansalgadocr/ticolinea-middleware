using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// DescribeForLog es la identidad que acompaña cada rechazo de token en el log.
// Contrato doble: (1) SIEMPRE nombra al cliente (sub/provider/mac) para que un
// 401 sea diagnosticable — el incidente del 2026-07-30 fue una hora de 401s
// anónimos; (2) NUNCA filtra el token crudo — un JWT en syslog es una
// credencial regalada (el código viejo logueaba preview de 100 chars).
public class TokenValidationDescribeTests
{
    private static string MakeToken(params Claim[] claims) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims));

    [Fact]
    public void Names_the_client_from_its_claims()
    {
        var token = MakeToken(
            new Claim("sub", "client-123"),
            new Claim("providerId", "logicsphere"),
            new Claim("mac", "AA:BB:CC:DD:EE:FF"),
            new Claim("jti", "tok-9"));

        var line = TokenValidation.DescribeForLog(token);

        line.Should().Contain("sub=client-123")
            .And.Contain("provider=logicsphere")
            .And.Contain("mac=AA:BB:CC:DD:EE:FF")
            .And.Contain("jti=tok-9");
    }

    [Fact]
    public void Never_leaks_raw_token_material()
    {
        var token = MakeToken(new Claim("sub", "client-123"));
        var payloadSegment = token.Split('.')[1];

        var line = TokenValidation.DescribeForLog(token);

        // Ningún segmento base64 del JWT puede aparecer en el log.
        line.Should().NotContain(payloadSegment).And.NotContain(token.Split('.')[0]);
    }

    [Fact]
    public void Missing_claims_render_as_dashes_not_crashes()
    {
        var line = TokenValidation.DescribeForLog(MakeToken());
        line.Should().Contain("sub=-").And.Contain("mac=-");
    }

    [Fact]
    public void Unreadable_token_reports_its_shape()
    {
        // "garbage" (0 puntos) vs "a.b" (1 punto): la forma distingue "no era un
        // JWT" de "JWT truncado" sin volcar el contenido.
        TokenValidation.DescribeForLog("garbage")
            .Should().Contain("unreadable").And.Contain("len=7").And.Contain("dots=0");
        TokenValidation.DescribeForLog(new string('x', 40) + "." + new string('y', 20))
            .Should().Contain("dots=1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_safe(string? token)
    {
        TokenValidation.DescribeForLog(token).Should().Be("token=(empty)");
    }
}
