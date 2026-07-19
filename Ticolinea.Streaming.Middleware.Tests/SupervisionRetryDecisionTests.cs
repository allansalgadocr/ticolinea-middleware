using FluentAssertions;
using ticolinea.stream.service.Services;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// StreamingService.DecideRetry — the pure decision core of SupervisarStream's
// retry loop ("given exitCode != 0: watchdogKill, runtimeSeconds,
// currentRetryCount → new retryCount / delay class"). Extracted so the fixes
// are testable without a real ffmpeg:
//  a) a watchdog kill never consumes the supervisor retry budget,
//  b) a failure after a long stable run counts as a FIRST failure — the budget
//     is no longer a lifetime death sentence, and
//  c) the stable-runtime reset applies to the watchdog branch too: a kill after
//     hours of healthy running clears historical counts instead of preserving
//     them for the next organic failure to trip the stop.
// runtimeSeconds is measured from the REAL ffmpeg start (StartedCommandEvent),
// never from before the queue/semaphore waits.
public class SupervisionRetryDecisionTests
{
    // ---------- watchdog kill: corrective relaunch, budget untouched ----------

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)] // one short of the cap: a watchdog kill must NOT stop supervision
    public void Watchdog_kill_after_unstable_run_preserves_the_count(int currentRetryCount)
    {
        var decision = StreamingService.DecideRetry(
            watchdogKill: true, runtimeSeconds: 30, currentRetryCount);

        decision.Kind.Should().Be(StreamingService.RetryKind.RelaunchAfterWatchdogKill);
        decision.NewRetryCount.Should().Be(currentRetryCount);
        decision.BaseDelaySeconds.Should().Be(StreamingService.WatchdogRelaunchDelaySeconds);
    }

    [Fact]
    public void Watchdog_kill_after_stable_run_clears_the_historical_count()
    {
        // Killed after hours of healthy output: preserving a historical 9 would
        // let the next rapid organic failure hit 10 and stop supervision.
        var decision = StreamingService.DecideRetry(
            watchdogKill: true, runtimeSeconds: 3600, currentRetryCount: 9);

        decision.Kind.Should().Be(StreamingService.RetryKind.RelaunchAfterWatchdogKill);
        decision.NewRetryCount.Should().Be(0);
        decision.BaseDelaySeconds.Should().Be(StreamingService.WatchdogRelaunchDelaySeconds);
    }

    [Fact]
    public void Watchdog_kill_uses_short_corrective_delay_not_backoff()
    {
        // Even with a high retry count the relaunch is fast: the 12s minimum
        // restart interval inside LanzarProcesoFfmpeg is the storm protection.
        var decision = StreamingService.DecideRetry(true, 30, 8);

        decision.BaseDelaySeconds.Should().Be(2);
        decision.Kind.Should().Be(StreamingService.RetryKind.RelaunchAfterWatchdogKill);
    }

    // ---------- genuine failures: increment + exponential backoff ----------

    [Fact]
    public void First_rapid_failure_gets_base_delay()
    {
        var decision = StreamingService.DecideRetry(false, 30, 0);

        decision.Kind.Should().Be(StreamingService.RetryKind.BackoffAndRetry);
        decision.NewRetryCount.Should().Be(1);
        decision.BaseDelaySeconds.Should().Be(StreamingService.RetryBaseDelaySeconds); // 5s
    }

    [Fact]
    public void Rapid_failure_increments_and_doubles_the_delay()
    {
        // 3rd failure: 5 * 2^2 = 20s (jitter is applied by the caller).
        var decision = StreamingService.DecideRetry(false, 30, 2);

        decision.Kind.Should().Be(StreamingService.RetryKind.BackoffAndRetry);
        decision.NewRetryCount.Should().Be(3);
        decision.BaseDelaySeconds.Should().Be(20);
    }

    [Fact]
    public void Backoff_delay_is_capped_at_the_maximum()
    {
        // 8th failure: 5 * 2^min(7,6) = 320 → capped to 300.
        var decision = StreamingService.DecideRetry(false, 30, 7);

        decision.NewRetryCount.Should().Be(8);
        decision.BaseDelaySeconds.Should().Be(StreamingService.RetryMaxDelaySeconds); // 300s
    }

    // ---------- stable-runtime reset: the budget is not a lifetime sentence ----------

    [Fact]
    public void Failure_after_a_stable_run_counts_as_the_first_failure()
    {
        // Ran healthy ≥ 5 min before dying: reset BEFORE incrementing.
        var decision = StreamingService.DecideRetry(
            false, StreamingService.StableRuntimeResetSeconds, 7);

        decision.Kind.Should().Be(StreamingService.RetryKind.BackoffAndRetry);
        decision.NewRetryCount.Should().Be(1);
        decision.BaseDelaySeconds.Should().Be(StreamingService.RetryBaseDelaySeconds);
    }

    [Fact]
    public void Failure_just_under_the_stable_threshold_still_increments()
    {
        var decision = StreamingService.DecideRetry(false, 299.9, 7);

        decision.NewRetryCount.Should().Be(8);
    }

    [Fact]
    public void Stable_run_prevents_the_permanent_stop()
    {
        // At count 9 a rapid failure would be the 10th and stop supervision —
        // but this failure came after hours of healthy runtime: first failure.
        var decision = StreamingService.DecideRetry(false, 3600, 9);

        decision.Kind.Should().Be(StreamingService.RetryKind.BackoffAndRetry);
        decision.NewRetryCount.Should().Be(1);
    }

    // ---------- final safety stop: 10 consecutive rapid genuine failures ----------

    [Fact]
    public void Tenth_consecutive_rapid_failure_stops_supervision()
    {
        var decision = StreamingService.DecideRetry(false, 30, 9);

        decision.Kind.Should().Be(StreamingService.RetryKind.StopSupervision);
        decision.NewRetryCount.Should().Be(StreamingService.MaxSupervisionRetries); // 10
    }
}
