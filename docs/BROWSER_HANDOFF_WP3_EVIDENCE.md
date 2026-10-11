# Browser handoff Package 3: private application bridge

2026-10-11; base `1c9c7c7`. Implements the GUI-composed lifecycle service and isolated executable tests. **Production browser traffic remains disabled.** No host, extension, browser registry registration, or new engine owner is supplied.

## Endpoint and discovery contract (Package 4 must reuse)

The primary `SingleInstanceLease` owns the selected private runtime. `BrowserBridgeEndpoint.Open` publishes `browser-installation-v1.txt` there: a nonzero D-format UUID, UTF-8, at most 64 bytes. Creation writes and flushes a random temporary file under the private inherited directory ACL, then atomically moves it without overwrite. The primary lease serializes publication; malformed/reparse identity files fail closed. Identity survives restart. Do not create a new identity on corruption or silently select another installation.

Pipe name is `AriaGUI.browser.v1.` followed by the complete uppercase SHA-256 hex of UTF-8 text joining these fields with one LF, no final LF:

1. literal `1`
2. channel
3. package model
4. absolute runtime path, trailing separator trimmed, invariant uppercase
5. current Windows user SID
6. installation UUID in lowercase D format

The deployed host must select the trusted installation/runtime/channel/package model and reuse `BrowserBridgeEndpoint.Name`; never take any of them from page JSON. It reads the private identity file; no globally writable discovery manifest is introduced. Connect only to local `.`. After connecting, the host must verify the actual pipe server PID and trusted selected GUI executable/package identity before sending a request (Package 4 gate). A name or ready response alone is insufficient authentication. Tests verify the expected live server PID; production client implementation is still absent.

Current test contexts are `fixture/unpackaged` and `verification/synthetic-gui`. Release channel/package selection is intentionally not activated. Normal Release OnLaunched contains no marker/listener activation. Debug/explicit verification requires both the existing marked synthetic profile and its `browser-bridge-synthetic-peer.txt` containing an absolute trusted peer executable path. This marker is not a production preference or request-controlled switch.

## Security, admission and lifecycle decision

`BrowserBridgeServer` verifies its endpoint matches the primary lease runtime. It initializes repository/intake plus an authoritative successful preferences read before listening. It creates all four pipe handles before readiness. Handles are overlapped duplex byte streams, protected DACL with network SID deny and current-user allow, `PIPE_REJECT_REMOTE_CLIENTS`, and first-instance flag on the first handle. Four reusable handles remain allocated through running lifetime, preventing an interval with no owning pipe instance. The OS pipe capacity is four; there is no unbounded managed session queue.

Each connected peer must impersonate to the expected current-user SID and its kernel-reported client PID must resolve to the trusted executable path. Failure disconnects without revealing a response/job. This is same-user/executable-path verification, **not signature verification or browser-extension authorization**. A same-user attacker able to run/replace that trusted executable remains within the later host/install trust gate. The server derives the ledger caller key `browser-native-host-v1` from fixed trusted server context; request JSON supplies neither caller identity nor authorization. Installation mismatch is rejected. The future host must independently authenticate its allowed browser launch/extension origin.

Receive/parse runs on background tasks, not the UI dispatcher. Each session has at most eight frames, a 15-second receive-session budget and Contracts' five-second whole-frame read/write budget. Intake requests use a five-second cancellation deadline; synchronous SQLite work cannot be forcibly preempted, so this is cancellation-aware rather than a universal hard execution bound. Four session tasks bound that work. No direct aria2 RPC is used. Engine starting/offline can still accept a durable queued intent; readiness means storage/intake/preferences capability, not engine running.

StopAdmission takes a synchronous barrier before app-lifetime cancellation, closes receive admission and cancels waiting/read connections. Already admitted intake does not use disconnect cancellation; accepted engine work retains application lifetime. App shutdown bounds bridge startup wait and drain to two seconds each, disposes listener handles, and retains existing checkpoints, tray and engine shutdown. A canceled/lost response is recovered by stable request ID, not another blind engine Add. Tray close does not call StopAdmission or force focus on background intake. Secondary launch exits through the existing lease/tray request path and never registers a listener. No fixed launch-mode/payload mechanism was needed here.

