:setvar SQLCMDERRORLEVEL 1
:on error exit

USE [SSISDB];
GO

SET NOCOUNT ON;

DECLARE @folder_name nvarchar(128) = N'FlowPlaneA10';
DECLARE @project_name nvarchar(128) = N'FlowPlaneA11Fixture';
DECLARE @environment_name nvarchar(128) = N'FlowPlaneA11Environment';
DECLARE @variable_name nvarchar(128) = N'FlowPlaneA11SensitiveValue';
DECLARE @fixture_canary nvarchar(128) = N'flowplane-a11-sensitive-canary-value';

-- Explicit operator-run fixture setup only. Lookups use the catalog's collation.
-- Never reuse an unrelated environment merely because its name collides.
IF EXISTS
(
    SELECT 1
    FROM [catalog].[environments] AS [environment]
    INNER JOIN [catalog].[folders] AS [folder]
        ON [folder].[folder_id] = [environment].[folder_id]
    WHERE [folder].[name] = @folder_name
      AND [environment].[name] = @environment_name
      AND ISNULL([environment].[description], N'') <> N'FlowPlane A-11 controlled discovery fixture'
)
    THROW 50002, 'The existing environment is not the approved A-11 fixture environment.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[projects] AS [project]
    INNER JOIN [catalog].[folders] AS [folder]
        ON [folder].[folder_id] = [project].[folder_id]
    WHERE [folder].[name] = @folder_name
      AND [project].[name] = @project_name
)
    THROW 50000, 'Deploy FlowPlaneA11Fixture before configuring its catalog metadata.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[environments] AS [environment]
    INNER JOIN [catalog].[folders] AS [folder]
        ON [folder].[folder_id] = [environment].[folder_id]
    WHERE [folder].[name] = @folder_name
      AND [environment].[name] = @environment_name
)
BEGIN
    EXEC [catalog].[create_environment]
        @folder_name = @folder_name,
        @environment_name = @environment_name,
        @environment_description = N'FlowPlane A-11 controlled discovery fixture';
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[environment_variables] AS [variable]
    INNER JOIN [catalog].[environments] AS [environment]
        ON [environment].[environment_id] = [variable].[environment_id]
    INNER JOIN [catalog].[folders] AS [folder]
        ON [folder].[folder_id] = [environment].[folder_id]
    WHERE [folder].[name] = @folder_name
      AND [environment].[name] = @environment_name
      AND [variable].[name] = @variable_name
)
BEGIN
    EXEC [catalog].[create_environment_variable]
        @folder_name = @folder_name,
        @environment_name = @environment_name,
        @variable_name = @variable_name,
        @data_type = N'String',
        @sensitive = 1,
        @value = @fixture_canary,
        @description = N'Non-credential canary used only to prove discovery redaction';
END
ELSE
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM [catalog].[environment_variables] AS [variable]
        INNER JOIN [catalog].[environments] AS [environment]
            ON [environment].[environment_id] = [variable].[environment_id]
        INNER JOIN [catalog].[folders] AS [folder]
            ON [folder].[folder_id] = [environment].[folder_id]
        WHERE [folder].[name] = @folder_name
          AND [environment].[name] = @environment_name
          AND [variable].[name] = @variable_name
          AND ([variable].[type] <> N'String' OR [variable].[sensitive] <> 1)
    )
        THROW 50001, 'The existing A-11 environment variable has incompatible metadata.', 1;

    EXEC [catalog].[set_environment_variable_value]
        @folder_name = @folder_name,
        @environment_name = @environment_name,
        @variable_name = @variable_name,
        @value = @fixture_canary;
END;

IF NOT EXISTS
(
    SELECT 1
    FROM [catalog].[environment_references] AS [reference]
    INNER JOIN [catalog].[projects] AS [project]
        ON [project].[project_id] = [reference].[project_id]
    INNER JOIN [catalog].[folders] AS [folder]
        ON [folder].[folder_id] = [project].[folder_id]
    WHERE [folder].[name] = @folder_name
      AND [project].[name] = @project_name
      AND [reference].[environment_name] = @environment_name
      AND [reference].[reference_type] = 'R'
)
BEGIN
    DECLARE @reference_id bigint;
    EXEC [catalog].[create_environment_reference]
        @folder_name = @folder_name,
        @project_name = @project_name,
        @environment_name = @environment_name,
        @reference_type = 'R',
        @reference_id = @reference_id OUTPUT;
END;

EXEC [catalog].[set_object_parameter_value]
    @object_type = 20,
    @folder_name = @folder_name,
    @project_name = @project_name,
    @parameter_name = N'RequiredProjectInput',
    @parameter_value = N'configured-for-discovery-fixture',
    @value_type = 'V';

EXEC [catalog].[set_object_parameter_value]
    @object_type = 20,
    @folder_name = @folder_name,
    @project_name = @project_name,
    @parameter_name = N'SensitiveEnvironmentInput',
    @parameter_value = @variable_name,
    @value_type = 'R';
GO
