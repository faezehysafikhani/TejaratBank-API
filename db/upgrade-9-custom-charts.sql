-- Charts drawn by hand («طراحی چارت دلخواه») for TejaratBank-API: org_structure.CustomCharts.
-- Generated from the EF Core migration AddCustomCharts (Nexus.OrgStructure.Infrastructure) in the
-- NexusCore 0.1.0.1405072816-mahdi packages.
-- Idempotent: safe to run again. For a database already upgraded with 1 to 8 (run those first).
-- Not needed when Database:InitializeOrgStructureSchema is true (the default): the API does the same.
--   sqlcmd -S <server> -d TejaratBank -I -i db/upgrade-9-custom-charts.sql
SET QUOTED_IDENTIFIER ON;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101107_AddCustomCharts'
)
BEGIN
    CREATE TABLE [org_structure].[CustomCharts] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [Name] nvarchar(200) NOT NULL,
        [Description] nvarchar(1000) NULL,
        [Selection] nvarchar(max) NOT NULL,
        [ShowHolders] bit NOT NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAtUtc] datetimeoffset NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_CustomCharts] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101107_AddCustomCharts'
)
BEGIN
    CREATE INDEX [IX_CustomCharts_TenantId] ON [org_structure].[CustomCharts] ([TenantId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261025101107_AddCustomCharts'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261025101107_AddCustomCharts', N'8.0.24');
END;
GO

COMMIT;
GO

