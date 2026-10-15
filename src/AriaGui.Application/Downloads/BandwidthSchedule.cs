namespace AriaGui.Application.Downloads;

public enum ScheduleAction { Profile, Block }

/// <summary>Minutes are measured from local midnight on the named weekday. End may cross midnight.</summary>
public sealed record WeeklyBandwidthInterval(DayOfWeek Day, int StartMinute, int EndMinute,
    ScheduleAction Action, BandwidthProfile? Profile = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Day) || StartMinute is < 0 or >= 1440 || EndMinute <= StartMinute || EndMinute > StartMinute + 1440 ||
            !Enum.IsDefined(Action) || Action == ScheduleAction.Profile && Profile is null || Action == ScheduleAction.Block && Profile is not null)
            throw new ArgumentException("Choose a valid weekly interval and action.");
        Profile?.Validate();
    }
}

public sealed record TemporaryBandwidthOverride(ScheduleAction Action, BandwidthProfile? Profile, DateTimeOffset ExpiresAt)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Action) || Action == ScheduleAction.Profile && Profile is null ||
            Action == ScheduleAction.Block && Profile is not null || ExpiresAt == default)
            throw new ArgumentException("Choose a valid temporary override and expiry.");
        Profile?.Validate();
    }
}

public sealed record BandwidthSchedule(string TimeZoneId, BandwidthProfile DefaultProfile,
    IReadOnlyList<WeeklyBandwidthInterval> Intervals, TemporaryBandwidthOverride? Override = null, bool Enabled = false,
    bool ResetDefaultOnDisable = false, IReadOnlyList<BandwidthProfile>? SavedProfiles = null)
{
    public static BandwidthSchedule Disabled { get; } = new(TimeZoneInfo.Local.Id, BandwidthProfile.Unlimited, []);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId) || TimeZoneId.Length > 128 || Intervals is null || Intervals.Count > 128)
            throw new ArgumentException("Choose a valid schedule and timezone.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        DefaultProfile.Validate(); Override?.Validate();
        if (SavedProfiles is { Count: > 32 }) throw new ArgumentException("Save at most 32 named bandwidth profiles.");
        if (SavedProfiles is not null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in SavedProfiles)
            {
                profile.Validate();
                if (!names.Add(profile.Name)) throw new ArgumentException("Saved profile names must be unique.");
            }
        }
        var occupied = new List<(int Start, int End)>();
        foreach (var interval in Intervals)
        {
            interval.Validate();
            var start = (int)interval.Day * 1440 + interval.StartMinute;
            var end = (int)interval.Day * 1440 + interval.EndMinute;
            if (end <= 10080) occupied.Add((start, end));
            else { occupied.Add((start, 10080)); occupied.Add((0, end - 10080)); }
        }
        occupied.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var i = 1; i < occupied.Count; i++)
            if (occupied[i].Start < occupied[i - 1].End)
                throw new ArgumentException("Weekly schedule intervals may not overlap.");
    }

    public ScheduleDecision Evaluate(DateTimeOffset now)
    {
        Validate();
        if (!Enabled) return new(false, DefaultProfile, false);
        if (Override is { } temporary && now < temporary.ExpiresAt)
            return new(temporary.Action == ScheduleAction.Block, temporary.Profile ?? DefaultProfile, true);
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));
        var minute = (int)local.DayOfWeek * 1440 + local.Hour * 60 + local.Minute;
        foreach (var interval in Intervals)
        {
            var start = (int)interval.Day * 1440 + interval.StartMinute;
            var end = (int)interval.Day * 1440 + interval.EndMinute;
            if (minute >= start && minute < end || end > 10080 && minute < end - 10080)
                return new(interval.Action == ScheduleAction.Block, interval.Profile ?? DefaultProfile, false);
        }
        return new(false, DefaultProfile, false);
    }
}

public sealed record ScheduleDecision(bool Blocked, BandwidthProfile Profile, bool TemporaryOverride);
