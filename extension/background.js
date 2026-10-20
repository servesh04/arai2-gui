const HOST = "com.ariagui.handoff";
const defaults = { enabled: false, autoCapture: true, altBypass: true, minimumSizeBytes: 0,
  captureFileTypes: [], excludedSites: [] };

async function settings() {
  return { ...defaults, ...await chrome.storage.local.get(Object.keys(defaults)) };
}
function httpUrl(raw) {
  try { const url = new URL(raw); return ["http:", "https:"].includes(url.protocol) ? url : null; }
  catch { return null; }
}
function siteExcluded(host, sites) {
  return sites.some(site => host === site || host.endsWith("." + site));
}
function nativeMessage(payload) {
  return Promise.race([
    chrome.runtime.sendNativeMessage(HOST, payload).catch(() => ({ ok: false, error: "host-not-registered" })),
    new Promise(resolve => setTimeout(() => resolve({ ok: false, error: "timeout" }), 4000))
  ]);
}
async function cookiesFor(url) {
  try {
    const values = await chrome.cookies.getAll({ url });
    return values.map(cookie => `${cookie.name}=${cookie.value}`).join("; ");
  } catch { return null; }
}
async function remember(key, url, milliseconds) {
  await chrome.storage.session.set({ [key + url]: Date.now() + milliseconds });
}
async function remembered(key, url) {
  const name = key + url;
  const result = await chrome.storage.session.get(name);
  return (result[name] || 0) > Date.now();
}
async function sendDownload(url, data, trigger) {
  const address = httpUrl(url);
  if (!address) return { ok: false, reason: "not-captured" };
  const request = { version: 1, url: address.href, trigger, method: "GET",
    filename: data.filename || undefined, mime: data.mime || undefined,
    referrer: httpUrl(data.referrer)?.href, tabSite: httpUrl(data.tabSite)?.href,
    sizeBytes: knownSize(data),
    userAgent: navigator.userAgent, cookies: await cookiesFor(address.href) || undefined };
  const result = await nativeMessage(request);
  if (result?.ok === true) await remember("sent:", address.href, 10000);
  return result;
}
function knownSize(item) {
  for (const size of [item.totalBytes, item.fileSize])
    if (Number.isSafeInteger(size) && size >= 0) return size;
  return undefined;
}
async function discardBrowserCopy(id) {
  const [before] = await chrome.downloads.search({ id });
  if (before?.state === "in_progress") {
    try { await chrome.downloads.cancel(id); } catch { /* It may have completed during the handoff. */ }
  }
  const [after] = await chrome.downloads.search({ id });
  if (after?.state === "in_progress") return;
  if (after?.state === "complete") await chrome.downloads.removeFile(id);
  await chrome.downloads.erase({ id });
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: "ariagui-download", title: "Download with AriaGUI", contexts: ["link"],
    targetUrlPatterns: ["http://*/*", "https://*/*"] });
});
chrome.runtime.onMessage.addListener((message, sender) => {
  if (message?.kind === "alt-link" && sender.tab?.id !== undefined && httpUrl(message.url))
    remember("alt:", message.url, 5000);
});
chrome.webRequest.onBeforeRequest.addListener(details => {
  if (details.type === "other" || details.type === "main_frame" || details.type === "sub_frame")
    chrome.storage.session.set({ ["verb:" + details.url]: { method: details.method, until: Date.now() + 10000 } });
}, { urls: ["http://*/*", "https://*/*"] });

chrome.downloads.onCreated.addListener(async item => {
  try {
    if (item.byExtensionId === chrome.runtime.id) return;
    const config = await settings();
    if (!config.enabled) return;
    const handshake = await nativeMessage({ version: 1, action: "hello" });
    if (handshake?.ok !== true || !handshake.settings?.enabled || !handshake.settings?.autoCapture) return;
    const app = handshake.settings;
    const raw = item.finalUrl || item.url;
    const address = httpUrl(raw);
    if (!address || await remembered("sent:", address.href)
      || app.altBypass && await remembered("alt:", address.href)
      || siteExcluded(address.hostname, [...config.excludedSites, ...app.excludedSites])) return;
    let verb;
    for (let attempt = 0; attempt < 5; attempt++) {
      verb = (await chrome.storage.session.get("verb:" + address.href))["verb:" + address.href];
      if (verb?.until > Date.now()) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    if (verb?.method !== "GET") return; // No method evidence means leave the browser in charge.
    const size = knownSize(item);
    if (size !== undefined && size < app.minimumSizeBytes) return;
    const extension = /\.[a-z0-9]+$/i.exec(item.filename || address.pathname)?.[0]?.toLowerCase();
    if (app.captureFileTypes.length && !app.captureFileTypes.some(type => type.toLowerCase() === extension)) return;
    const result = await sendDownload(address.href, item, "download");
    if (result?.ok === true) await discardBrowserCopy(item.id);
  } catch { /* Browser download continues on any failure. */ }
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== "ariagui-download" || !httpUrl(info.linkUrl)) return;
  const config = await settings();
  if (!config.enabled) { await chrome.downloads.download({ url: info.linkUrl }); return; }
  try {
    const result = await sendDownload(info.linkUrl, { referrer: info.pageUrl, tabSite: tab?.url }, "contextMenu");
    if (result?.ok === true) return;
  } catch { }
  await chrome.downloads.download({ url: info.linkUrl });
});
