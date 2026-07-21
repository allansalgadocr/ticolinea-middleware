using FluentAssertions;
using ticolinea.stream.service.NodeConsole;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// The owner edits stream sources directly, so validation is the last thing
// standing between a typo and a dead channel. Pure so it can be exercised here
// and reused by the controllers.
public class ConsoleValidationTests
{
    [Fact]
    public void A_channel_with_a_name_and_a_supported_source_is_accepted()
    {
        ConsoleValidation.Channel("Teletica 7", "http://origin.example/live.m3u8").Should().BeNull();
    }

    [Theory]
    [InlineData("http://a/b.m3u8")]
    [InlineData("https://a/b.m3u8")]
    [InlineData("srt://198.51.100.44:9000?streamid=x")]
    [InlineData("rtmp://a/live/key")]
    [InlineData("rtmps://a/live/key")]
    [InlineData("rtsp://a:554/stream")]
    [InlineData("udp://239.0.0.1:1234")]
    public void Every_scheme_ffmpeg_can_actually_read_is_accepted(string source)
    {
        ConsoleValidation.Channel("Canal", source).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_channel_without_a_name_is_rejected(string name)
    {
        ConsoleValidation.Channel(name, "http://a/b.m3u8").Should().Be("El nombre del canal es obligatorio.");
    }

    [Fact]
    public void A_channel_without_a_source_is_rejected()
    {
        ConsoleValidation.Channel("Canal", "  ").Should().Be("El origen del stream es obligatorio.");
    }

    [Theory]
    [InlineData("origin.example/live.m3u8")]   // no scheme at all
    [InlineData("file:///etc/passwd")]          // local file read
    [InlineData("javascript:alert(1)")]
    public void A_source_ffmpeg_cannot_stream_is_rejected(string source)
    {
        ConsoleValidation.Channel("Canal", source)
            .Should().Be("El origen debe iniciar con http, https, srt, rtmp, rtmps, rtsp o udp.");
    }

    [Fact]
    public void An_over_long_channel_name_is_rejected_before_mysql_truncates_it()
    {
        ConsoleValidation.Channel(new string('x', 121), "http://a/b.m3u8")
            .Should().Be("El nombre del canal no puede superar 120 caracteres.");
    }

    [Fact]
    public void A_category_needs_a_name()
    {
        ConsoleValidation.Category(" ").Should().Be("El nombre de la categoría es obligatorio.");
    }

    [Fact]
    public void A_valid_category_name_is_accepted()
    {
        ConsoleValidation.Category("Deportes").Should().BeNull();
    }

    [Theory]
    [InlineData("ab")]                 // too short
    [InlineData("Operaciones")]        // uppercase
    [InlineData("con espacio")]
    [InlineData("raro!")]
    public void A_username_that_is_not_a_simple_lowercase_handle_is_rejected(string username)
    {
        ConsoleValidation.NewUser(username, "unaClaveLarga123")
            .Should().Be("El usuario debe tener entre 3 y 32 caracteres en minúscula (letras, números, punto, guion o guion bajo).");
    }

    [Fact]
    public void A_short_password_is_rejected()
    {
        ConsoleValidation.NewUser("operaciones", "corta")
            .Should().Be("La contraseña debe tener al menos 12 caracteres.");
    }

    [Fact]
    public void A_valid_new_user_is_accepted()
    {
        ConsoleValidation.NewUser("operaciones", "unaClaveLarga123").Should().BeNull();
    }
}
