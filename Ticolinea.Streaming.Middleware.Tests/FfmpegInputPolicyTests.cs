using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class FfmpegInputPolicyTests
{
    [Theory]
    [InlineData("http://origin.example.com/ch/1.m3u8")]
    [InlineData("https://origin.example.com/ch/1.m3u8")]
    [InlineData("HTTP://ORIGIN.EXAMPLE.COM/ch/1.m3u8")]
    public void Http_sources_get_rw_timeout_by_default(string fuente)
    {
        FfmpegInputPolicy.ShouldApplyRwTimeout(fuente, System.Array.Empty<string>())
            .Should().BeTrue();
    }

    [Fact]
    public void No_rw_timeout_token_opts_an_http_source_out()
    {
        FfmpegInputPolicy.ShouldApplyRwTimeout(
                "http://origin.example.com/ch/1.m3u8",
                new[] { "reconnect", "no_rw_timeout" })
            .Should().BeFalse();
    }

    [Fact]
    public void Explicit_rw_timeout_token_still_works_for_any_source()
    {
        // opt-in path unchanged: non-http source with the token gets the flag
        FfmpegInputPolicy.ShouldApplyRwTimeout(
                "rtmp://origin.example.com/live/ch1", new[] { "rw_timeout" })
            .Should().BeTrue();

        // http source with the token: still a single true decision (flag emitted once)
        FfmpegInputPolicy.ShouldApplyRwTimeout(
                "http://origin.example.com/ch/1.m3u8", new[] { "rw_timeout" })
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("rtmp://origin.example.com/live/ch1")]
    [InlineData("udp://239.0.0.1:1234")]
    [InlineData("/home/ticolineaplay/local/file.ts")]
    public void Non_http_sources_without_token_are_unchanged(string fuente)
    {
        FfmpegInputPolicy.ShouldApplyRwTimeout(fuente, new[] { "reconnect" })
            .Should().BeFalse();
    }

    [Fact]
    public void Managed_discontinuities_flag_on_adds_epoch_start_number_source()
    {
        FfmpegInputPolicy.ExtraHlsArgs(ffmpegManagedDiscontinuities: true)
            .Should().Be("-hls_start_number_source epoch");
    }

    [Fact]
    public void Managed_discontinuities_flag_off_adds_nothing()
    {
        // default-off = byte-for-byte today's args: the builder receives an empty string
        FfmpegInputPolicy.ExtraHlsArgs(ffmpegManagedDiscontinuities: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void Stream_map_pins_first_video_and_first_audio_by_default()
    {
        // Production incident (LogicSphere, canal 467): fuente multi-programa con
        // -c copy produjo segmentos con 2 video + 2 audio y ExoPlayer murió con
        // ERROR_CODE_DECODING_FAILED. El mapeo explícito fija un solo programa.
        FfmpegInputPolicy.StreamMapArgs(System.Array.Empty<string>())
            .Should().Be("-map 0:v:0? -map 0:a:0?");
    }

    [Fact]
    public void Stream_map_tokens_do_not_collide_with_reconnect_tokens()
    {
        // Los tokens de reconexión viven en el mismo campo (stream.Bitrate);
        // su presencia no debe desactivar el mapeo.
        FfmpegInputPolicy.StreamMapArgs(new[] { "reconnect", "rw_timeout" })
            .Should().Be("-map 0:v:0? -map 0:a:0?");
    }

    [Fact]
    public void Map_all_token_opts_a_channel_out_of_stream_mapping()
    {
        // Escape hatch por canal: conserva la estructura original de la fuente
        // (comportamiento previo, sin -map).
        FfmpegInputPolicy.StreamMapArgs(new[] { "map_all" })
            .Should().BeEmpty();
    }
}
