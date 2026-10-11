# Browser contracts v1 — Package 1

Implemented 2026-10-11 in `src/AriaGui.Browser.Contracts`. This library references Core only; it has no WinUI, EF, engine, browser, registry or network dependency. Package 0 remains separate and uncommitted. Package 1 does not implement host/app intake, trusted caller context, readiness, durable receipts, deployment or browser integration.

## Closed wire schema

The published structural schema is `src/AriaGui.Browser.Contracts/schema/v1.schema.json`. Use `ContractJson.ParseRequest`, `ParseResponse` and `Serialize` for normative validation/serialization; stock JsonSerializer or JSON Schema alone is insufficient. DTOs and named enums are in `Contracts.cs`. Explicit wire names are emitted by `ContractJson`, not CLR enum names.

Requests require `protocol`, `requestId`, `action`, `payload`. Responses require `protocol`, `requestId`, `result`. Every object rejects unknown and duplicate decoded property names, including escaped duplicates. Names are case-sensitive; comments, trailing commas, BOM, invalid UTF-8, malformed JSON and depth beyond 8 reject. Body size is 1–65536 UTF-8 bytes. UUIDs must be nonzero, lowercase canonical D strings. Protocol major is 1; minor is a nonnegative Int32. Higher minor values are accepted only with the same closed v1 fields. The sole optional field is `suggestedFileName`; explicit null is rejected. Additional field allowlisting requires updating both endpoints and fixtures.

| Action | Payload fields |
|---|---|
| `hello` | installationId, extensionVersion |
| `addPublicGet` | installationId, createdAt, sourceUrl, trigger; optional suggestedFileName |
| `getRequestStatus` | installationId, targetRequestId |

Versions are bounded to 64 characters with numeric three-part and optional suffix syntax. `createdAt` uses UTC `YYYY-MM-DDTHH:mm:ss[.1–7 digits]Z` and must be a valid date. Typed serialization emits UTC with seven fractional digits. Trigger is only `linkContextMenu`. Timestamp age/ledger lookup order belongs to durable intake in Package 2, not this parser.

Source URL is at most 16384 UTF-8 bytes. Only absolute HTTP(S) with a host and no user-info is accepted. Raw controls/whitespace/backslashes, malformed percent triplets, encoded ASCII controls, unsupported schemes and invalid ports reject. Fragment is removed; the remaining input string is preserved exactly, including query order, percent escape case and signed query encoding. No Uri reserialization, percent decoding or query normalization occurs.

This is syntactic contract validation, not network admission. The parser currently permits syntactically valid loopback/link-local/private IPv4 and IPv6 literals. Boundary tests make that behavior explicit without making a request. **Package 2 authoritative intake must reject those literals by default before acceptance**, as required by the architecture. Do not connect this parser directly to engine dispatch or advertise it as guaranteeing public-network targets. DNS resolution, redirects and rebinding require transport-level enforcement; this library performs none and supplies no SSRF isolation guarantee.

Filename uses the shared pure `WindowsFileNamePolicy` in Core, also used by FileDownloadDestination: 1–240 UTF-16 code units, a single basename, Windows invalid characters/control characters forbidden, no trailing dot/space or reserved devices including superscript COM/LPT variants. Parser does not trim or normalize a name. Existing manual destination trimming/fallback behavior is unchanged. JSON Schema's string constraints are structural; Windows reserved-device, UTF-16 length, actual date, URL safety and UTF-8 byte rules additionally require the parser.

| Result code | Required result fields besides code |
|---|---|
| `ready` | appVersion, capabilities (unique subset of addPublicGet/getRequestStatus), maxMessageBytes (1–65536) |
| `accepted` | jobId, state, duplicate (boolean) |
| `rejected` | reason (closed rejection enum) |
| `processing` | retryAfterMs (1–10000) |
| `not_found`, `expired`, `unavailable`, `unknown` | none |

