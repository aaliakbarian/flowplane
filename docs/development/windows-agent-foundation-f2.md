# Windows Agent Foundation F2: local service and evidence review

F2 implements local primitives only, following [ADR-0007](../adr/0007-windows-agent-security-transport-and-durable-operations.md) and the [approved foundation plan](../superpowers/plans/2026-11-01-windows-agent-foundation.md). It does not implement agent HTTPS, enrollment, polling, database persistence, SSIS adapters or native execution. `Pair` returns `PairingUnavailable`; this is not enrollment success. `ReadyForLocalSetup` is neither control-plane authorization nor SQL execution readiness.

## Boundaries and builds

- `FlowPlane.slnx` builds/tests portable Runtime and Protocol, plus existing backend projects. Runtime references Protocol only and adds no packages. The synthetic JournalProbe is a test-only process, never a distributed agent.
- `FlowPlane.Agent.Windows.slnx` separately cross-builds the Windows Worker, Setup helper and Windows tests. The two Microsoft hosting packages are pinned to 10.0.13. Windows test compilation does not count as Windows-native test execution. The explicitly filtered `PortableBoundary` initialization-fault test can run in WSL using injected local I/O/token boundaries; it proves no real Windows token, CNG, SCM or ACL behavior.
- The Worker only runs as an SCM service under `NT SERVICE\FlowPlaneAgent`. It rejects console mode and unexpected arguments, does not consume environment/command-line configuration, and does not expose an execution callback. Logging providers are removed rather than exposing raw Windows/CNG/IPC errors.
- The separate B-02 service-token probe can later reuse the token, identity and journal primitives under separately reviewed test-host approval. F2 does not reference SqlClient, B-07 or B-08.

Build-only verification, not installation authorization:

```sh
dotnet build FlowPlane.slnx --configuration Release --no-restore -warnaserror
dotnet build FlowPlane.Agent.Windows.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/unit/FlowPlane.Agent.Tests/FlowPlane.Agent.Tests.csproj --configuration Release --no-restore --nologo -m:1
dotnet test tests/unit/FlowPlane.Architecture.Tests/FlowPlane.Architecture.Tests.csproj --configuration Release --no-restore --nologo -m:1
```

## Local identity and setup

