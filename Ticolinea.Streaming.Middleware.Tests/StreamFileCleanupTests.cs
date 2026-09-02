using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// EliminarArchivosViejos wipes everything in the streams folder older than 20
// minutes. The placeholder slate lives there, is written once and never again —
// it must be exempt or placeholders regenerate (and race) every half hour.
public class StreamFileCleanupTests
{
    [Fact]
    public void The_placeholder_slate_is_never_deleted()
        => StreamFileCleanup.IsProtected("placeholder_slate.ts").Should().BeTrue();

    [Fact]
    public void Ordinary_segments_and_playlists_are_not_protected()
    {
        StreamFileCleanup.IsProtected("467_8587.ts").Should().BeFalse();
        StreamFileCleanup.IsProtected("467_.m3u8").Should().BeFalse();
    }
}