Job intake states are queued, active, paused, completed, failed, interrupted and engine_uncertain. Rejection reasons are unsupported_protocol, invalid_request, unsupported_url, invalid_filename, destination_conflict, destination_unavailable, integration_disabled, not_ready, shutting_down, busy, storage_unavailable and request_id_conflict. Accepted is a future durable receipt contract, not evidence that this library creates work or transfers bytes.

Parse failures contain only a safe enum. A correlation UUID is retained only after the complete JSON document passes UTF-8, syntax/depth and recursive duplicate checks and its requestId is canonical. A caller may send a v1 rejected result with that ID; when it is null, close without echoing raw input. Major mismatch yields unsupported_protocol when correlation is trustworthy. Frame errors have fixed messages and no payload/correlation. No arbitrary raw exception text is part of the wire contract.

## Framing

`FrameConnection` takes a caller-owned cancellable async Stream and explicit transport. Browser native messaging is Windows x64 native-order (little endian); pipe framing is explicitly little endian. Both use a four-byte unsigned body length followed by exact UTF-8 bytes. Validate nonzero/max length before body allocation. Reads handle fragmented headers/bodies, concatenation, clean EOF before a header and truncated EOF during a frame. JSON parsing is a separate step after the frame.

One FrameConnection instance per duplex stream serializes reads and writes independently. Writers snapshot bounded input before waiting, validate that snapshot, and hold one gate across header, body and flush. Default deadline is a **five-second whole-frame wall-clock budget**, including waiting for the gate; it is stricter than an idle timer and prevents drip feeding. TimeProvider and cancellation are injectable. Deadline, partial cancellation or I/O/framing failure faults the connection; caller must close its stream and cannot resume framing. Invalid outbound data rejected before any write leaves the connection usable. Clean EOF returns null. The stream must honor async cancellation; the library does not claim it can forcibly stop an arbitrary noncooperative Stream. Queue/session/admission limits and connect/cold-start deadlines belong to later services.

## Tests and verification

`tests/AriaGui.Browser.Contracts.Tests/fixtures/v1.json` is shared by C# and dependency-free JavaScript tooling. Fixtures include all request/result shapes, non-ASCII signed URL/name, major mismatch, unsupported action, forbidden headers, Windows device name, user-info and duplicates. C# round-trips through the actual library; JS round-trips through isolated compatibility tooling and checks the published structural schema. JS tooling is not an extension implementation or a general JSON Schema validator.

Commands:

```powershell
dotnet restore AriaGui.slnx -p:Platform=x64 --locked-mode -p:NuGetAudit=false -m:1 -nr:false
dotnet build AriaGui.slnx --no-restore -p:Platform=x64 -m:1 -nr:false
dotnet test tests/AriaGui.Browser.Contracts.Tests/AriaGui.Browser.Contracts.Tests.csproj --no-restore --logger trx --results-directory artifacts/browser-wp1
node --test tests/AriaGui.Browser.Contracts.Tests/fixtures.test.mjs
```

Node.js **22+** is now a verification prerequisite, checked with a clear error at Verify.ps1 entry. Tested Node 24.14.1. Tests themselves use only fixture files, memory streams, injected timers and local assertions; no browser/process/network/registry actions. VSTest runner needs its ordinary local testhost socket permissions. Locked restore and serialized solution build passed with zero warnings/errors outside the restricted context; restricted solution restore exited 1 without diagnostics.

Current evidence: Contracts **93/93**, JS **22/22**, Core **16/16**, Application **94/94**, targeted Infrastructure **4/4** (Preferences, HistorySorting and retry preservation). TRX artifacts are under `artifacts/browser-wp1`. C01–C05 are covered at parser/stream level, including adversarial input, byte rather than character lengths, exact/partial/concatenated framing, injected read/write deadlines, cancellation and concurrent writes. The complete Verify.ps1/full Infrastructure/package acceptance was not rerun; Verify wiring was inspected and its relevant new commands run directly. No host/app integration or deployment acceptance is implied.
