using AriaGui.NativeHost;
using AriaGui.Application.Downloads;

if (args is ["--unregister"]) return HandoffRegistration.Unregister() ? 0 : 1;
if (args.Length > 0 && args[0] == "chrome-extension://" + HandoffRegistration.ExtensionId + "/")
    return await HandoffNativeSession.RunAsync(args);
return await NativeHostSession.RunAsync(args);
