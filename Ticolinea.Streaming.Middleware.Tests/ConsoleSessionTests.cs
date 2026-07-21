using System;
using FluentAssertions;
using ticolinea.stream.service.NodeConsole.Auth;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// Sessions are opaque random tokens, stored only as a hash. The raw token lives
// in the client's cookie and nowhere else, so a leaked DB dump cannot be replayed.
public class SessionTokenTests
{
    [Fact]
    public void Each_issued_token_is_unique()
    {
        SessionToken.New().Raw.Should().NotBe(SessionToken.New().Raw);
    }

    [Fact]
    public void Raw_token_is_long_enough_to_resist_guessing()
    {
        // 32 random bytes, base64url — no padding, no '+' or '/' to break cookies.
        var raw = SessionToken.New().Raw;

        raw.Length.Should().BeGreaterThanOrEqualTo(43);
        raw.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void Stored_hash_never_equals_the_raw_token()
    {
        var token = SessionToken.New();

        token.Hash.Should().NotBe(token.Raw);
    }

    [Fact]
    public void Hashing_the_raw_token_reproduces_the_stored_hash()
    {
        var token = SessionToken.New();

        SessionToken.HashFor(token.Raw).Should().Be(token.Hash);
    }

    [Fact]
    public void Different_tokens_hash_differently()
    {
        SessionToken.HashFor("aaa").Should().NotBe(SessionToken.HashFor("bbb"));
    }
}

// Whether a presented session may act is a pure decision over the stored row.
// Keeping it out of the DB layer is what makes revocation semantics testable.
public class SessionPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_live_session_for_an_enabled_user_is_valid()
    {
        SessionPolicy.IsValid(expiresUtc: Now.AddHours(1), userEnabled: true, now: Now).Should().BeTrue();
    }

    [Fact]
    public void An_expired_session_is_rejected()
    {
        SessionPolicy.IsValid(expiresUtc: Now.AddSeconds(-1), userEnabled: true, now: Now).Should().BeFalse();
    }

    [Fact]
    public void Disabling_a_user_invalidates_their_live_session_immediately()
    {
        SessionPolicy.IsValid(expiresUtc: Now.AddHours(1), userEnabled: false, now: Now).Should().BeFalse();
    }

    [Fact]
    public void A_session_expiring_exactly_now_is_rejected()
    {
        SessionPolicy.IsValid(expiresUtc: Now, userEnabled: true, now: Now).Should().BeFalse();
    }
}
