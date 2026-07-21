using System;
using FluentAssertions;
using ticolinea.stream.service.NodeConsole.Auth;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// The console is password-authenticated on a public port, so failed logins are
// counted and locked out. Time is passed in rather than read, so the 10-minute
// window is testable without waiting for it.
public class LoginThrottleTests
{
    private static readonly DateTime T0 = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
    private const string Key = "admin|203.0.113.7";

    [Fact]
    public void A_fresh_key_is_not_locked()
    {
        new LoginThrottle().IsLocked(Key, T0, out _).Should().BeFalse();
    }

    [Fact]
    public void Four_failures_do_not_lock()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 4; i++) t.RecordFailure(Key, T0);

        t.IsLocked(Key, T0, out _).Should().BeFalse();
    }

    [Fact]
    public void The_fifth_failure_locks()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure(Key, T0);

        t.IsLocked(Key, T0, out _).Should().BeTrue();
    }

    [Fact]
    public void RecordFailure_reports_the_failure_that_caused_the_lock()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 4; i++) t.RecordFailure(Key, T0).Should().BeFalse();

        // Only the transition returns true, so the caller logs the lockout once
        // rather than on every subsequent attempt.
        t.RecordFailure(Key, T0).Should().BeTrue();
        t.RecordFailure(Key, T0).Should().BeFalse();
    }

    [Fact]
    public void A_locked_key_reports_the_remaining_wait()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure(Key, T0);

        t.IsLocked(Key, T0.AddMinutes(4), out var retryAfter).Should().BeTrue();
        retryAfter.Should().BeCloseTo(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_lock_expires_after_ten_minutes()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure(Key, T0);

        t.IsLocked(Key, T0.AddMinutes(10).AddSeconds(1), out _).Should().BeFalse();
    }

    [Fact]
    public void Expiry_clears_the_counter_rather_than_leaving_it_primed()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure(Key, T0);

        var later = T0.AddMinutes(11);
        t.IsLocked(Key, later, out _);

        // One failure after expiry must not re-lock immediately.
        t.RecordFailure(Key, later).Should().BeFalse();
        t.IsLocked(Key, later, out _).Should().BeFalse();
    }

    [Fact]
    public void A_successful_login_clears_the_failures()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 4; i++) t.RecordFailure(Key, T0);

        t.RecordSuccess(Key);

        t.RecordFailure(Key, T0).Should().BeFalse();
        t.IsLocked(Key, T0, out _).Should().BeFalse();
    }

    [Fact]
    public void Keys_are_tracked_independently()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 5; i++) t.RecordFailure(Key, T0);

        // Locking one caller must never lock a different one — otherwise a
        // scanner hitting 'admin' would lock the real owner out of their node.
        t.IsLocked("admin|198.51.100.1", T0, out _).Should().BeFalse();
    }
}
