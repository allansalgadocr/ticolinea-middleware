using System.Diagnostics;

namespace ticolinea.stream.service.Helpers;

// The static "no channel" clip behind placeholder playlist entries: 4 seconds,
// generated ONCE with the node's own ffmpeg the first time a placeholder is
// played, then served as a plain file. No running process, no DB row.
public static class PlaceholderSlate
{
    // VOD-style playlist: plays the clip once and stops. Relative segment URI
    // resolves under /Live/.
    public const string PlaylistBody =
        "#EXTM3U\n" +
        "#EXT-X-VERSION:3\n" +
        "#EXT-X-TARGETDURATION:5\n" +
        "#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-PLAYLIST-TYPE:VOD\n" +
        "#EXTINF:4.000000,\n" +
        "Placeholder0.ts\n" +
        "#EXT-X-ENDLIST\n";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string SegmentPath =>
        Path.Combine(Constantes.Global.STREAMS_FOLDER, "placeholder_slate.ts");

    public static async Task<bool> EnsureSegmentAsync()
    {
        if (File.Exists(SegmentPath)) return true;
        await Gate.WaitAsync();
        try
        {
            if (File.Exists(SegmentPath)) return true;
            // Try a titled slate first; drawtext needs fontconfig, so fall back
            // to plain black if that build/font is unavailable.
            const string titled =
                "color=c=black:s=640x360:d=4," +
                "drawtext=text='Canal no disponible':fontcolor=white:fontsize=28:x=(w-text_w)/2:y=(h-text_h)/2";
            if (await GenerateAsync(titled)) return true;
            return await GenerateAsync("color=c=black:s=640x360:d=4");
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> GenerateAsync(string videoGraph)
    {
        // Encode to a temp name and move atomically: File.Exists(SegmentPath)
        // is the fast-path check outside the gate, so the final path must only
        // ever hold a COMPLETE file (a half-written one would be served — and
        // cached — as the slate).
        var tmp = SegmentPath + ".tmp";
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Constantes.Global.FFMPEG_PATH,
                UseShellExecute = false,
            };
            foreach (var a in new[]
            {
                "-y", "-f", "lavfi", "-i", videoGraph,
                "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
                "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "32k", "-f", "mpegts", tmp,
            }) psi.ArgumentList.Add(a);

            proc = Process.Start(psi);
            if (proc == null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await proc.WaitForExitAsync(timeout.Token);
            if (proc.ExitCode != 0 || !File.Exists(tmp)) { TryDelete(tmp); return false; }
            File.Move(tmp, SegmentPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlaceholderSlate] generation failed: {ex.Message}");
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            TryDelete(tmp);
            return false;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
