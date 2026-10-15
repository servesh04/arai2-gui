using AriaGui.Application.Downloads;

namespace AriaGui.Application.Tests;

public sealed class BandwidthScheduleTests
{
    private static readonly BandwidthProfile Metered = new("Metered", new(32768), ByteRateLimit.Unlimited);

    [Fact]
    public void Midnight_and_week_wrap_use_current_local_wall_time()
    {
        var schedule = new BandwidthSchedule("UTC", BandwidthProfile.Unlimited,
            [new(DayOfWeek.Saturday, 23 * 60, 25 * 60, ScheduleAction.Block)], Enabled: true);
        Assert.False(schedule.Evaluate(new DateTimeOffset(2026, 10, 10, 22, 59, 0, TimeSpan.Zero)).Blocked);
        Assert.True(schedule.Evaluate(new DateTimeOffset(2026, 10, 11, 0, 30, 0, TimeSpan.Zero)).Blocked);
        Assert.False(schedule.Evaluate(new DateTimeOffset(2026, 10, 11, 1, 0, 0, TimeSpan.Zero)).Blocked);
    }

    [Fact]
    public void Dst_gap_is_skipped_and_repeated_hour_evaluates_twice()
    {
        var zone = "Eastern Standard Time";
        var gap = new BandwidthSchedule(zone, BandwidthProfile.Unlimited,
            [new(DayOfWeek.Sunday, 120, 180, ScheduleAction.Block)], Enabled: true);
        Assert.False(gap.Evaluate(new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero)).Blocked);
        Assert.False(gap.Evaluate(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero)).Blocked);
        var repeat = new BandwidthSchedule(zone, BandwidthProfile.Unlimited,
            [new(DayOfWeek.Sunday, 60, 120, ScheduleAction.Block)], Enabled: true);
        Assert.True(repeat.Evaluate(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero)).Blocked);
        Assert.True(repeat.Evaluate(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)).Blocked);
    }

    [Fact]
    public void Timezone_override_expiry_and_overlap_are_explicit()
    {
        var now = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero);
        var interval = new WeeklyBandwidthInterval(DayOfWeek.Monday, 7 * 60, 8 * 60, ScheduleAction.Profile, Metered);
        var utc = new BandwidthSchedule("UTC", BandwidthProfile.Unlimited, [interval], Enabled: true);
        var india = utc with { TimeZoneId = "India Standard Time" };
        Assert.Equal(BandwidthProfile.Unlimited, utc.Evaluate(now).Profile);
        Assert.Equal(Metered, india.Evaluate(now).Profile);
        var temporary = utc with { Override = new(ScheduleAction.Block, null, now.AddMinutes(1)) };
        Assert.True(temporary.Evaluate(now).Blocked);
        Assert.False(temporary.Evaluate(now.AddMinutes(1)).Blocked);
        Assert.Throws<ArgumentException>(() => (utc with { Intervals = [interval,
            new(DayOfWeek.Monday, 7 * 60 + 30, 8 * 60 + 30, ScheduleAction.Block)] }).Validate());
    }
}