The v1 schema adds `removed` before deployment so retained Removed jobs map accurately. `expired` remains a deleted-history tombstone. Engine uncertainty maps to `engine_uncertain`; domain storage-read diagnostics are not emitted as illegal accepted-result fields. All emitted results pass the closed Contracts serializer/parser. There is no raw exception/URL diagnostic output.

## Executable evidence and limitations

| Gate | Evidence | Disposition |
|---|---|---|
| W01 | Real synthetic server/client processes: startup has no listener before initialization; hello/add/status with offline queued state; disconnect-after-write persists accepted intent; restart retains identity/job; shutdown stops further responses/admission | Service/process portion passes. Actual WinUI closed-launch, GUI visible/tray-hidden and UI history rendering unverified; `TRAY` is explicitly a fixture simulation |
| W02 | Ten simultaneous synthetic app processes select one primary listener/nine secondaries. Ten separate client processes with identical request converge to one durable job/GID and no offline engine calls | Pass for shared lease/private-service process topology. No native-host cold launch or real aria2 owner process was created; actual GUI cold-start/engine-owner claim remains unverified |
| W03 | Wrong installation rejected, wrong client executable disconnected without response/job; endpoint factors isolated | Pass for exercised cases. Alternate-user token and remote machine unavailable, so actual cross-user/remote attacks unverified; DACL/remote flag inspected |
| W04 | Existing squatted pipe causes first-instance initialization failure; correct same-user known executable connects, both peer SID and client PID checked; fixture verifies live server PID | Pass on current normal Medium Mandatory Level token. Signature/package identity, browser launch provenance and elevated-to-medium deployment matrix remain later gates |
| W05 | Corrupt authoritative preferences prevent Ready/listener; Package 2 real-SQLite rollback/FULL/postcommit fault regressions rerun | Partial. Physical full disk/runtime ACL failure and original-browser fallback not exercised |

Initial process run was 5/6: denied-peer fixture write could race server disconnect, and prematurely disposed process wrappers masked that assertion during cleanup. Fixture now owns/kills all of its processes until cleanup, and observes denial at either write or read. Subsequent focused run passed 6/6; expanded run passed **55/55 Infrastructure** including **8 bridge cases + 47 Package 2/RPC/upgrade/destination regressions**. Final self-review added a released-lease ownership guard: bridge rerun **9/9**, existing instance-lease regressions **2/2**. No test connects to a personal browser or origin. Temporary profile deletion checks resolved path containment; process timeouts kill only fixture children.

Contracts **94/94**, JavaScript fixture compatibility **23/23** (Node 24.14.1), Application **94/94**. Final Debug solution/app and normal Release app builds passed with zero warnings/errors. A UTF-16 metadata-literal scan finds `browser-bridge-synthetic-peer.txt` in Debug and absent in Release, confirming activation marker compilation is excluded there; this is binary/static evidence, not a Release GUI launch test. TRX artifacts: `artifacts/browser-wp3`. Unsandboxed tools use current Medium Mandatory Level (`S-1-16-8192`), not a UAC elevation. Restricted default shell is Low Mandatory Level; these process/socket tests were run outside that restricted context.

Commands:

```powershell
dotnet test tests/AriaGui.Infrastructure.Tests/AriaGui.Infrastructure.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName~BrowserBridgeProcessTests|FullyQualifiedName~ExternalIntakeTests|FullyQualifiedName~UpgradeTests|FullyQualifiedName~RpcTests|FullyQualifiedName~Retry_does_not_overwrite_an_existing_file_without_a_control_file' --logger trx --results-directory artifacts/browser-wp3
dotnet test tests/AriaGui.Browser.Contracts.Tests/AriaGui.Browser.Contracts.Tests.csproj --no-restore -m:1 -nr:false --logger trx --results-directory artifacts/browser-wp3
node --test tests/AriaGui.Browser.Contracts.Tests/fixtures.test.mjs
dotnet test tests/AriaGui.Application.Tests/AriaGui.Application.Tests.csproj --no-restore -m:1 -nr:false --logger trx --results-directory artifacts/browser-wp3
dotnet build AriaGui.slnx --no-restore -p:Platform=x64 -p:NuGetAudit=false -m:1 -nr:false
```

DNS resolution, redirect/rebinding isolation and actual aria2 origin URL normalization remain unresolved Package 2 gates. Do not enable production browser intake on this package's evidence. Full Infrastructure/Verify, actual GUI native interaction, host authorization/install/signature and deployment/uninstall acceptance are not claimed.
