using System.Text.RegularExpressions;

namespace AriaGui.Core.Downloads;

/// <summary>Pure Windows basename policy, independent of the current OS and filesystem.</summary>
public static partial class WindowsFileNamePolicy
{
    public const int MaximumLength = 240;
    public static bool IsValid(string name) => name.Length is > 0 and <= MaximumLength && name is not "." and not ".."
        && !name.EndsWith('.') && !name.EndsWith(' ')
        && !name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) && !ReservedName().IsMatch(name);

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
