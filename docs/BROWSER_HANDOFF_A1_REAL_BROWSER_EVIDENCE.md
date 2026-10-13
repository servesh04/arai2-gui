# A1 narrow real-browser proof

2026-10-13 follow-up to [A1 reducer fixtures](BROWSER_HANDOFF_A1_EVIDENCE.md). No production integration or commit. This proof tests actual download APIs in newly created workspace profiles; it is not A01–A04 takeover acceptance.

## Result

| Browser | Executed result | Limits |
|---|---|---|
| Installed Chrome 154.0.8037.98 | Seven narrow checks passed; owned process closed | One synthetic loopback HTTPS origin with an exact synthetic certificate SPKI exception; headless, isolated profile |
| Installed EdgeCore 154.0.4258.62 | Same seven checks passed; owned process closed | Same controlled scope; EdgeCore executable, not installer/native-host acceptance |
| Cached Playwright Firefox 153.0 | Browser launched in isolated profile; extension installation unavailable through current automation | `about:debugging#/runtime/this-firefox` navigation timed out at 30 seconds waiting for load, and at 10 seconds waiting for commit; no addon or synthetic download proof executed |
| Stock Firefox | Not found in inspected installation paths | Cached Playwright Firefox is a patched testing binary; no stock Firefox acceptance claimed |

Chrome/Edge checks prove this limited controlled-source case is observable. They do not prove arbitrary downloads are public GETs. The harness owns the server and the fresh profile, serves deterministic reusable content, correlates native items with exact nonce-bound URLs and reads real server request methods/credential-header absence. A DownloadItem itself still omits method/auth provenance. The same URL was fetched via POST in a separate actual download and remained indistinguishable by URL alone.

The seven assertions are:

1. Two distinct real browser downloads of the same controlled URL issue GET twice and produce the expected identical 4096-byte SHA-256.
2. A persisted model cancellation intent precedes the synthetic cancel call; a fresh same-ID/start/source/name observation is interrupted/USER_CANCELED and is recognized by the A1 adapter.
3. Extension local storage contains the strict recovery record with no raw URL or filename.
4. The redirect fixture reports the initial URL separately from its final/effective URL.
5. A POST download at the exact same URL is observed at the server, showing URL membership is not GET proof.
6. Every recorded synthetic download/redirect request has no Cookie or Authorization header.
7. Native DownloadItem has no method property.

The controlled issuer/source semantics come from the harness, not from caller-supplied GET flags. Profile identity in this experiment is the owned new directory; production trustworthy profile identity, user consent and immutable issuer grant reacquisition are still unimplemented. The cancellation correlation has the same documented lack of an attributable transaction token: these successful samples cannot exclude another actor winning a race in a real profile.

## Isolation and artifacts

`scripts/Test-BrowserTakeoverEligibility.mjs` accepts only a new nonexistent output directory under workspace `artifacts`, creates a new profile and separate temporary extension, and connects to the debugger endpoint from that owned profile's DevToolsActivePort. Chrome/Edge load the unpacked probe through Extensions.loadUnpacked with unsafe extension debugging confined to that process. The probe's downloads/storage permissions belong only to the synthetic extension. Production manifests/backgrounds/permissions and app prepared-service registration were not changed. No native-host registration or personal-profile path was used.

The only explicit cancel targets the harness's slow synthetic URL, after matching ID/start/source/effective source and safe/private/initiating-extension state. No download history is erased. Other synthetic active responses end when the owned browser/server shuts down. Browser shutdown is not attributed as a successful cancel observation. Completion files/history and isolated profiles remain in the artifact directories for review. Raw synthetic URLs naturally exist in native browser download history and evidence; the no-source-storage assertion applies to the extension local recovery record, not a claim that browser history contains no URLs.

Final artifacts:

* `artifacts/browser-a1-real/chrome-final/evidence.json`
* `artifacts/browser-a1-real/edge-final/evidence.json`
* `artifacts/browser-a1-real/firefox-a/evidence.json` and `firefox-b/evidence.json` (failed install-page navigation; no API assertions)
* `artifacts/browser-a1-real/review-summary.json` (copied extension file hashes and confirmation that final Chrome/Edge reports match the current executed harness)

