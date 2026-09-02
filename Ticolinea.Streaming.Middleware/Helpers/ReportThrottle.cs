using System.Collections.Concurrent;

namespace ticolinea.stream.service.Helpers;

// Per-CLIENT activity-report throttle. Keeps same-channel heartbeats at one
// report per window, but lets a channel CHANGE report immediately — including
// flipping back to a channel watched seconds ago. (The original per
// (client,stream) key suppressed exactly that flip-back for up to 30s, so the
// panel looked stale whenever an operator tested a channel change live.)
public class ReportThrottle
{
    private readonly int _throttleSeconds;
    private readonly int _floorSeconds;
    private readonly ConcurrentDictionary<string, (int StreamId, long Time)> _lastBySubject = new();

    // floorSeconds: minimum spacing between ANY two reports for one subject,
    // stream change or not. Guards a MAC-less account with several devices,
    // where interleaved playlist reloads look like a change every request.
    public ReportThrottle(int throttleSeconds, int floorSeconds = 2)
    {
        _throttleSeconds = throttleSeconds;
        _floorSeconds = floorSeconds;
    }

    // deviceKey (MAC when known) separates boxes sharing one client, so two
    // devices heartbeating different channels do not defeat the throttle.
    public bool ShouldReport(int clientId, int streamId, long now, string? deviceKey = null)
    {
        var subject = string.IsNullOrEmpty(deviceKey) ? clientId.ToString() : $"{clientId}:{deviceKey}";
        if (_lastBySubject.TryGetValue(subject, out var last))
        {
            if ((now - last.Time) < _floorSeconds) return false;
            if (last.StreamId == streamId && (now - last.Time) < _throttleSeconds) return false;
        }

        _lastBySubject[subject] = (streamId, now);
        return true;
    }

    // Drops clients not seen for maxAgeSeconds; returns how many were evicted.
    public int Evict(long now, int maxAgeSeconds)
    {
        var evicted = 0;
        foreach (var kvp in _lastBySubject)
        {
            if ((now - kvp.Value.Time) > maxAgeSeconds && _lastBySubject.TryRemove(kvp.Key, out _))
                evicted++;
        }
        return evicted;
    }
}
