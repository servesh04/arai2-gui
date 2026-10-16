namespace AriaGui.Browser.Contracts;

// Existing installed native-host wake command; payload-free and case-sensitive.
public static class GuiLaunchContract
{
    public const string BrowserWakeArgument = "--browser-wakeup";
    public static bool IsBrowserWake(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0] == BrowserWakeArgument;
}
