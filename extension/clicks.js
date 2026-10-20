document.addEventListener("click", event => {
  if (!event.altKey) return;
  const link = event.target.closest?.("a[href]");
  if (link) chrome.runtime.sendMessage({ kind: "alt-link", url: link.href });
}, true);