Successful reports include native before/after items, initial/effective URLs, same-URL POST item, repeated body hashes, method/credential-header observations, owned-process closure, browser executable SHA-256 and harness SHA-256. The harness was corrected after the earlier Chrome-a restricted launch exit, Chrome-b unsupported Worker.waitForFunction call and Chrome-c selection of a different built-in worker. Chrome-d and Edge-a are earlier successful reports; final reports use the reviewed worker-ID selection, distinct repeated-download IDs, awaited distinct POST completion and cleanup assertion. Those early failures are not browser feature failures and are not silently included as successful checks.

The certificate/key were reused from the synthetic B0 fixture. Chrome/Edge accept that exact SPKI only in the owned process. There is no certificate-store change or production TLS relaxation. Firefox's attempted path was HTTP-only and would not pass A1's HTTPS grant rule even if installation succeeded; no HTTPS eligibility result is claimed for Firefox.

The failed Firefox reports retain the early harness's default certificateException description. That metadata was inaccurate for Firefox: its path created an HTTP server and never reached a download API operation or applied a TLS exception. The summary explicitly records this correction; the failed reports are preserved rather than rewritten.

## Reproduction

From repository root, using the bundled Playwright package returned by the runtime dependency tool (path is environment-specific):

```powershell
node scripts/Test-BrowserTakeoverEligibility.mjs chrome 'C:\Program Files\Google\Chrome\Application\chrome.exe' artifacts/browser-a1-real/NEW-chrome 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules\playwright' artifacts/browser-phase-b/b0-final/synthetic-tls-cert.pem artifacts/browser-phase-b/b0-final/synthetic-tls-key.pem
node scripts/Test-BrowserTakeoverEligibility.mjs edge 'C:\Program Files (x86)\Microsoft\EdgeCore\154.0.4258.62\msedge.exe' artifacts/browser-a1-real/NEW-edge 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules\playwright' artifacts/browser-phase-b/b0-final/synthetic-tls-cert.pem artifacts/browser-phase-b/b0-final/synthetic-tls-key.pem
node scripts/Test-BrowserTakeoverEligibility.mjs firefox 'C:\Users\User\AppData\Local\ms-playwright\firefox-1538\firefox\firefox.exe' artifacts/browser-a1-real/NEW-firefox 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules\playwright' artifacts/browser-phase-b/b0-final/synthetic-tls-cert.pem artifacts/browser-phase-b/b0-final/synthetic-tls-key.pem
```

The restricted Chrome process exited before debugger readiness; approved ordinary execution was needed for browser/process/loopback access. Both final Chrome/Edge commands exited zero. Firefox's two attempted commands exited one and retained failure reports. Syntax check and the existing extension suite (99/99) passed after this harness addition; extension build still excludes the experiment. No production C# source changed in this follow-up.

## Gate decision

**Implemented/executed:** narrow controlled Chrome/Edge API feasibility. **Disabled:** all actual app takeover and production cancellation/permissions. **Blocked/unproven:** Firefox eligibility through available tooling and effective-source provider; generic browser method/auth provenance; production controlled issuer/profile/consent/restart integration. **Untested:** real worker/process kill boundaries, pause/cancel/completion/other-extension races, app reservation/commit transport, jobs/GIDs/origin duplication counts and loss-window recovery UI. **Release-accepted:** no automatic takeover. A2 activation, B1 and Phase C were not started.

This is enough to show a controlled Chrome/Edge experiment can observe the required native state. It is not enough to advertise automatic capture for every supported browser or move into activation. Keep A1/A2 gated. A subsequent separately reviewed scope may restrict support to proven browser/integration combinations, but that decision was not made here.

Primary references: [Chrome downloads API](https://developer.chrome.com/docs/extensions/reference/api/downloads), [Chrome extension automation](https://developer.chrome.com/docs/devtools/agents/extensions), [Mozilla DownloadItem](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/downloads/DownloadItem), [Firefox temporary-addon debugging](https://firefox-source-docs.mozilla.org/devtools-user/about_colon_debugging/index.html). Mozilla's documented item still has no Chromium-style finalUrl; current tooling failure does not prove Firefox itself incapable of a future controlled integration.
