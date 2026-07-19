using System;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using ticolinea.stream.service.Services;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// The pure decision core of the output-progress watchdog. Time is simulated by
// passing NowUtc explicitly; every scenario drives Evaluate + Apply exactly like
// OutputWatchdogService does (evaluate, then apply the returned action).
public class WatchdogPolicyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WatchdogObservation Obs(
        DateTime now,
        double uptime = 600,
        bool hasPlaylist = true,
        long mediaSequence = 100,
        string lastSegment = "7_100.ts",
        int targetDuration = 4,
        double? playlistAge = 0)
        => new(now, uptime, hasPlaylist, mediaSequence, lastSegment, targetDuration, playlistAge);

    private static WatchdogAction Step(WatchdogObservation obs, StreamProgress state)
    {
        var action = WatchdogPolicy.Evaluate(obs, state);
        WatchdogPolicy.Apply(action, obs, state);
        return action;
    }

    // Drives the state to "stale, one check counted" so the NEXT stale check is
    // eligible for restart. Baseline at T0, first stale check at `firstStaleAt`.
    private static StreamProgress StaleOnce(DateTime firstStaleAt, out WatchdogObservation staleObs)
    {
        var state = new StreamProgress();
        Step(Obs(T0), state).Should().Be(WatchdogAction.Progress); // baseline
        staleObs = Obs(firstStaleAt, playlistAge: 60);
        Step(staleObs, state).Should().Be(WatchdogAction.CountStale);
        return state;
    }

    // ---------- baseline & progress ----------

    [Fact]
    public void First_observation_is_baseline_progress_never_stale()
    {
        var state = new StreamProgress();
        // Even a stale-looking playlist counts as Progress on first sight: there is
        // no prior sequence/segment to compare against.
        var action = Step(Obs(T0, playlistAge: 500), state);

        action.Should().Be(WatchdogAction.Progress);
        state.MediaSequence.Should().Be(100);
        state.LastSegment.Should().Be("7_100.ts");
        state.LastProgressUtc.Should().Be(T0);
        state.ConsecutiveStaleChecks.Should().Be(0);
    }

    [Fact]
    public void MediaSequence_change_is_progress_and_resets_stale_count()
    {
        var state = StaleOnce(T0.AddSeconds(60), out _);
        state.ConsecutiveStaleChecks.Should().Be(1);

        var action = Step(Obs(T0.AddSeconds(65), mediaSequence: 101, playlistAge: 0), state);

        action.Should().Be(WatchdogAction.Progress);
        state.ConsecutiveStaleChecks.Should().Be(0);
        state.MediaSequence.Should().Be(101);
    }

    [Fact]
    public void LastSegment_change_alone_is_progress()
    {
        var state = StaleOnce(T0.AddSeconds(60), out _);

        var action = Step(Obs(T0.AddSeconds(65), lastSegment: "7_101.ts", playlistAge: 0), state);

        action.Should().Be(WatchdogAction.Progress);
        state.LastSegment.Should().Be("7_101.ts");
    }

    // ---------- startup grace ----------

    [Theory]
    [InlineData(4, 15)]    // grace = max(4*4, 30) = 30s; uptime 15 < 30
    [InlineData(4, 29)]
    [InlineData(10, 39)]   // grace = max(4*10, 30) = 40s; uptime 39 < 40
    public void Inside_startup_grace_never_stale(int targetDuration, double uptime)
    {
        var state = new StreamProgress();
        Step(Obs(T0, targetDuration: targetDuration), state); // baseline

        var action = Step(
            Obs(T0.AddSeconds(120), uptime: uptime, targetDuration: targetDuration, playlistAge: 500),
            state);

        action.Should().Be(WatchdogAction.None);
        state.ConsecutiveStaleChecks.Should().Be(0);
    }

    [Fact]
    public void Unknown_uptime_is_treated_as_in_grace()
    {
        // uptime <= 0 = _lastProcessStart not tracked (e.g. orphaned ffmpeg from a
        // previous service boot). The watchdog must never act on those.
        var state = new StreamProgress();
        Step(Obs(T0), state);

        var action = Step(Obs(T0.AddMinutes(10), uptime: 0, playlistAge: 500), state);

        action.Should().Be(WatchdogAction.None);
    }

    [Fact]
    public void Past_grace_stale_playlist_counts_stale()
    {
        var state = new StreamProgress();
        Step(Obs(T0), state);

        // targetDuration 4 → stale threshold max(12, 20) = 20s; age 25 ≥ 20; uptime 31 > grace 30.
        var action = Step(Obs(T0.AddSeconds(31), uptime: 31, playlistAge: 25), state);

        action.Should().Be(WatchdogAction.CountStale);
        state.ConsecutiveStaleChecks.Should().Be(1);
    }

    [Fact]
    public void Fresh_playlist_age_below_threshold_is_not_stale()
    {
        var state = new StreamProgress();
        Step(Obs(T0), state);

        // Unchanged content but only 10s old and 10s since baseline: below max(12,20)=20.
        var action = Step(Obs(T0.AddSeconds(10), uptime: 600, playlistAge: 10), state);

        action.Should().Be(WatchdogAction.None);
        state.ConsecutiveStaleChecks.Should().Be(0);
    }

    [Fact]
    public void Missing_playlist_past_grace_counts_stale()
    {
        var state = new StreamProgress();
        Step(Obs(T0), state);

        var action = Step(
            Obs(T0.AddSeconds(60), hasPlaylist: false, mediaSequence: 0, lastSegment: "",
                targetDuration: 0, playlistAge: null),
            state);

        action.Should().Be(WatchdogAction.CountStale);
    }

    [Fact]
    public void Unchanged_content_with_fresh_mtime_goes_stale_via_no_change_clock()
    {
        // Wedge mode where ffmpeg rewrites the playlist (fresh mtime) without ever
        // adding segments: the "no change since last progress" clause catches it.
        var state = new StreamProgress();
        Step(Obs(T0), state);

        var action = Step(Obs(T0.AddSeconds(60), playlistAge: 1), state);

        action.Should().Be(WatchdogAction.CountStale);
    }

    // ---------- two-consecutive rule & restart ----------

    [Fact]
    public void First_stale_check_only_counts_second_restarts()
    {
        var state = StaleOnce(T0.AddSeconds(60), out _);

        var action = Step(Obs(T0.AddSeconds(65), playlistAge: 65), state);

        action.Should().Be(WatchdogAction.Restart);
        state.TotalRestarts.Should().Be(1);
        state.WatchdogRestartsInWindow.Should().Be(1);
        state.LastRestartUtc.Should().Be(T0.AddSeconds(65));
        state.ConsecutiveStaleChecks.Should().Be(0);
    }

    [Fact]
    public void Progress_between_stale_checks_breaks_the_consecutive_rule()
    {
        var state = StaleOnce(T0.AddSeconds(60), out _);
        Step(Obs(T0.AddSeconds(65), mediaSequence: 101, playlistAge: 0), state)
            .Should().Be(WatchdogAction.Progress);

        // Stale again later (same sequence as the last progress): must count from 1, not restart.
        Step(Obs(T0.AddSeconds(120), mediaSequence: 101, playlistAge: 30), state)
            .Should().Be(WatchdogAction.CountStale);
    }

    // ---------- cooldown ----------

    [Fact]
    public void No_second_restart_inside_30s_cooldown()
    {
        var state = StaleOnce(T0.AddSeconds(60), out _);
        Step(Obs(T0.AddSeconds(65), playlistAge: 65), state).Should().Be(WatchdogAction.Restart);

        // Still stale 2 checks later, but only 15s after the restart → cooldown holds.
        Step(Obs(T0.AddSeconds(70), playlistAge: 70), state).Should().Be(WatchdogAction.CountStale);
        Step(Obs(T0.AddSeconds(80), playlistAge: 80), state).Should().Be(WatchdogAction.CountStale);
        state.TotalRestarts.Should().Be(1);
    }

    [Fact]
    public void Restart_allowed_again_after_cooldown_expires()
    {
        var state = StaleOnce(T0.AddSeconds(60), out _);
        Step(Obs(T0.AddSeconds(65), playlistAge: 65), state).Should().Be(WatchdogAction.Restart);
        Step(Obs(T0.AddSeconds(70), playlistAge: 70), state).Should().Be(WatchdogAction.CountStale);

        // 35s after the restart, stale count already ≥ 2 → restart again.
        Step(Obs(T0.AddSeconds(100), playlistAge: 100), state).Should().Be(WatchdogAction.Restart);
        state.TotalRestarts.Should().Be(2);
    }

    // ---------- budget & degradation ----------

    // Runs the wedged-stream loop until three restarts have been spent within the
    // 10-min window, returning the time of the last restart.
    private static DateTime ExhaustBudget(StreamProgress state)
    {
        Step(Obs(T0), state); // baseline
        var t = T0;
        for (int restarts = 0; restarts < 3;)
        {
            t = t.AddSeconds(20);
            var action = Step(Obs(t, playlistAge: 600), state);
            if (action == WatchdogAction.Restart) restarts++;
            action.Should().BeOneOf(WatchdogAction.CountStale, WatchdogAction.Restart);
        }
        state.WatchdogRestartsInWindow.Should().Be(3);
        return t;
    }

    [Fact]
    public void Fourth_restart_within_window_marks_degraded_instead()
    {
        var state = new StreamProgress();
        var lastRestart = ExhaustBudget(state);

        // Still wedged: next eligible restart (past cooldown, 2 stale checks) must
        // degrade, not restart.
        Step(Obs(lastRestart.AddSeconds(31), playlistAge: 600), state)
            .Should().Be(WatchdogAction.CountStale);
        var action = Step(Obs(lastRestart.AddSeconds(40), playlistAge: 600), state);

        action.Should().Be(WatchdogAction.MarkDegraded);
        state.Degraded.Should().BeTrue();
        state.TotalRestarts.Should().Be(3);
    }

    // Drives a wedged stream through budget exhaustion into Degraded, returning
    // the state and the time of the last budgeted restart.
    private static (StreamProgress state, DateTime lastRestart) Degrade()
    {
        var state = new StreamProgress();
        var lastRestart = ExhaustBudget(state);
        Step(Obs(lastRestart.AddSeconds(31), playlistAge: 600), state);
        Step(Obs(lastRestart.AddSeconds(40), playlistAge: 600), state).Should().Be(WatchdogAction.MarkDegraded);
        state.Degraded.Should().BeTrue();
        return (state, lastRestart);
    }

    [Fact]
    public void Degraded_wedged_channel_gets_slow_probe_restart_every_10_minutes()
    {
        // Slow-retry contract: a permanently wedged channel must NOT stall forever.
        // While Degraded, one probe Restart is granted per DegradedRetryInterval
        // (10 min) since the last restart — escalation stays monotonic
        // (normal: up to 3/10min → degraded: 1/10min).
        var (state, lastRestart) = Degrade();

        // Before the interval elapses: observation only.
        Step(Obs(lastRestart.AddMinutes(9), playlistAge: 600), state)
            .Should().Be(WatchdogAction.CountStale);

        // At 10 min since the last restart: probe.
        var probe1 = lastRestart.AddMinutes(10);
        Step(Obs(probe1, playlistAge: 600), state).Should().Be(WatchdogAction.Restart);
        state.Degraded.Should().BeTrue();          // probe does NOT clear Degraded
        state.TotalRestarts.Should().Be(4);
        state.LastRestartUtc.Should().Be(probe1);

        // Still wedged after the probe: stale counting resumes, no early restart...
        Step(Obs(probe1.AddSeconds(20), playlistAge: 600), state).Should().Be(WatchdogAction.CountStale);
        Step(Obs(probe1.AddSeconds(40), playlistAge: 600), state).Should().Be(WatchdogAction.CountStale);

        // ...until the next 10-min interval grants the next probe.
        var probe2 = probe1.AddMinutes(10);
        Step(Obs(probe2, playlistAge: 600), state).Should().Be(WatchdogAction.Restart);
        state.Degraded.Should().BeTrue();
        state.TotalRestarts.Should().Be(5);
    }

    [Fact]
    public void Degraded_stale_before_10_minutes_only_counts_stale()
    {
        var (state, lastRestart) = Degrade();

        var action = Step(Obs(lastRestart.AddMinutes(9), playlistAge: 600), state);

        action.Should().Be(WatchdogAction.CountStale);
        state.TotalRestarts.Should().Be(3);
        state.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Degraded_probe_updates_restart_clock_but_not_window_budget()
    {
        // The probe deliberately does NOT consume window budget: Degraded already
        // rate-limits it harder (1/10min) than the window would, and ClearDegraded
        // resets the window anyway.
        var (state, lastRestart) = Degrade();
        var windowStartBefore = state.WindowStartUtc;
        state.WatchdogRestartsInWindow.Should().Be(3);

        var probeAt = lastRestart.AddMinutes(10);
        Step(Obs(probeAt, playlistAge: 600), state).Should().Be(WatchdogAction.Restart);

        state.WatchdogRestartsInWindow.Should().Be(3);      // unchanged
        state.WindowStartUtc.Should().Be(windowStartBefore); // unchanged
        state.LastRestartUtc.Should().Be(probeAt);
        state.Degraded.Should().BeTrue();
        state.ConsecutiveStaleChecks.Should().Be(0);
    }

    [Fact]
    public void Degraded_probe_then_two_advances_clears_degraded()
    {
        // Existing recovery contract intact: only two progress advances clear
        // Degraded — including when the progress comes from a probe restart.
        var (state, lastRestart) = Degrade();
        var probeAt = lastRestart.AddMinutes(10);
        Step(Obs(probeAt, playlistAge: 600), state).Should().Be(WatchdogAction.Restart);

        var t1 = probeAt.AddSeconds(30);
        Step(Obs(t1, mediaSequence: 200, playlistAge: 0), state).Should().Be(WatchdogAction.Progress);
        state.Degraded.Should().BeTrue();
        state.ProgressAdvancesWhileDegraded.Should().Be(1);

        var t2 = t1.AddSeconds(5);
        Step(Obs(t2, mediaSequence: 201, playlistAge: 0), state).Should().Be(WatchdogAction.ClearDegraded);
        state.Degraded.Should().BeFalse();
        state.WatchdogRestartsInWindow.Should().Be(0);
        state.WindowStartUtc.Should().Be(t2);
    }

    [Fact]
    public void Two_progress_advances_clear_degraded_and_reset_budget()
    {
        var state = new StreamProgress();
        var lastRestart = ExhaustBudget(state);
        Step(Obs(lastRestart.AddSeconds(31), playlistAge: 600), state);
        Step(Obs(lastRestart.AddSeconds(40), playlistAge: 600), state).Should().Be(WatchdogAction.MarkDegraded);

        // First advance: still degraded.
        var t1 = lastRestart.AddSeconds(60);
        Step(Obs(t1, mediaSequence: 200, playlistAge: 0), state).Should().Be(WatchdogAction.Progress);
        state.Degraded.Should().BeTrue();
        state.ProgressAdvancesWhileDegraded.Should().Be(1);

        // Second advance: degraded cleared, budget window reset.
        var t2 = t1.AddSeconds(5);
        Step(Obs(t2, mediaSequence: 201, playlistAge: 0), state).Should().Be(WatchdogAction.ClearDegraded);
        state.Degraded.Should().BeFalse();
        state.WatchdogRestartsInWindow.Should().Be(0);
        state.WindowStartUtc.Should().Be(t2);
        state.ProgressAdvancesWhileDegraded.Should().Be(0);
    }

    [Fact]
    public void Single_advance_does_not_clear_degraded_and_stale_resumes_counting()
    {
        var state = new StreamProgress();
        var lastRestart = ExhaustBudget(state);
        Step(Obs(lastRestart.AddSeconds(31), playlistAge: 600), state);
        Step(Obs(lastRestart.AddSeconds(40), playlistAge: 600), state).Should().Be(WatchdogAction.MarkDegraded);

        Step(Obs(lastRestart.AddSeconds(60), mediaSequence: 200, playlistAge: 0), state)
            .Should().Be(WatchdogAction.Progress);
        state.Degraded.Should().BeTrue();

        // Wedges again before the second advance (sequence frozen at 200): stays
        // degraded, observe-only.
        Step(Obs(lastRestart.AddSeconds(120), mediaSequence: 200, playlistAge: 60), state)
            .Should().Be(WatchdogAction.CountStale);
        state.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Budget_window_slides_after_10_minutes_restarts_resume()
    {
        var state = new StreamProgress();
        var lastRestart = ExhaustBudget(state);
        state.WatchdogRestartsInWindow.Should().Be(3);

        // 10+ minutes after the window started: the sliding window has expired, so a
        // new eligible restart is granted (not MarkDegraded).
        var late = state.WindowStartUtc.AddMinutes(10).AddSeconds(1);
        // Ensure cooldown from lastRestart also passed.
        (late - lastRestart).TotalSeconds.Should().BeGreaterThan(WatchdogPolicy.RestartCooldownSeconds);

        Step(Obs(late, playlistAge: 9999), state).Should().Be(WatchdogAction.CountStale);
        var action = Step(Obs(late.AddSeconds(5), playlistAge: 9999), state);

        action.Should().Be(WatchdogAction.Restart);
        state.WatchdogRestartsInWindow.Should().Be(1);          // fresh window
        state.WindowStartUtc.Should().Be(late.AddSeconds(5));
        state.TotalRestarts.Should().Be(4);
    }

    // ---------- watchdog kill marks (StreamingService) ----------
    // Fix: an intentional watchdog kill must not feed the app circuit breaker.
    // Only the pure mark→consume semantics are testable here (explicit clock,
    // unique stream ids against the static registry); the _failureTracker skip
    // itself lives inside LanzarProcesoFfmpeg's exit handling (private static
    // state driven by a real ffmpeg exit) and is exercised only via integration.

    [Fact]
    public void Watchdog_kill_mark_is_consumed_exactly_once()
    {
        StreamingService.MarkWatchdogKill(990101, T0);

        StreamingService.TryConsumeWatchdogKillMark(990101, T0.AddSeconds(5)).Should().BeTrue();
        // Consumed: a second exit (real failure) within the TTL must count.
        StreamingService.TryConsumeWatchdogKillMark(990101, T0.AddSeconds(6)).Should().BeFalse();
    }

    [Fact]
    public void Stale_watchdog_kill_mark_is_ignored_and_removed()
    {
        StreamingService.MarkWatchdogKill(990102, T0);

        // 61s > 60s TTL: a leaked mark cannot mask a real future failure.
        StreamingService.TryConsumeWatchdogKillMark(990102, T0.AddSeconds(61)).Should().BeFalse();
        // And it was removed, not merely ignored.
        StreamingService.TryConsumeWatchdogKillMark(990102, T0.AddSeconds(62)).Should().BeFalse();
    }

    [Fact]
    public void Consume_without_mark_is_false()
    {
        StreamingService.TryConsumeWatchdogKillMark(990103, T0).Should().BeFalse();
    }

    [Fact]
    public void Remarking_refreshes_the_mark_timestamp()
    {
        StreamingService.MarkWatchdogKill(990104, T0);
        StreamingService.MarkWatchdogKill(990104, T0.AddSeconds(50));

        // 100s after the first mark but only 50s after the refresh: still fresh.
        StreamingService.TryConsumeWatchdogKillMark(990104, T0.AddSeconds(100)).Should().BeTrue();
    }

    // Fix (round 2): the mark is a COUNT — MatarProcesoParaWatchdog may kill
    // several PIDs (pgrep) in one pass, one mark per delivered kill; a mark whose
    // kill was never delivered is retracted. Invariant: live marks == kills
    // actually delivered.

    [Fact]
    public void Two_kills_leave_two_marks_each_consumed_once()
    {
        StreamingService.MarkWatchdogKill(990105, T0);
        StreamingService.MarkWatchdogKill(990105, T0.AddSeconds(1));

        StreamingService.TryConsumeWatchdogKillMark(990105, T0.AddSeconds(5)).Should().BeTrue();
        StreamingService.TryConsumeWatchdogKillMark(990105, T0.AddSeconds(6)).Should().BeTrue();
        // Third exit within the TTL is a REAL failure and must count.
        StreamingService.TryConsumeWatchdogKillMark(990105, T0.AddSeconds(7)).Should().BeFalse();
    }

    [Fact]
    public void Retract_cancels_an_undelivered_kill_mark()
    {
        StreamingService.MarkWatchdogKill(990106, T0);
        StreamingService.RetractWatchdogKillMark(990106, T0.AddSeconds(1));

        // The kill never happened: a real failure right after must count.
        StreamingService.TryConsumeWatchdogKillMark(990106, T0.AddSeconds(2)).Should().BeFalse();
    }

    [Fact]
    public void Retract_removes_only_one_mark_of_several()
    {
        StreamingService.MarkWatchdogKill(990107, T0);
        StreamingService.MarkWatchdogKill(990107, T0.AddSeconds(1));
        StreamingService.RetractWatchdogKillMark(990107, T0.AddSeconds(2));

        StreamingService.TryConsumeWatchdogKillMark(990107, T0.AddSeconds(3)).Should().BeTrue();
        StreamingService.TryConsumeWatchdogKillMark(990107, T0.AddSeconds(4)).Should().BeFalse();
    }

    [Fact]
    public void Retract_without_mark_is_a_noop()
    {
        StreamingService.RetractWatchdogKillMark(990108, T0);

        StreamingService.TryConsumeWatchdogKillMark(990108, T0.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void Expired_entry_is_dropped_wholly_never_consumed_mark_by_mark()
    {
        StreamingService.MarkWatchdogKill(990109, T0);
        StreamingService.MarkWatchdogKill(990109, T0.AddSeconds(1));

        // 61s past the LAST mark: both marks expired — the entry is removed
        // whole, not decremented, so neither consume succeeds.
        StreamingService.TryConsumeWatchdogKillMark(990109, T0.AddSeconds(62)).Should().BeFalse();
        StreamingService.TryConsumeWatchdogKillMark(990109, T0.AddSeconds(63)).Should().BeFalse();
    }

    [Fact]
    public void Marking_over_an_expired_entry_does_not_revive_it()
    {
        StreamingService.MarkWatchdogKill(990110, T0);
        // 120s later the first mark is long expired; a new kill starts from 1.
        StreamingService.MarkWatchdogKill(990110, T0.AddSeconds(120));

        StreamingService.TryConsumeWatchdogKillMark(990110, T0.AddSeconds(125)).Should().BeTrue();
        StreamingService.TryConsumeWatchdogKillMark(990110, T0.AddSeconds(126)).Should().BeFalse();
    }

    // ---------- purity of Evaluate ----------

    [Fact]
    public void Evaluate_does_not_mutate_state()
    {
        // The double-read guard in OutputWatchdogService depends on this: it calls
        // Evaluate twice (before and after the 2s re-read) and only Apply once.
        var state = StaleOnce(T0.AddSeconds(60), out _);
        var before = (state.MediaSequence, state.LastSegment, state.LastProgressUtc,
                      state.LastRestartUtc, state.ConsecutiveStaleChecks,
                      state.WatchdogRestartsInWindow, state.WindowStartUtc,
                      state.Degraded, state.ProgressAdvancesWhileDegraded, state.TotalRestarts);

        var obs = Obs(T0.AddSeconds(65), playlistAge: 65);
        WatchdogPolicy.Evaluate(obs, state).Should().Be(WatchdogAction.Restart);
        WatchdogPolicy.Evaluate(obs, state).Should().Be(WatchdogAction.Restart); // idempotent

        var after = (state.MediaSequence, state.LastSegment, state.LastProgressUtc,
                     state.LastRestartUtc, state.ConsecutiveStaleChecks,
                     state.WatchdogRestartsInWindow, state.WindowStartUtc,
                     state.Degraded, state.ProgressAdvancesWhileDegraded, state.TotalRestarts);
        after.Should().Be(before);
    }

    [Fact]
    public void Thresholds_scale_with_target_duration()
    {
        WatchdogPolicy.GraceSeconds(0).Should().Be(30);
        WatchdogPolicy.GraceSeconds(4).Should().Be(30);   // 16 < 30
        WatchdogPolicy.GraceSeconds(10).Should().Be(40);  // 4×10
        WatchdogPolicy.StaleThresholdSeconds(0).Should().Be(20);
        WatchdogPolicy.StaleThresholdSeconds(6).Should().Be(20);  // 18 < 20
        WatchdogPolicy.StaleThresholdSeconds(10).Should().Be(30); // 3×10
    }
}
