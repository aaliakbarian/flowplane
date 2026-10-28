# SSIS Provider Integration Testing

The A-10 validation and A-11 discovery integration suites validate FlowPlane against a controlled external SQL Server/SSISDB environment. They are opt-in: ordinary Foundation CI and a normal FlowPlane foundation boot do not require SQL Server or SSIS.

## Safety boundary

- Never point automated provisioning at a production SSIS environment.
- FlowPlane does not create logins, users, or SSISDB permissions.
- Review and run `tests/integration/ssis/provision-test-principals.sql` manually with an appropriate administrative identity.
- Microsoft documents that `catalog.grant_permission` cannot be invoked by a SQL Server-authenticated login, including `sa`.
- Runtime principals remain SQL-authenticated and must not be `sysadmin`, `ssis_admin`, or `db_owner`.
- The provisioning template contains no passwords and expects a dedicated `FlowPlaneA10` folder with a harmless fixture project/package.

The controlled principals are:

- `flowplane_a10_valid`: folder READ, READ_OBJECTS, and EXECUTE_OBJECTS;
- `flowplane_a10_readonly`: folder READ and READ_OBJECTS, without execute permission;
- `flowplane_a10_no_object_permission`: an SSISDB user with no catalog-object grant; and
- optionally, a SQL login with server access but no SSISDB access.

Zero visible objects or permissions is intentionally reported as capability `Unknown`; it is not proof of denial and does not make connectivity validation fail.

The A-11 discovery fixture is maintained separately from the A-10 project so discovery-fixture redeployment cannot erase the representative A-10 evidence. Its source and operator procedure are in [`tests/fixtures/ssis/FlowPlaneA11Fixture`](../../tests/fixtures/ssis/FlowPlaneA11Fixture/README.md). Building, deploying, and configuring that fixture are explicit, separately reviewed test-environment operations; FlowPlane only reads the public catalog views.

The SQL Server 2025 RTM (`17.0.1000.7`) public `catalog.folders` view observed in the controlled environment exposes `folder_id`, not the `id` name currently shown in Microsoft Learn. The A-10 compatibility probe and provisioning template therefore require `folder_id` explicitly; the remaining allowlisted `catalog.projects`, `catalog.packages`, and `catalog.effective_object_permissions` columns match their current Microsoft-documented public view shapes and passed the controlled probe. The observed project and package format versions were `1` and `8`, respectively.

## Local configuration

Copy the non-secret template and protect the populated file:

```bash
cp tests/integration/ssis/.env.example tests/integration/ssis/.env
chmod 600 tests/integration/ssis/.env
```

Populate only the controlled test values. The `.env` file is ignored by Git. Passwords are supplied as distinct environment variables and become `env:` SecretReferences; they are never added to provider configuration JSON.

Leave `FLOWPLANE_TEST_SSIS_A11_ENABLED=false` until the A-11 fixture build, deployment, catalog configuration, and read-only verification have all succeeded. A gated or skipped A-11 run is not acceptance evidence.

FlowPlane resolves each password only for the bounded validation operation and never persists, returns, or logs the generated connection string. `Microsoft.Data.SqlClient` still requires an immutable connection-string value internally, so neither FlowPlane nor this test path claims perfect managed-memory zeroization; disposal bounds the usable connection lifetime but cannot erase every immutable string copy.

The secure default is certificate validation:

```text
FLOWPLANE_TEST_SSIS_TRUST_SERVER_CERTIFICATE=false
```

Install the SQL Server certificate's issuing CA in the WSL/container trust store. Set the value to `true` only for an explicit Development test exception; production validation rejects that configuration before resolving the secret or opening a connection.

## WSL test path

Load the ignored values into the current shell without printing them, then run:

```bash
set -a
. tests/integration/ssis/.env
set +a
dotnet test tests/integration/FlowPlane.Ssis.Integration.Tests/FlowPlane.Ssis.Integration.Tests.csproj --nologo -m:1
```

Unset the variables or close the shell after testing. The suite skips individual roles that are not configured.

## Containerized Linux test path

When the SQL Server endpoint is reachable from Docker, run the same suite in the repository SDK image:

