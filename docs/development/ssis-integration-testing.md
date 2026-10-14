# SSIS Provider Integration Testing

The A-10 integration suite validates FlowPlane against a controlled external SQL Server/SSISDB environment. It is opt-in: ordinary Foundation CI and a normal FlowPlane foundation boot do not require SQL Server or SSIS.

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

The SQL Server 2025 RTM (`17.0.1000.7`) public `catalog.folders` view observed in the controlled environment exposes `folder_id`, not the `id` name currently shown in Microsoft Learn. The A-10 compatibility probe and provisioning template therefore require `folder_id` explicitly; the remaining allowlisted project, package, and effective-permission columns match their current Microsoft-documented public view shapes and remain subject to the controlled probe. SQL Server 2025 remains `Unverified` until the complete controlled matrix passes.

## Local configuration

Copy the non-secret template and protect the populated file:

```bash
cp tests/integration/ssis/.env.example tests/integration/ssis/.env
chmod 600 tests/integration/ssis/.env
```

Populate only the controlled test values. The `.env` file is ignored by Git. Passwords are supplied as distinct environment variables and become `env:` SecretReferences; they are never added to provider configuration JSON.

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
  --env-file tests/integration/ssis/.env \
  --mount type=bind,source="$PWD",target=/src \
  --workdir /src \
  mcr.microsoft.com/dotnet/sdk:10.0.401-noble \
  dotnet test tests/integration/FlowPlane.Ssis.Integration.Tests/FlowPlane.Ssis.Integration.Tests.csproj --nologo -m:1
```

Use a DNS name or address reachable from the container; `127.0.0.1` inside the container is the container itself. The production backend image intentionally contains no SDK or test runner, so this controlled SDK-container path exercises the same Linux/SqlClient network stack without changing the runtime image.

## Completion evidence

A-10 remains open until the least-privilege technical path and representative wrong-password, TLS, catalog-access, permission, and recognized-version cases have run in a controlled real SSISDB environment. Until that evidence is accepted, SQL Server 2019, 2022, and 2025 are reported as `Unverified`, and the integration suite expects the support gate to remain failed while independently verifying catalog and capability behavior. Promoting a tested major to `Supported` requires an explicit compatibility-policy and test change backed by the captured evidence. Unit tests and skipped integration tests are not sufficient closure evidence.
