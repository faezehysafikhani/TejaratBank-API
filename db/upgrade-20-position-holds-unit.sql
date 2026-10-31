-- The simple chart: a «سمت / پست سازمانی» may now also hold «واحد سازمانی» nodes (e.g. a CEO with deputies under him).
-- DATA ONLY, no schema change: adds ONE rule row per organization to org_structure.NodeTypeRules:
--     parent = the position type (kind_position, or org_role from the first version)  ->  child = the unit type (kind_unit / org_post).
-- Nothing is changed or removed; existing nodes, rules, people and heads stay exactly as they are. Idempotent.
-- (The API adds the same rule itself the first time someone who may add nodes opens the chart; this script is for doing it on purpose.)
-- Needs upgrade-19. Back up the database first. To undo: db/upgrade-20-rollback.sql.
--   sqlcmd -S <server> -d TejaratBank -I -f 65001 -i db/upgrade-20-position-holds-unit.sql
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();

INSERT INTO [org_structure].[NodeTypeRules] ([Id], [TenantId], [ParentNodeTypeId], [ChildNodeTypeId], [CreatedAtUtc])
SELECT NEWID(), p.[TenantId], p.[Id], u.[Id], @now
FROM [org_structure].[NodeTypes] p
JOIN [org_structure].[NodeTypes] u ON u.[TenantId] = p.[TenantId] AND u.[Code] IN (N'kind_unit', N'org_post')
WHERE p.[Code] IN (N'kind_position', N'org_role')
  AND NOT EXISTS (SELECT 1 FROM [org_structure].[NodeTypeRules] r
                  WHERE r.[TenantId] = p.[TenantId] AND r.[ParentNodeTypeId] = p.[Id] AND r.[ChildNodeTypeId] = u.[Id]);

PRINT CONCAT(N'upgrade-20: rules added: ', @@ROWCOUNT);
GO
