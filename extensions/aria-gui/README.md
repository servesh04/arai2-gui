# Public-link extension MVP

One shared dependency-free source tree; Node.js 22+ builds browser-specific development artifacts. No installation or registry mutation is performed.

```powershell
node extensions/aria-gui/build.mjs
node --test extensions/aria-gui/tests/*.test.mjs
```

Generated output is ignored under `dist/`: Chrome/Edge MV3 module service workers, Firefox MV3 nonpersistent script/event page, three native-manifest templates and exact derived identities. `identities.development.json` explicitly pins development keys/add-on ID/host name. Native executable path remains an unresolved installer placeholder. These artifacts are not production/store registrations; never overwrite a production host registration with them.

Default disabled. Options require the selected **app installation UUID** from a trusted installer and offer connection test; no UUID discovery channel or installer is introduced here. A separate generated extension-instance UUID is local metadata only. Established v1 cannot isolate ledger identity per browser/profile/extension install; E05 remains open.

Right-click a public HTTP(S) link to request GET. Source URL is transient in memory/native request only; pending storage contains app UUID, stable request UUID, timestamp and uncertainty state. Worker wake/reconnect queries pending status with the same target ID and never resends Add. `not_found`, processing or lost transport remain uncertain because a delayed transaction can still commit. At most 32 pending requests are retained; admission stops when full rather than dropping unresolved IDs. Reconciliation checks at most eight per wake/manual check, preserving all remaining records.

Private contexts are refused; no automatic capture, content scripts, host permissions, cookies, POST or browser download APIs. App history does retain the URL. Popup acceptance means saved to AriaGUI, not transfer completion. Firefox140+ declares required websiteContent consent because an explicit link is transmitted outside the browser; store/privacy review remains required.

No runtime npm dependencies; the v1 validator is derived from the repository's fixture compatibility implementation and tested against the same fixtures. See `docs/BROWSER_HANDOFF_WP5_EVIDENCE.md` for actual acceptance limits.
