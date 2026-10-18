using System.Globalization;

namespace AriaGui.Application.Downloads;

public sealed record SpeedLimitDefinition(string Name, long? BytesPerSecond);

public static class SpeedLimitPresetPolicy
{
    public const long MinimumBytesPerSecond = 1024;
    public const long MaximumBytesPerSecond = 1_073_741_824;

    public static IReadOnlyList<SpeedLimitDefinition> Build(SpeedLimitPreferences preferences)
    {
        preferences.Validate();
        var reduced = preferences.ReducedBytesPerSecond
            ?? Math.Max(MinimumBytesPerSecond, preferences.CustomBytesPerSecond / 2);
        return [new("Unlimited", null), new("Standard", null), new("Reduced", reduced),
            new("Custom", preferences.CustomBytesPerSecond)];
    }

    public static string ToAriaOptionValue(long? bytesPerSecond)
    {
        ValidateRate(bytesPerSecond);
        return (bytesPerSecond ?? 0).ToString(CultureInfo.InvariantCulture);
    }

    public static void ValidateRate(long? value)
    {
        if (value is < MinimumBytesPerSecond or > MaximumBytesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(value), "Choose unlimited or 1 KiB/s through 1 GiB/s.");
    }
}
