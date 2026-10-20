:setvar SQLCMDERRORLEVEL 1
:on error exit

USE [SSISDB];
GO

SET NOCOUNT ON;

DECLARE @folder_name nvarchar(128) = N'FlowPlaneA10';
DECLARE @project_name nvarchar(128) = N'FlowPlaneA11Fixture';
DECLARE @project_id bigint;

SELECT @project_id = [project].[project_id]
FROM [catalog].[projects] AS [project]
INNER JOIN [catalog].[folders] AS [folder]
    ON [folder].[folder_id] = [project].[folder_id]
WHERE [folder].[name] = @folder_name
  AND [project].[name] = @project_name
  AND [project].[project_format_version] = 1;

IF @project_id IS NULL
    THROW 50000, 'The compatible FlowPlaneA11Fixture project is not deployed.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[packages]
    WHERE [project_id] = @project_id
      AND [name] = N'EntryPoint.dtsx'
      AND [entry_point] = 1
      AND [package_format_version] = 8
)
    THROW 50001, 'EntryPoint.dtsx is not a compatible executable entry point.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[packages]
    WHERE [project_id] = @project_id
      AND [name] = N'InternalStep.dtsx'
      AND [entry_point] = 0
      AND [package_format_version] = 8
)
    THROW 50002, 'InternalStep.dtsx is not a compatible non-entry-point package.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[object_parameters]
    WHERE [project_id] = @project_id
      AND [object_type] = 20
      AND [parameter_name] = N'RequiredProjectInput'
      AND [required] = 1
      AND [sensitive] = 0
)
    THROW 50003, 'RequiredProjectInput metadata is missing or incompatible.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[object_parameters]
    WHERE [project_id] = @project_id
      AND [object_type] = 20
      AND [parameter_name] = N'SensitiveEnvironmentInput'
      AND [required] = 1
      AND [sensitive] = 1
      AND [value_type] = 'R'
      AND [referenced_variable_name] = N'FlowPlaneA11SensitiveValue'
)
    THROW 50004, 'SensitiveEnvironmentInput is not configured by environment reference.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[object_parameters]
    WHERE [project_id] = @project_id
      AND [object_type] = 30
      AND [object_name] = N'EntryPoint.dtsx'
      AND [parameter_name] = N'BatchSize'
      AND [required] = 0
      AND [sensitive] = 0
)
    THROW 50005, 'EntryPoint.dtsx package parameter metadata is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[environment_references]
    WHERE [project_id] = @project_id
      AND [reference_type] = 'R'
      AND [environment_folder_name] IS NULL
      AND [environment_name] = N'FlowPlaneA11Environment'
)
    THROW 50006, 'The controlled relative environment reference is missing.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[environment_variables] AS [variable]
    INNER JOIN [catalog].[environments] AS [environment]
        ON [environment].[environment_id] = [variable].[environment_id]
    INNER JOIN [catalog].[folders] AS [folder]
        ON [folder].[folder_id] = [environment].[folder_id]
    WHERE [folder].[name] = @folder_name
      AND [environment].[name] = N'FlowPlaneA11Environment'
      AND [variable].[name] = N'FlowPlaneA11SensitiveValue'
      AND [variable].[type] = N'String'
      AND [variable].[sensitive] = 1
)
    THROW 50007, 'The controlled sensitive environment-variable metadata is missing.', 1;

SELECT
    [folder].[name] AS [folder_name],
    [project].[name] AS [project_name],
    [package].[name] AS [package_name],
    [package].[entry_point],
    [project].[project_format_version],
    [package].[package_format_version]
FROM [catalog].[folders] AS [folder]
INNER JOIN [catalog].[projects] AS [project]
    ON [project].[folder_id] = [folder].[folder_id]
INNER JOIN [catalog].[packages] AS [package]
    ON [package].[project_id] = [project].[project_id]
WHERE [project].[project_id] = @project_id
ORDER BY [package].[name];
GO