```bash
docker run --rm \
  --user "$(id -u):$(id -g)" \
  --env HOME=/tmp/flowplane-home \
  --env DOTNET_CLI_HOME=/tmp/flowplane-home \
  --env NUGET_PACKAGES=/tmp/flowplane-nuget \
  --env-file tests/integration/ssis/.env \
  --mount type=bind,source="$PWD",target=/src \
  --workdir /src \
  mcr.microsoft.com/dotnet/sdk:10.0.402-noble \
  dotnet test tests/integration/FlowPlane.Ssis.Integration.Tests/FlowPlane.Ssis.Integration.Tests.csproj --nologo -m:1
```

Use a DNS name or address reachable from the container; `127.0.0.1` inside the container is the container itself. The production backend image intentionally contains no SDK or test runner, so this controlled SDK-container path exercises the same Linux/SqlClient network stack without changing the runtime image.

## Compatibility evidence

The controlled A-10 matrix passed all six scenarios against SQL Server 2025 RTM (`17.0.1000.7`): READ plus EXECUTE, read-only, no catalog-object permission, no SSISDB access, invalid SQL password, and TLS certificate rejection without downgrade. The suite passed both from native WSL and from the non-root `mcr.microsoft.com/dotnet/sdk:10.0.402-noble` Linux container.

This evidence promotes SQL Server major 17 to `Supported` only for the implemented A-10 connection, authentication, public-catalog validation, and capability-assessment boundary. All SQL Server 17.x builds are eligible for that validation path subject to its live version and public-catalog compatibility checks; the representative environment independently verified so far is SQL Server 2025 RTM `17.0.1000.7`, not other 17.x builds. SQL Server 2019 and 2022 remain recognized but `Unverified`. Package discovery, execution, cancellation, monitoring, deployment, ISPAC retrieval, DTSX parsing, graph projection, and unobserved definition formats remain outside this compatibility claim. A-11 owns discovery behavior; execution and the other deferred operations require their own representative evidence.

## A-11 discovery contract

One scan reads all currently visible folders/projects and all approved current package, parameter and environment-reference metadata through documented public views. It never selects default values, environment-variable values, execution parameter values, ISPACs, DTSX content or historical object versions. Non-entry-point packages and unknown-format definitions remain observations; only recognized entry-point packages receive executable candidate references.

The Application capability is `ScanAsync(ProviderInstance, IProviderDiscoverySink, CancellationToken)`. Use one invocation-scoped sink per refresh; do not mix concurrent scans. Each awaited `AppendAsync` delivers one provisional provider-qualified observation with an opaque native key, approved versioned metadata document and optional executable reference. Provisional observations are not completed results. A-11b may publish only after durable sink success and a verified terminal summary with `ScanCompleted = true`, `Outcome` equal to `Succeeded` or `Partial`, and `SourceCompatibility = Compatible`, with its own configuration/access-context fencing. A failed scan may already have delivered provisional rows; discard them. Cancellation, timeout, exceptions or process termination never authorize publication. A-11a does not persist inventory or implement A-11b publication.

The scanner spools a sequential project manifest in owner-only temporary storage. On Linux x64/arm64, `O_TMPFILE | O_EXCL | O_CLOEXEC` creates a mode-0600 inode with no pathname at any initialization stage; only the open handle is used, never a filename reopen. Closing the last descriptor, including after `SIGKILL`, releases it. No scavenger or named-file fallback is used. The temporary filesystem must support `O_TMPFILE`; otherwise discovery fails safely without preventing ordinary application boot. Windows uses an explicit owner-only ACL and kernel delete-on-close. Other native Unix platforms are not currently supported by this spool implementation. Temp storage needs space for the project manifest; failure never becomes complete coverage. Previously abandoned files from older implementations are not automatically deleted, because their ownership/liveness cannot safely be inferred from a filename.

Rows are written directly without a buffered text writer; abandoning a scan does not flush or retry provisional writes. Constructor failure closes owned storage, and caller cancellation retains precedence if cleanup also fails. Tests cover real Linux `ENOSPC` via `/dev/full`, injected write/flush/close failures, and abrupt subprocess termination. These checks do not claim protection against filesystem/kernel/hardware faults or hostile process compromise; Windows lifecycle/ACL tests require a native Windows run.

