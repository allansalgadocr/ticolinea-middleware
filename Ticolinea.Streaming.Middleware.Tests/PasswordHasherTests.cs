using FluentAssertions;
using ticolinea.stream.service.NodeConsole.Auth;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// Console admin passwords never leave the node, so the hash format is ours to
// define — but it must be salted per-password and verifiable without a DB.
public class PasswordHasherTests
{
    [Fact]
    public void Hash_of_the_same_password_differs_every_time()
    {
        var a = PasswordHasher.Hash("correct horse battery");
        var b = PasswordHasher.Hash("correct horse battery");

        a.Should().NotBe(b, "each hash must carry its own random salt");
    }

    [Fact]
    public void Verify_accepts_the_original_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery");

        PasswordHasher.Verify("correct horse battery", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_a_wrong_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery");

        PasswordHasher.Verify("Correct horse battery", hash).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2$1000$@@@$aGFzaA==")]
    public void Verify_rejects_a_malformed_hash_instead_of_throwing(string stored)
    {
        PasswordHasher.Verify("anything", stored).Should().BeFalse();
    }

    [Fact]
    public void Hash_is_self_describing_so_iterations_can_be_raised_later()
    {
        PasswordHasher.Hash("x").Should().StartWith("pbkdf2$");
    }
}
