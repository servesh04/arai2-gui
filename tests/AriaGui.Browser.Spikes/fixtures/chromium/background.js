// Hello-only throwaway extension. No add/download/cookie functionality.
const port = chrome.runtime.connectNative("com.ariagui.wp0_spike");
port.onMessage.addListener((reply) => {
  chrome.storage.local.set({ spikeResult: reply.result?.code, requestId: reply.requestId });
  port.disconnect();
});
port.onDisconnect.addListener(() => {
  if (chrome.runtime.lastError) chrome.storage.local.set({ spikeResult: "host_unavailable" });
});
port.postMessage({ protocol: { major: 1, minor: 0 }, requestId: "e204774d-08ba-4a09-96c9-9e64f2b31ce3",
  action: "hello", payload: { extensionVersion: "0.0.0", installationId: "7df3ed34-ea26-450b-a428-90ce4c9589ec" } });
