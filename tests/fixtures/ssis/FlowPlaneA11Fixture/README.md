# FlowPlane A-11 SSIS discovery fixture

This project-deployment-model fixture is deliberately harmless: both packages have empty control flows and no external side effects. It is a separate `FlowPlaneA11Fixture` project in the existing controlled `FlowPlaneA10` folder so redeployment cannot overwrite the A-10 validation fixture.

The source contract is:

- `EntryPoint.dtsx` is explicitly marked as an entry point and has the non-sensitive package parameter `BatchSize`;
- `InternalStep.dtsx` is explicitly marked as a non-entry point;
- `RequiredProjectInput` is required and non-sensitive;
- `SensitiveEnvironmentInput` is required and sensitive, with no source-controlled value; and
- `configure-catalog.sql` adds a relative `FlowPlaneA11Environment` reference and maps the sensitive project parameter to `FlowPlaneA11SensitiveValue`.

The catalog canary is a published non-credential string used only to prove that discovery never retrieves environment values. No real secret or build artifact belongs in this directory.

## Build

Microsoft documents `SSISBuild.exe` as the standalone project-deployment build tool. From WSL, with the [standalone SSIS DevOps Tools](https://learn.microsoft.com/sql/integration-services/devops/ssis-devops-standalone?view=sql-server-ver17) extracted under `C:\tools\ssis-devops`, run:

```bash
/mnt/c/tools/ssis-devops/SSISBuild.exe \
  -p:"$(wslpath -w "$PWD/tests/fixtures/ssis/FlowPlaneA11Fixture/FlowPlaneA11Fixture.dtproj")" \
  -c:Development \
  -o:"$(wslpath -w /tmp/flowplane-a11-ssis-build)"
```

The documented standalone GA tool version `1.0.0.0` was verified against this checked-in source: the consistency check passed and produced an ISPAC with project format `1`, package format `8`, and both entry-point flags intact. Visual Studio graphical tooling is therefore not required for the normal fixture build. If a later Microsoft toolchain stops accepting the fixture, the current Microsoft SQL Server Integration Services Projects tooling is the supported fallback build engine; that fallback must be verified before use.

Do not commit the generated `bin/` directory or ISPAC.

## Deployment and catalog setup

Deployment is an explicit test-environment operation, never FlowPlane application behavior. Review the generated ISPAC, then deploy it with Windows authentication using standalone `SSISDeploy.exe`:

```bash
/mnt/c/tools/ssis-devops/SSISDeploy.exe \
  -s:"$(wslpath -w /tmp/flowplane-a11-ssis-build/Development/FlowPlaneA11Fixture.ispac)" \
  '-d:catalog;/SSISDB/FlowPlaneA10/FlowPlaneA11Fixture;<controlled-server>' \
  -at:win
```

Do not use the tool's SQL password command-line option; it exposes credentials in process arguments and shell history. The installed `ISDeploymentWizard.exe` with Windows authentication is an acceptable Microsoft-supported deployment fallback.

After separately reviewing the mutation, run `configure-catalog.sql` with an administrative Windows identity capable of changing SSIS catalog environments and project configuration. Then run the read-only `verify-catalog.sql`. The verification must show `EntryPoint.dtsx` with `entry_point = 1` and `InternalStep.dtsx` with `entry_point = 0`; filenames alone are not evidence.

The setup joins `catalog.folders.folder_id` to `catalog.environments.folder_id`, then joins `catalog.environment_variables.environment_id`. Folder/environment names are not columns of the variables view. Existing environments must carry the fixture description before this script will reuse them; incompatible variable metadata fails clearly. The script is rerunnable for these dedicated objects but does not promise atomic rollback of earlier setup steps if a later catalog procedure fails. Review any failure before rerunning. It does not grant permissions, select variable values or echo the canary. Catalog lookups follow the server's collation. The read-only verification additionally checks the variable's type and sensitive flag without reading its value.

The column/parameter static tests use the public SQL Server 2025 contracts for [environment variables](https://learn.microsoft.com/sql/integration-services/system-views/catalog-environment-variables-ssisdb-database?view=sql-server-ver17), [environment references](https://learn.microsoft.com/sql/integration-services/system-stored-procedures/catalog-create-environment-reference-ssisdb-database?view=sql-server-ver17), [creating a variable](https://learn.microsoft.com/sql/integration-services/system-stored-procedures/catalog-create-environment-variable-ssisdb-database?view=sql-server-ver17), and [setting parameter bindings](https://learn.microsoft.com/sql/integration-services/system-stored-procedures/catalog-set-object-parameter-value-ssisdb-database?view=sql-server-ver17), plus the A-10-observed `folder_id` column. Static validation is not live setup evidence; no external fixture mutation is authorized by passing it.

Only after build, deployment, configuration, and verification succeed should the ignored integration-test configuration set:

```text
FLOWPLANE_TEST_SSIS_A11_ENABLED=true
```

The six A-11 real integration tests must then pass from both native WSL and the patched non-root SDK container. A missing or unverified fixture is a blocked acceptance run, not a passing or skipped acceptance result.
