# A1 disabled reducer and adapter fixture evidence

Date: 2026-10-13. Baseline: Package 8 `2753957` plus the preserved uncommitted A0/B0 changes. This is a later evidence addendum, not a rewrite of historical WP8 acceptance.

This report covers the browser-free fixture milestone. The subsequent [narrow real-browser proof](BROWSER_HANDOFF_A1_REAL_BROWSER_EVIDENCE.md) records controlled Chrome/Edge API success and a Firefox tooling blocker. It does not upgrade these fixtures to A01–A04 or complete the production eligibility/recovery gate.

**Implemented:** a browser-independent reducer, closed versioned recovery schema, controlled-source fixture issuer, injected browser-specific snapshot mapping, and executable worker/recovery fixtures. **Disabled:** automatic takeover, live pause/cancel, real grant issuance, new downloads permissions and new transport actions. **Blocked:** A1's real controlled-browser replayability/context proof. **Untested:** real browser worker/process death and A01–A04; no browser versions or browser acceptance claimed. **Release-accepted:** none of automatic takeover. B1 and Phase C were not started. No commit was created.

## Code and boundaries

`extensions/aria-gui/experiments/takeover/` is outside packaged `src`; the build copies only `src`. No module is imported by the production background and there are no browser globals/listeners. The driver defaults to disabled and additionally requires fixture-tagged browser, app and grant dependencies. These tags are harness guards, not a security boundary. The existing v1 schema and three extension permissions remain unchanged. No changes connect the unregistered app-side prepared service to this experiment.

The reducer stores UUID request/install/caller/profile/grant identity, browser family/item/start identity, version, phase, timestamps, attempted-effect flags and stable accepted job ID. A closed validator rejects extra properties and non-UUID identity fields; raw source, filename, cookies, headers, browser profile paths and exception text are absent. A 32-record cap refuses new work without evicting history. Effect flags distinguish an unresolved attempt from an observation; they do not claim a browser side effect succeeded.

The trusted synthetic issuer independently declares GET/public/no-auth/no-cookies/repeatability and binds issuer/revision/expiry, exact HTTPS source/name, request/install/caller/profile/browser/item/start identity. Its immutable grant ID cannot be rebound to a changed resource. It keeps raw context in issuer memory. Neither browser DownloadItem fields nor caller GET flags establish a grant. Recovery reacquires the same trusted issuer record; loss/revocation/expiry of that record stops mutations. Fixtures that retain this issuer across driver reconstruction model an independently surviving integration, not worker-local memory magically surviving death.

Prepare status and mutations use one stable request key. Pause and cancel intent are saved and awaited before the injected first operation. Item identity/context are re-read after preparation/pause and after cancel. Only fresh, unchanged interrupted/USER_CANCELED plus our persisted attempt permits fixture commit. A fulfilled call alone never does. Changed, disappeared, completed, dangerous, private, extension-origin, container or unrelated-interruption observations stop cancellation/commit. Changed or missing source/name cannot be recovered from browser storage.

Recovery makes one status query per invocation. Accepted receipts are monotonic and acceptance can be discovered even if item/grant disappears. Accepted recovery never prepares/cancels/commits again. Before-side-effect observed records remain inert. Unresolved cancellation intent with an in-progress item stays cannot-confirm; cancellation is never blindly repeated. Aborted/expired/unavailable/unknown outcomes preserve pending history and do not create a replacement, erase, resume or restart a browser item. This slice returns recovery phases for a future deliberate UI; it does not implement that UI or terminal-ledger pruning.

## Verification

Commands from `extensions/aria-gui`:

```text
npm run build
npm test
node --test --test-reporter=tap tests/takeover.test.mjs
```

From repository root:

```text
dotnet test tests/AriaGui.Browser.Contracts.Tests/AriaGui.Browser.Contracts.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal
dotnet test tests/AriaGui.Browser.Contracts.Tests/AriaGui.Browser.Contracts.Tests.csproj --no-build --no-restore -m:1 --verbosity minimal --logger trx --results-directory artifacts/browser-a1/contracts-results
git diff --check
```

The Node suite tests all three browser-family mappings; driver reconstruction at twelve persistence/API/ACK boundaries; save failures before pause/cancel; same-key callbacks and profile isolation; every grant/context binding; initial and fresh changed/disappeared/completed/dangerous/private/extension-origin observations; Firefox containers; ambiguous cancellation; lost prepare/commit ACK; issuer loss/revocation/expiry; immutable resource identity; capacity/schema refusal; and source/name marker absence from every serialized recovery snapshot. Packaging assertions verify no experiment module or downloads permission in generated artifacts. These are browser-free in-memory fixtures with injected termination exceptions and driver reconstruction, not actual browser worker kill tests or a durable storage-engine acceptance test.

Initial Node run: 93 passed, 1 failed. It caught a pre-cancel pause by another actor during preparation. The fresh check now rejects paused items unless our own prior pause intent exists. Review also added checks before pause and after awaited persistence for grant revocation/expiry, danger/context changes, and changed cancellation observations before commit. Final Node results: **99/99** overall, **62/62** focused A1 fixtures. The focused TAP artifact is `artifacts/browser-a1/takeover-tests.tap`. Extension build passed; experiments remain absent from generated trees. `git diff --check` passed.

The .NET command compiled current browser-contract source without warnings, then the sandboxed VSTest testhost connection timed out at 90 seconds with no assertion results. The second command used approved testhost access and the just-built binaries: **94/94 passed**, recorded in `artifacts/browser-a1/contracts-results/`. No C# source changed for A1; the preceding A0 app build remains separately reported. No claim of a new A1 app build is needed or made.

## Exact remaining gate

Before A expansion, prove a real controlled integration can establish method, repeatability and credential-free context for the particular download independently of URL membership. A generic onCreated callback does not provide that proof. The integration needs trustworthy consent/profile identity and immutable issuer/grant reacquisition after worker restart, or must visibly stop at cannot-confirm. Source retention approval from A0 remains open independently.

Mozilla's documented DownloadItem lacks Chromium's `finalUrl`. The positive Firefox fixture is expressly an enriched synthetic snapshot, not a native Firefox sample. Native-shaped snapshots missing an independently established effective source fail eligibility in all three mappings; there is no fallback from missing `finalUrl` to `url`. Firefox's effective-source proof/provider remains an additional concrete browser gate. [Mozilla DownloadItem](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/downloads/DownloadItem).

An actual throwaway-profile Chrome/Edge/Firefox test must map current snapshots and cancellation reasons, record browser/build versions, exercise pause/cancel/completion races, initiating extensions and unsupported privacy/container context, and terminate real workers at each boundary. Downloads permission is not present in current artifacts, so that test was not performed or silently activated. Chrome describes cancel completion as potentially canceled, completed, interrupted or absent; DownloadItem does not establish original method/auth context. [Chrome downloads API](https://developer.chrome.com/docs/extensions/reference/api/downloads). Firefox exposes initiating extension, privacy, danger and cookie-store context, and USER_CANCELED as an interruption reason. [Mozilla DownloadItem](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/downloads/DownloadItem), [Mozilla interruption reasons](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/downloads/InterruptReason).

USER_CANCELED has no transaction token attributing the outcome to this extension: another actor can win after persisted cancellation intent. Fixture observations cannot remove that uncertainty or the cancellation-to-app-commit loss window. Real A01–A04 and separately reviewed A2 transport/optional-permission/recovery UI remain required. Stop A expansion here until that proof and its separate scope gate are satisfied.
