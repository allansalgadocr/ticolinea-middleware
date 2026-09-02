using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// The activity-report throttle exists to keep heartbeats at one POST per 30s —
// it must NEVER delay a channel change. The original per-(client,stream) key
// suppressed returning to a recently-watched channel for up to 30s, which is
// exactly the flip-back an operator uses to test the dashboard.
public class ReportThrottleTests
{
    [Fact]
    public void First_report_for_a_client_is_allowed()
    {
        var t = new ReportThrottle(throttleSeconds: 30);
        t.ShouldReport(clientId: 62, streamId: 100, now: 1000).Should().BeTrue();
    }

    [Fact]
    public void Same_channel_heartbeat_is_throttled_within_the_window()
    {
        var t = new ReportThrottle(30);
        t.ShouldReport(62, 100, 1000).Should().BeTrue();
        t.ShouldReport(62, 100, 1010).Should().BeFalse();
        t.ShouldReport(62, 100, 1030).Should().BeTrue(); // window elapsed
    }

    [Fact]
    public void A_channel_change_reports_immediately()
    {
        var t = new ReportThrottle(30);
        t.ShouldReport(62, 100, 1000).Should().BeTrue();
        t.ShouldReport(62, 200, 1005).Should().BeTrue(); // change, inside window
    }

    [Fact]
    public void Flipping_back_to_the_previous_channel_reports_immediately()
    {
        // The bug: A -> B -> A within 30s. The return to A must report.
        var t = new ReportThrottle(30);
        t.ShouldReport(62, 100, 1000).Should().BeTrue();  // A
        t.ShouldReport(62, 200, 1005).Should().BeTrue();  // B
        t.ShouldReport(62, 100, 1010).Should().BeTrue();  // back to A — was suppressed before
    }

    [Fact]
    public void Clients_throttle_independently()
    {
        var t = new ReportThrottle(30);
        t.ShouldReport(62, 100, 1000).Should().BeTrue();
        t.ShouldReport(63, 100, 1001).Should().BeTrue();
        t.ShouldReport(62, 100, 1002).Should().BeFalse();
        t.ShouldReport(63, 100, 1003).Should().BeFalse();
    }

    [Fact]
    public void Two_devices_on_one_client_throttle_independently()
    {
        // A family with two boxes on the same client: each device heartbeats
        // its own channel without the interleaving defeating the throttle.
        var t = new ReportThrottle(30);
        t.ShouldReport(62, 100, 1000, deviceKey: "AA:BB").Should().BeTrue();
        t.ShouldReport(62, 200, 1002, deviceKey: "CC:DD").Should().BeTrue();
        t.ShouldReport(62, 100, 1006, deviceKey: "AA:BB").Should().BeFalse(); // heartbeat, throttled
        t.ShouldReport(62, 200, 1008, deviceKey: "CC:DD").Should().BeFalse(); // heartbeat, throttled
        t.ShouldReport(62, 300, 1010, deviceKey: "AA:BB").Should().BeTrue();  // real change
    }

    [Fact]
    public void Eviction_drops_only_entries_older_than_the_max_age()
    {
        var t = new ReportThrottle(30);
        t.ShouldReport(62, 100, 1000).Should().BeTrue();
        t.ShouldReport(63, 100, 1350).Should().BeTrue(); // fresh — must survive
        t.Evict(now: 1400, maxAgeSeconds: 300).Should().Be(1);
        t.Evict(now: 1400, maxAgeSeconds: 300).Should().Be(0);
        // the surviving fresh entry still throttles its own heartbeat
        t.ShouldReport(63, 100, 1360).Should().BeFalse();
    }

    [Fact]
    public void Rapid_stream_flapping_on_one_subject_hits_the_floor()
    {
        // A MAC-less account with two boxes alternates streams on every HLS
        // reload; without a floor every request would POST. Changes inside the
        // floor window are suppressed; a change after it reports.
        var t = new ReportThrottle(30, floorSeconds: 2);
        t.ShouldReport(62, 100, 1000).Should().BeTrue();
        t.ShouldReport(62, 200, 1001, deviceKey: null).Should().BeFalse(); // change, but < floor
        t.ShouldReport(62, 200, 1003).Should().BeTrue();                    // change, past floor
    }
}
