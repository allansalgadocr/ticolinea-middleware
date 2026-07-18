using System;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
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

    [Fact]
    public void No_restart_while_degraded_only_observation()
    {
        var state = new StreamProgress();
        var lastRestart = ExhaustBudget(state);
        Step(Obs(lastRestart.AddSeconds(31), playlistAge: 600), state);
        Step(Obs(lastRestart.AddSeconds(40), playlistAge: 600), state).Should().Be(WatchdogAction.MarkDegraded);

        // Hours of staleness later: still only CountStale, never Restart/MarkDegraded again
        // (the ERROR log happens once, on the MarkDegraded transition).
        for (int i = 1; i <= 5; i++)
        {
            Step(Obs(lastRestart.AddHours(i), playlistAge: 9999), state)
                .Should().Be(WatchdogAction.CountStale);
        }
        state.TotalRestarts.Should().Be(3);
        state.Degraded.Should().BeTrue();
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
