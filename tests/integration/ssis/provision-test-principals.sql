/*
  A-10 CONTROLLED TEST ENVIRONMENT TEMPLATE ONLY.

  Review every value before execution. Run manually with an administrative identity
  that can assign SSIS catalog permissions. catalog.grant_permission cannot be
  invoked by a SQL Server-authenticated login, including sa.

  This script does not create server logins and contains no passwords. A DBA must
  create the three SQL-authenticated logins separately before running it.
*/

:setvar ValidLogin "flowplane_a10_valid"
:setvar ReadOnlyLogin "flowplane_a10_readonly"
:setvar NoObjectPermissionLogin "flowplane_a10_no_object_permission"
:setvar TestFolder "FlowPlaneA10"

USE [SSISDB];
GO

IF USER_ID(N'$(ValidLogin)') IS NULL
    CREATE USER [$(ValidLogin)] FOR LOGIN [$(ValidLogin)];
IF USER_ID(N'$(ReadOnlyLogin)') IS NULL
    CREATE USER [$(ReadOnlyLogin)] FOR LOGIN [$(ReadOnlyLogin)];
IF USER_ID(N'$(NoObjectPermissionLogin)') IS NULL
    CREATE USER [$(NoObjectPermissionLogin)] FOR LOGIN [$(NoObjectPermissionLogin)];
GO

DECLARE @folder_id bigint = (
    SELECT [folder_id]
    FROM [catalog].[folders]
    WHERE [name] = N'$(TestFolder)');

IF @folder_id IS NULL
    THROW 50000, 'Create the dedicated FlowPlaneA10 folder and fixture project before granting permissions.', 1;

DECLARE @valid_principal int = DATABASE_PRINCIPAL_ID(N'$(ValidLogin)');
DECLARE @readonly_principal int = DATABASE_PRINCIPAL_ID(N'$(ReadOnlyLogin)');

EXEC [catalog].[grant_permission] 1, @folder_id, @valid_principal, 1;
EXEC [catalog].[grant_permission] 1, @folder_id, @valid_principal, 101;
EXEC [catalog].[grant_permission] 1, @folder_id, @valid_principal, 103;

EXEC [catalog].[grant_permission] 1, @folder_id, @readonly_principal, 1;
EXEC [catalog].[grant_permission] 1, @folder_id, @readonly_principal, 101;
GO