The intended binary location is `%ProgramFiles%\FlowPlaneAgent\<canonical-version>\`; mutable state is `%ProgramData%\FlowPlaneAgent\`. The reviewed local Setup verbs are `install`, `uninstall`, `initialize`, `status`, and `pair`. There is no arbitrary executable/SQL/script command. Code/address input for pairing is interactive, never a command argument. The pasted code is hidden and transient; it is neither journaled nor logged. F2 does not transmit it to a backend.

Installation requires an elevated administrator and a separately authorized operational window. It rejects an existing service or data directory, validates quoted/versioned binary placement and restrictive ownership/ACLs, creates only the fixed local SCM service/account, enables its service SID, then creates protected state and an explicit installation marker. It does not start the service, change SQL rights, or silently overwrite a partial installation. Failed installation may leave partial local state requiring review.

An explicit local `initialize` request under the actual service token creates a machine CNG RSA-3072 signing key, non-exportable private material and a 90-day non-CA clientAuth certificate. TPM provider is preferred when available; software keys retain the documented privileged-host cloning limitation. An unavailable/failed TPM key operation is not silently retried with another identity. Public DER and installation/provider facts are stored locally; no PFX/private-key export is implemented. Normal startup only opens existing identity, checks ACL/profile/public-key consistency and performs a local signing proof. Missing/corrupt/expired identity or journal never causes replacement-key generation. Interrupted initialization requires recovery review, not blind reinitialization.

The fixed named pipe uses a protected Administrators/System DACL, `FILE_FLAG_FIRST_PIPE_INSTANCE` and `PIPE_REJECT_REMOTE_CLIENTS`. One server handle normally remains owned across connections. Every accepted connection is reset, including a broken client. If reset itself reports a recognized disconnect, its handle is disposed and the same exclusive-name/ACL factory must succeed again; failure stops the listener rather than adopting another server's pipe. The service impersonates and verifies the actual caller token. The helper verifies the server process token's exact service SID before sending pairing input. Frames have a 4-KiB body limit, strict versioned JSON and a 10-second client deadline. The server's entire request/response/acknowledgement exchange has a separate 10-second deadline. After receipt serialization/write/flush, it waits for one transport acknowledgement byte (`0x06`), sent only after the helper has read and validated the receipt. Flush alone is not client-consumption evidence. The acknowledgement grants no authority and is not durable operation evidence. Missing/invalid acknowledgement or recognized client disconnect never replays the request; unexpected infrastructure/security errors stop the listener with a sanitized error. After an uncertain initialization response, use explicit read-only `status`, not automatic `initialize`/`pair` retries or replacement-key generation. F2 pairing remains unavailable; future enrollment/native recovery must use its durable receipts/journal. Wrong-name, ordinary-user, remote-client and token-spoofing denial still require actual Windows proof.

Uninstall requires the exact expected service configuration, stopped service and healthy settled journal. It deletes only the service registration, never key/certificate/journal files. It refuses unknown or unsettled evidence; it does not cancel SQL, release Core capacity or erase uncertainty. Reinstall/partial-install repair is deliberately not automatic. No cleanup command to delete evidence is provided.

## Journal semantics and limits

The installation-qualified journal is protected by a separate exclusive ownership handle retained across compaction. Its header and fixed 186-byte, versioned, length-delimited frames carry a monotonic sequence, typed operation key, one of the five F1 evidence phases, typed result/receipt facts and a SHA-256 chain. There is no arbitrary metadata/diagnostic/value payload. Native Int64 IDs are stored in full-width binary and never converted to floating point.

Each acknowledgement follows `WriteThrough` and `Flush(true)`. Exact duplicate phase/body delivery recovers the immutable recorded evidence; changed keys/results or reordered phases fail. Receipts are evidence, not renewed permissions to invoke SQL. Any returned pre-existing `MayHaveInvoked` fact requires reconciliation by the future execution coordinator, never another invocation. A failed write/flush/replace poisons that live journal instance. A truncated/corrupt tail, wrong installation, missing expected file, or leftover compaction candidate fails closed without truncation or repair.

Capacity is bounded by 64 MiB and 4,096 operation IDs, with follow-up space reserved at acceptance. Full storage rejects new work; it never evicts unresolved records. Compaction preserves all unresolved phase receipts and one terminal tombstone per settled operation. Earlier receipts of a compacted settled operation reject as `Settled` rather than re-admit. Tombstone pruning is not implemented; reaching the operation limit requires a separately reviewed retention decision. Hashes detect corruption, not a malicious administrator with write authority.

Compaction flushes a unique same-directory replacement before rename, while retaining the ownership lock. A replacement failure preserves candidates and blocks that instance; restart refuses a leftover candidate. POSIX subprocess-kill tests prove the observed flushed-file behavior, not power-loss durability of Windows/NTFS, hardware caches or directory entries. Windows rename/flush and actual power/process-fault acceptance remain mandatory before runtime support is claimed.

## Platform and packaging gates

Cross-publish may produce separate win-x64 self-contained service/helper directories outside Git. It executes no published Windows application. F6 owns the merged downloadable archive, signed/integrity manifest, UI download and final installer UX; no SQL probe or test helper belongs in that archive. Development output is unsigned and is not production publisher authentication.

Not executed by WSL-only verification:

1. Real SCM registration/start/stop/uninstall and actual service SID/token/group inspection.
2. Private CNG creation/use, non-exportability, TPM/software selection and exact returned key DACL under the intended virtual account.
3. Ordinary-user/remote/impersonated setup denial and pipe-server identity validation on Windows.
4. Runtime denied writes to Program Files/configuration, permitted private-state access and adversarial file/key ACL tests.
5. Windows process/power loss, disk-full/write/flush/rename failures and uninstall/reinstall preserving unresolved evidence.
6. Interactive versus actual Windows Service-token SQL Integrated Authentication and separately approved B-02 execution proof.

These tests require separate isolated Windows operational approval. They are pending, not successful skips. No migration, key provisioning on the actual Windows host, service installation, ACL/grant change, SSIS operation or production readiness claim is authorized by this document.
