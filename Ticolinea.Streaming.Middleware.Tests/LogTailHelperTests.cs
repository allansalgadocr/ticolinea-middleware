using System;
using System.IO;
using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class LogTailHelperTests
{
    // ---- Log path resolution (must mirror log4net.config:
    //      %property{LogDir}/TL. + datePattern yyyyMMdd'.log') ----

    [Fact]
    public void File_name_follows_log4net_date_pattern()
    {
        LogTailHelper.LogFileName(new DateTime(2026, 7, 19)).Should().Be("TL.20260719.log");
        LogTailHelper.LogFileName(new DateTime(2025, 1, 3)).Should().Be("TL.20250103.log");
    }

    [Fact]
    public void Full_path_combines_resolved_dir_and_dated_file()
    {
        LogTailHelper.LogFilePath("/srv/fibraencasa/logs", new DateTime(2026, 7, 19))
            .Should().Be("/srv/fibraencasa/logs/TL.20260719.log");
    }

    [Fact]
    public void Directory_resolution_trims_trailing_slash_like_log4net_setup()
    {
        LogTailHelper.ResolveLogDirectory("/srv/x/logs/").Should().Be("/srv/x/logs");
        LogTailHelper.ResolveLogDirectory("/srv/x/logs").Should().Be("/srv/x/logs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Directory_resolution_falls_back_to_main_node_default(string? configured)
    {
        LogTailHelper.ResolveLogDirectory(configured).Should().Be("/home/ticolineaplay/logs");
    }

    // ---- Line-count clamping: default 200, min 1, cap 1000 ----

    [Fact]
    public void Line_count_defaults_to_200_when_unspecified()
    {
        LogTailHelper.ClampLineCount(null).Should().Be(200);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-25, 1)]
    [InlineData(1, 1)]
    [InlineData(999, 999)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(50000, 1000)]
    public void Line_count_is_clamped_to_1_through_1000(int requested, int expected)
    {
        LogTailHelper.ClampLineCount(requested).Should().Be(expected);
    }

    // ---- Tail over a synthetic file ----

    private static string WriteTempLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"TL.tailtest.{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Tail_returns_last_n_lines_in_order_when_file_is_larger()
    {
        var path = WriteTempLog("l1", "l2", "l3", "l4", "l5");
        try
        {
            LogTailHelper.TailLines(path, 3).Should().Equal("l3", "l4", "l5");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tail_returns_all_lines_when_asking_for_more_than_the_file_has()
    {
        var path = WriteTempLog("only", "two");
        try
        {
            LogTailHelper.TailLines(path, 200).Should().Equal("only", "two");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tail_of_empty_file_is_empty()
    {
        var path = WriteTempLog();
        try
        {
            LogTailHelper.TailLines(path, 200).Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tail_honors_the_clamped_cap_over_a_large_synthetic_file()
    {
        // 1500 lines, endpoint cap is 1000 → the last 1000 exactly.
        var lines = new string[1500];
        for (int i = 0; i < lines.Length; i++) lines[i] = $"line-{i + 1}";
        var path = WriteTempLog(lines);
        try
        {
            var tail = LogTailHelper.TailLines(path, LogTailHelper.ClampLineCount(5000));
            tail.Should().HaveCount(1000);
            tail[0].Should().Be("line-501");
            tail[^1].Should().Be("line-1500");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Tail_reads_a_file_held_open_for_writing()
    {
        // log4net's appender keeps the current file open with a write handle;
        // the tail must not fail on sharing (FileShare.ReadWrite on our side).
        var path = Path.Combine(Path.GetTempPath(), $"TL.tailtest.{Guid.NewGuid():N}.log");
        try
        {
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes("a\nb\nc\n");
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();

            LogTailHelper.TailLines(path, 2).Should().Equal("b", "c");
        }
        finally { File.Delete(path); }
    }
}
