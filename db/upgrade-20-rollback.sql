-- Rolls back db/upgrade-20-position-holds-unit.sql: removes the rule «position may hold a unit». REFUSES (changes nothing)
-- while a unit sits under a position, because the structure would then break its own rules. Move those units first.
--   sqlcmd -S <server> -d TejaratBank -I -f 65001 -i db/upgrade-20-rollback.sql
SET QUOTED_IDENTIFIER ON;
GO

IF EXISTS (
    SELECT 1 FROM [organization].[OrganizationUnits] c
    JOIN [organization].[OrganizationUnits] par ON par.[Id] = c.[ParentUnitId]
    JOIN [org_structure].[NodeTypes] pt ON pt.[Id] = par.[NodeTypeId] AND pt.[Code] IN (N'kind_position', N'org_role')
    JOIN [org_structure].[NodeTypes] ct ON ct.[Id] = c.[NodeTypeId] AND ct.[Code] IN (N'kind_unit', N'org_post')
    WHERE c.[IsActive] = 1)
    RAISERROR(N'upgrade-20 rollback refused: a unit sits under a position. Move it first.', 16, 1);
ELSE
BEGIN
    DELETE r FROM [org_structure].[NodeTypeRules] r
    JOIN [org_structure].[NodeTypes] p ON p.[Id] = r.[ParentNodeTypeId] AND p.[Code] IN (N'kind_position', N'org_role')
    JOIN [org_structure].[NodeTypes] u ON u.[Id] = r.[ChildNodeTypeId] AND u.[Code] IN (N'kind_unit', N'org_post');
    PRINT CONCAT(N'upgrade-20 rolled back: rules removed: ', @@ROWCOUNT);
END
GO
