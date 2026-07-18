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
}
