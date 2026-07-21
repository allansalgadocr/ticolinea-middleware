using FluentAssertions;
using ticolinea.stream.service.NodeConsole;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// NodeConsole:SeedPassword arrives from a rendered config file. Bootstrap
// validates it, but the file can also be hand-edited on the box, so the node
// makes its own decision rather than trusting whatever it is handed.
public class SeedPasswordTests
{
    [Fact]
    public void A_configured_password_is_used_as_is()
    {
        var resolved = ConsoleSchema.ResolveSeedPassword("unaClaveLargaSegura", out var generated);

        resolved.Should().Be("unaClaveLargaSegura");
        generated.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_configured_password_falls_back_to_a_generated_one(string? configured)
    {
        var resolved = ConsoleSchema.ResolveSeedPassword(configured, out var generated);

        generated.Should().BeTrue();
        resolved.Length.Should().BeGreaterThanOrEqualTo(ConsoleValidation.MinPassword);
    }

    [Fact]
    public void A_too_short_configured_password_is_refused_rather_than_seeded()
    {
        // Silently accepting it would put a weak credential on an
        // internet-facing console, which is worse than an unknown strong one.
        var resolved = ConsoleSchema.ResolveSeedPassword("corta", out var generated);

        generated.Should().BeTrue();
        resolved.Should().NotBe("corta");
        resolved.Length.Should().BeGreaterThanOrEqualTo(ConsoleValidation.MinPassword);
    }

    [Fact]
    public void Generated_passwords_are_not_predictable()
    {
        ConsoleSchema.ResolveSeedPassword(null, out _)
            .Should().NotBe(ConsoleSchema.ResolveSeedPassword(null, out _));
    }
}
