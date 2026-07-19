using System;
using System.Linq;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

public class LogRetentionTests
{
    private static readonly DateTime Now = new(2026, 07, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Deletes_only_TL_files_older_than_retention()
    {
        var files = new[]
        {
            ("TL.20260701.log", Now.AddDays(-18)),      // expired
            ("TL.20260705.log.1", Now.AddDays(-14.5)),  // size-rolled suffix, expired
            ("TL.20260718.log", Now.AddDays(-1)),       // fresh
            ("TL.20260719.log", Now),                   // today
            ("other.txt", Now.AddDays(-90)),            // foreign file: never touched
            ("schema.sql", Now.AddDays(-90)),           // foreign file: never touched
        };
        var expired = LogTailHelper.SelectExpiredLogFiles(files, Now, 14).ToList();
        Assert.Equal(new[] { "TL.20260701.log", "TL.20260705.log.1" }, expired);
    }

    [Fact]
    public void Boundary_exactly_at_cutoff_is_kept()
    {
        var files = new[] { ("TL.20260705.log", Now.AddDays(-14)) }; // == cutoff, not older
        Assert.Empty(LogTailHelper.SelectExpiredLogFiles(files, Now, 14));
    }

    [Fact]
    public void Retention_floor_is_one_day()
    {
        // A misconfigured 0/negative retention must never mean "delete everything".
        var files = new[] { ("TL.20260719.log", Now.AddHours(-2)) };
        Assert.Empty(LogTailHelper.SelectExpiredLogFiles(files, Now, 0));
    }
}
