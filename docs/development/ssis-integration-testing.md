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

The SQL Server 2025 RTM (`17.0.1000.7`) public `catalog.folders` view observed in the controlled environment exposes `folder_id`, not the `id` name currently shown in Microsoft Learn. The A-10 compatibility probe and provisioning template therefore require `folder_id` explicitly; the remaining allowlisted `catalog.projects`, `catalog.packages`, and `catalog.effective_object_permissions` columns match their current Microsoft-documented public view shapes and passed the controlled probe. The observed project and package format versions were `1` and `8`, respectively.

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
