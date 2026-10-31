-- Rolls back db/upgrade-19-org-kinds.sql: removes the position nodes it made (and their people, heads and company
-- links), gives the retyped nodes their old node type back, and drops NodePersons, NodeHeads and the two memory tables.
-- The node types kind_unit / kind_position and their rules are left (harmless). Posts, appointments and assignments
-- were never changed by upgrade-19, so nothing else is needed. REFUSES (changes nothing) when someone has since made a node
-- under a migrated position node; move or delete those first. Changes made with the new chart to people and heads since are lost.
-- Does nothing when upgrade-19 is not applied. Back up the database first.
--   sqlcmd -S <server> -d TejaratBank -I -f 65001 -i db/upgrade-19-rollback.sql
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'org_structure.KindMigrationNodes', N'U') IS NULL
BEGIN
    PRINT N'org_structure: upgrade-19 is not applied, nothing to roll back.';
END
ELSE
BEGIN
    IF EXISTS (
        SELECT 1 FROM [organization].[OrganizationUnits] c
        JOIN [org_structure].[KindMigrationNodes] m ON m.[NodeId] = c.[ParentUnitId]
        WHERE NOT EXISTS (SELECT 1 FROM [org_structure].[KindMigrationNodes] x WHERE x.[NodeId] = c.[Id]))
    BEGIN
        RAISERROR(N'upgrade-19 rollback refused: a node made after the upgrade sits under a migrated position node.', 16, 1);
    END
    ELSE
    BEGIN
        BEGIN TRANSACTION;

        DELETE FROM [org_structure].[CompanyUnits] WHERE [UnitId] IN (SELECT [NodeId] FROM [org_structure].[KindMigrationNodes]);
        IF OBJECT_ID(N'org_structure.OrgLinks', N'U') IS NOT NULL
            DELETE FROM [org_structure].[OrgLinks] WHERE [FromUnitId] IN (SELECT [NodeId] FROM [org_structure].[KindMigrationNodes]) OR [ToUnitId] IN (SELECT [NodeId] FROM [org_structure].[KindMigrationNodes]);
        DELETE FROM [org_structure].[NodeHeads] WHERE [HeadUnitId] IN (SELECT [NodeId] FROM [org_structure].[KindMigrationNodes]);
        DELETE FROM [org_structure].[NodePersons] WHERE [UnitId] IN (SELECT [NodeId] FROM [org_structure].[KindMigrationNodes]);
        DELETE FROM [organization].[OrganizationUnits] WHERE [Id] IN (SELECT [NodeId] FROM [org_structure].[KindMigrationNodes]);

        UPDATE u SET [NodeTypeId] = r.[OldNodeTypeId]
        FROM [organization].[OrganizationUnits] u JOIN [org_structure].[KindMigrationRetyped] r ON r.[UnitId] = u.[Id];

        DROP TABLE [org_structure].[KindMigrationNodes];
        DROP TABLE [org_structure].[KindMigrationRetyped];
        DROP TABLE [org_structure].[NodeHeads];
        DROP TABLE [org_structure].[NodePersons];

        COMMIT;
        PRINT N'upgrade-19 rolled back.';
    END
END
GO