The scanner reads project IDs in internal batches, streams metadata rows with backpressure, checks project revisions around each SQL read, compares a second metadata read and rechecks the final manifest. It retains only bounded transport buffers, batch context and fingerprints in memory. A large project is never collected into one JSON document. These checks detect observed changes, not every concurrent change or change-and-reversion race; they do not establish a transactionally consistent SSISDB snapshot or change SSISDB isolation configuration.

### A-11 discovery configuration

The provider-owned `FlowPlane:Ssis` configuration section has these defaults and inclusive safe ranges:

| Setting | Environment key | Default | Range |
| --- | --- | ---: | ---: |
| `MaxProjectsPerBatch` | `FlowPlane__Ssis__MaxProjectsPerBatch` | 20 | 1-100 |
| `ConnectionTimeoutSeconds` | `FlowPlane__Ssis__ConnectionTimeoutSeconds` | 5 | 1-60 |
| `CommandTimeoutSeconds` | `FlowPlane__Ssis__CommandTimeoutSeconds` | 15 | 1-600 |
| `OverallTimeoutSeconds` | `FlowPlane__Ssis__OverallTimeoutSeconds` | 1,800 | 1-86,400 |

`OverallTimeoutSeconds` must be at least both operation timeouts. The batch ceiling bounds SQL parameter count and per-batch context, not the total catalog. There is no candidate, parameter or reference cap, SSISDB pagination, truncation flag or zero-as-unlimited behavior. Invalid numeric values and the retired `MaxCandidates`, `MaxParametersPerCandidate` and `MaxEnvironmentReferencesPerProject` configuration keys fail startup clearly. Remove retired settings from local configuration. Counters use checked 64-bit arithmetic. Query/resource/time failures leave coverage incomplete rather than silently returning less metadata.

Compose maps the operator-facing `FLOWPLANE_SSIS_*` variables in `deploy/compose/.env` to those standard ASP.NET Core environment keys. The settings are backend/operator configuration only: they are not stored in ProviderInstance JSONB, and they do not add per-request pagination or filtering. Leaving the defaults unchanged still permits FlowPlane to boot without any external SQL Server or SSISDB.

Scan completeness, source compatibility and per-definition format/eligibility are separate. A completed scan containing incompatible definitions is `Partial` with complete coverage and preserved observations. An unread project, revision/manifest conflict, sink failure or timeout never completes a scan. Caller cancellation propagates; an operation-owned timeout returns sanitized `Unavailable`, without retries. A permission-filtered empty result is `Succeeded`. Refresh bindings and references even when the active LSN is unchanged. A-12's [durable hosted runner](a12-stage2-discovery.md) owns long-operation HTTP admission and observation.

`NativeDefinitionReference` identity consists of provider type, ProviderInstance ID, schema `flowplane.ssis/native-definition-reference.v1`, and the exact folder/project/package names. Catalog IDs, GUIDs, deployment timestamps, and object-version LSNs are revision observations in provider metadata, not stable identity. Names retain their original Unicode and exact ordinal identity in FlowPlane. Live catalog lookups follow SQL Server's actual collation; FlowPlane's ordinal equality does not override that collation or normalize native names.

The A-11 fixture suite contains six scenarios: least-privilege READ/EXECUTE, read-only, no visible object permission, invalid password, no SSISDB access, and TLS rejection without downgrade. The positive cases require the source-controlled fixture to prove entry-point eligibility, preservation of the non-entry-point observation, project/package parameter association, a relative environment reference, and value redaction. Run the same WSL and container commands above after enabling the reviewed fixture gate. Acceptance requires six passed, zero failed, and zero skipped A-11 fixture tests in each path; the A-10 six-test matrix remains a separate regression contract. The controlled fixture was separately approved/deployed and passed those WSL/container gates; the [live acceptance record](milestones/milestone-a-live-acceptance.md) adds real API/UI/publication evidence. A new environment without that reviewed fixture must still report fixture-dependent acceptance as Blocked, never Passed through skips.

`RealSsisScanContractTests` separately exercises the already deployed controlled catalog with batch size 1. That read-only smoke check can run before the A-11 fixture is deployed; it cannot replace the six fixture acceptance cases. No scan modifies external SSISDB or grants permissions.
