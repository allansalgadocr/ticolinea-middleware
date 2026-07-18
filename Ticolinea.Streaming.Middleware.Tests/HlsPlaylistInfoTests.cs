using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class HlsPlaylistInfoTests
{
    [Fact]
    public void Happy_path_parses_sequence_duration_and_last_segment()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-VERSION:3\n" +
                   "#EXT-X-TARGETDURATION:6\n" +
                   "#EXT-X-MEDIA-SEQUENCE:1042\n" +
                   "#EXT-X-DISCONTINUITY\n" +
                   "#EXTINF:6.000000,\n" +
                   "123_1042.ts\n" +
                   "#EXTINF:6.000000,\n" +
                   "123_1043.ts\n" +
                   "#EXTINF:5.960000,\n" +
                   "123_1044.ts\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.MediaSequence.Should().Be(1042);
        info.TargetDuration.Should().Be(6);
        info.LastSegment.Should().Be("123_1044.ts");
    }

    [Fact]
    public void Crlf_line_endings_are_tolerated()
    {
        var text = "#EXTM3U\r\n" +
                   "#EXT-X-TARGETDURATION:4\r\n" +
                   "#EXT-X-MEDIA-SEQUENCE:7\r\n" +
                   "#EXTINF:4.0,\r\n" +
                   "55_7.ts\r\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.MediaSequence.Should().Be(7);
        info.TargetDuration.Should().Be(4);
        info.LastSegment.Should().Be("55_7.ts");
    }

    [Fact]
    public void Blank_lines_and_trailing_whitespace_are_ignored()
    {
        var text = "#EXTM3U\n\n" +
                   "#EXT-X-TARGETDURATION:6\n" +
                   "\n" +
                   "#EXTINF:6.0,\n" +
                   "9_0.ts  \n" +
                   "\n\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.LastSegment.Should().Be("9_0.ts");
    }

    [Fact]
    public void Missing_media_sequence_defaults_to_zero()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-TARGETDURATION:6\n" +
                   "#EXTINF:6.0,\n" +
                   "1_0.ts\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.MediaSequence.Should().Be(0);
        info.TargetDuration.Should().Be(6);
    }

    [Fact]
    public void Missing_target_duration_defaults_to_zero()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-MEDIA-SEQUENCE:3\n" +
                   "#EXTINF:6.0,\n" +
                   "1_3.ts\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.TargetDuration.Should().Be(0);
        info.MediaSequence.Should().Be(3);
    }

    [Fact]
    public void No_segments_yet_gives_empty_last_segment()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-TARGETDURATION:6\n" +
                   "#EXT-X-MEDIA-SEQUENCE:0\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.LastSegment.Should().Be("");
    }

    [Fact]
    public void Malformed_numeric_values_fall_back_to_defaults()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-TARGETDURATION:abc\n" +
                   "#EXT-X-MEDIA-SEQUENCE:\n" +
                   "#EXTINF:6.0,\n" +
                   "2_0.ts\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.MediaSequence.Should().Be(0);
        info.TargetDuration.Should().Be(0);
        info.LastSegment.Should().Be("2_0.ts");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\r\n")]
    public void Empty_or_whitespace_text_returns_null(string text)
    {
        HlsPlaylistInfo.Parse(text).Should().BeNull();
    }

    [Fact]
    public void Text_without_extm3u_header_returns_null()
    {
        HlsPlaylistInfo.Parse("<html>404 not found</html>").Should().BeNull();
    }

    [Fact]
    public void Master_playlist_returns_null()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720\n" +
                   "720p/index.m3u8\n";

        HlsPlaylistInfo.Parse(text).Should().BeNull();
    }

    [Fact]
    public void Media_sequence_larger_than_int_is_kept_as_long()
    {
        var text = "#EXTM3U\n" +
                   "#EXT-X-MEDIA-SEQUENCE:5000000000\n" +
                   "#EXTINF:6.0,\n" +
                   "1_5000000000.ts\n";

        var info = HlsPlaylistInfo.Parse(text);

        info.Should().NotBeNull();
        info!.MediaSequence.Should().Be(5_000_000_000L);
    }
}
