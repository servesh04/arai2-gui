const HOST = "com.ariagui.handoff";
const status = document.getElementById("status");
const enabled = document.getElementById("enabled");
const site = document.getElementById("site");

async function activeSite() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  try { const url = new URL(tab?.url); return ["http:", "https:"].includes(url.protocol) ? url.hostname : null; }
  catch { return null; }
}
async function load() {
  const saved = await chrome.storage.local.get({ enabled: false, excludedSites: [] });
  enabled.checked = saved.enabled;
  const host = await activeSite();
  site.disabled = !host;
  site.checked = !!host && saved.excludedSites.includes(host);
  site.onchange = async () => {
    if (!host) return;
    const sites = new Set((await chrome.storage.local.get({ excludedSites: [] })).excludedSites);
    if (site.checked) sites.add(host); else sites.delete(host);
    await chrome.storage.local.set({ excludedSites: [...sites] });
  };
  enabled.onchange = () => chrome.storage.local.set({ enabled: enabled.checked });
  try {
    const reply = await Promise.race([
      chrome.runtime.sendNativeMessage(HOST, { version: 1, action: "hello" }),
      new Promise(resolve => setTimeout(() => resolve({ error: "app-unavailable" }), 4000))
    ]);
    status.textContent = reply.ok ? "Connected" : reply.error === "app-unavailable" ? "App not running" : "Host not registered";
  } catch { status.textContent = "Host not registered"; }
}
load();
