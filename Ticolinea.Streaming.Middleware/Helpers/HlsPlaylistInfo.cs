namespace ticolinea.stream.service.Helpers;

// Pure HLS media-playlist parser. No I/O — the caller reads the .m3u8 file
// (AdminController) and hands the text in. Robust by design: the file is being
// rewritten continuously by FFmpeg, so missing tags fall back to defaults
// (0 / empty) instead of failing; only "this is not a media playlist at all"
// returns null. Mirrors the pure-helper pattern of PackageSyncPlan /
// FfmpegInputPolicy so it stays unit-testable without a filesystem.
public sealed class HlsPlaylistInfo
{
    public long MediaSequence { get; init; }        // #EXT-X-MEDIA-SEQUENCE (0 if absent)
    public int TargetDuration { get; init; }        // #EXT-X-TARGETDURATION (0 if absent)
    public string LastSegment { get; init; } = "";  // last segment URI line ("" if none yet)

    // Returns null when the text is not an HLS media playlist: empty/whitespace,
    // missing the #EXTM3U header, or a master playlist (#EXT-X-STREAM-INF).
    public static HlsPlaylistInfo? Parse(string playlistText)
    {
        if (string.IsNullOrWhiteSpace(playlistText))
            return null;

        // Tolerate CRLF and blank lines: split on \n, trim each line.
        var lines = playlistText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal))
            return null;

        long mediaSequence = 0;
        int targetDuration = 0;
        string lastSegment = "";

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                return null; // master playlist, not a media playlist

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out var seq))
                    mediaSequence = seq;
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (int.TryParse(line["#EXT-X-TARGETDURATION:".Length..].Trim(), out var dur))
                    targetDuration = dur;
            }
            else if (!line.StartsWith("#", StringComparison.Ordinal))
            {
                // URI line — the last one wins.
                lastSegment = line;
            }
        }

        return new HlsPlaylistInfo
        {
            MediaSequence = mediaSequence,
            TargetDuration = targetDuration,
            LastSegment = lastSegment
        };
    }
}
