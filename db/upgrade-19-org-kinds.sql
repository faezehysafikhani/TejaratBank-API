-- The simple organization chart (org / unit / position), part of the structure redesign:
--   * new tables org_structure.NodePersons (the people of a position node, many to many),
--     org_structure.NodeHeads (a unit's head: one of its direct positions) and two small tables that remember
--     what this script changed (KindMigrationNodes, KindMigrationRetyped) so db/upgrade-19-rollback.sql can undo it;
--   * data, explicit mapping (nothing is deleted or changed in place except the node type of a node):
--       company (org_structure.Companies)        -> «سازمان» (org): unchanged, it is the root
--       every existing node (OrganizationUnits)  -> «واحد سازمانی» (node type kind_unit), its old type is remembered
--       every post (UnitPositions)               -> a child node «سمت / پست سازمانی» (kind_position) named by the
--                                                  post's position title: under its unit, under the node of its parent
--                                                  post, or at the top of its company for a post directly in the company
--       the holder of a post (UserPositions, open now) -> a row of NodePersons, the official one
--       the manager post of a node               -> that unit's head (NodeHeads)
--     Node types made by the first version of the simple chart (codes org_post / org_role) are reused as unit / position.
--     Posts, appointments and assignments are NOT removed: they keep working for tasks given to posts, history and rollback.
--   * the node types kind_unit and kind_position, and the rules between them, are added per organization (existing rules stay).
-- Idempotent: running it again adds nothing already added. Needs upgrade-10 to 18. Back up the database first.
-- Not needed for the tables when Database:InitializeOrgStructureSchema is true (the API makes them), but the data part is only here.
--   sqlcmd -S <server> -d TejaratBank -I -f 65001 -i db/upgrade-19-org-kinds.sql
-- To undo: db/upgrade-19-rollback.sql.
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'org_structure.NodePersons', N'U') IS NULL
BEGIN
    CREATE TABLE [org_structure].[NodePersons] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [UnitId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [IsPrimary] bit NOT NULL,
        [AddedAtUtc] datetimeoffset NOT NULL,
        [StartDate] datetimeoffset NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAtUtc] datetimeoffset NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_NodePersons] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_NodePersons_TenantId_UnitId_UserId] ON [org_structure].[NodePersons] ([TenantId], [UnitId], [UserId]);
    CREATE INDEX [IX_NodePersons_TenantId_UserId] ON [org_structure].[NodePersons] ([TenantId], [UserId]);
END;
GO

IF OBJECT_ID(N'org_structure.NodeHeads', N'U') IS NULL
BEGIN
    CREATE TABLE [org_structure].[NodeHeads] (
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [UnitId] uniqueidentifier NOT NULL,
        [HeadUnitId] uniqueidentifier NOT NULL,
        [CreatedAtUtc] datetimeoffset NOT NULL,
        [CreatedByUserId] uniqueidentifier NULL,
        [ModifiedAtUtc] datetimeoffset NULL,
        [ModifiedByUserId] uniqueidentifier NULL,
        CONSTRAINT [PK_NodeHeads] PRIMARY KEY ([Id])
    );
    CREATE UNIQUE INDEX [IX_NodeHeads_TenantId_UnitId] ON [org_structure].[NodeHeads] ([TenantId], [UnitId]);
END;
GO

IF OBJECT_ID(N'org_structure.KindMigrationNodes', N'U') IS NULL
    CREATE TABLE [org_structure].[KindMigrationNodes] ([NodeId] uniqueidentifier NOT NULL PRIMARY KEY, [PostId] uniqueidentifier NOT NULL, [TenantId] uniqueidentifier NOT NULL);
IF OBJECT_ID(N'org_structure.KindMigrationRetyped', N'U') IS NULL
    CREATE TABLE [org_structure].[KindMigrationRetyped] ([UnitId] uniqueidentifier NOT NULL PRIMARY KEY, [OldNodeTypeId] uniqueidentifier NULL, [TenantId] uniqueidentifier NOT NULL);
GO

BEGIN TRANSACTION;
GO

DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();

-- 1. the two node types per organization (reuse org_post / org_role from the first version)
CREATE TABLE #Tenants ([TenantId] uniqueidentifier NOT NULL PRIMARY KEY, [UnitTypeId] uniqueidentifier NULL, [PositionTypeId] uniqueidentifier NULL);
INSERT INTO #Tenants ([TenantId])
SELECT [TenantId] FROM [organization].[OrganizationUnits]
UNION SELECT [TenantId] FROM [org_structure].[UnitPositions]
UNION SELECT [TenantId] FROM [org_structure].[NodeTypes];

UPDATE t SET [UnitTypeId] = (SELECT TOP 1 n.[Id] FROM [org_structure].[NodeTypes] n WHERE n.[TenantId] = t.[TenantId] AND n.[Code] IN (N'kind_unit', N'org_post') ORDER BY CASE n.[Code] WHEN N'kind_unit' THEN 0 ELSE 1 END),
             [PositionTypeId] = (SELECT TOP 1 n.[Id] FROM [org_structure].[NodeTypes] n WHERE n.[TenantId] = t.[TenantId] AND n.[Code] IN (N'kind_position', N'org_role') ORDER BY CASE n.[Code] WHEN N'kind_position' THEN 0 ELSE 1 END)
FROM #Tenants t;

UPDATE #Tenants SET [UnitTypeId] = NEWID() WHERE [UnitTypeId] IS NULL;
UPDATE #Tenants SET [PositionTypeId] = NEWID() WHERE [PositionTypeId] IS NULL;

INSERT INTO [org_structure].[NodeTypes] ([Id], [TenantId], [Code], [Name], [IsDomainBoundary], [SortOrder], [IsActive], [Color], [Icon], [CreatedAtUtc])
SELECT t.[UnitTypeId], t.[TenantId], N'kind_unit', N'واحد سازمانی', 0, 0, 1, N'#0e9384', N'building-2', @now
FROM #Tenants t WHERE NOT EXISTS (SELECT 1 FROM [org_structure].[NodeTypes] n WHERE n.[Id] = t.[UnitTypeId]);
INSERT INTO [org_structure].[NodeTypes] ([Id], [TenantId], [Code], [Name], [IsDomainBoundary], [SortOrder], [IsActive], [Color], [Icon], [CreatedAtUtc])
SELECT t.[PositionTypeId], t.[TenantId], N'kind_position', N'سمت / پست سازمانی', 0, 1, 1, N'#c9731a', N'user', @now
FROM #Tenants t WHERE NOT EXISTS (SELECT 1 FROM [org_structure].[NodeTypes] n WHERE n.[Id] = t.[PositionTypeId]);

-- 2. the rules between them (the organization is the null parent); existing rules stay
CREATE TABLE #Pairs ([Parent] char(1) NULL, [Child] char(1) NOT NULL);
INSERT INTO #Pairs VALUES (NULL, 'U'), (NULL, 'P'), ('U', 'U'), ('U', 'P'), ('P', 'P');
INSERT INTO [org_structure].[NodeTypeRules] ([Id], [TenantId], [ParentNodeTypeId], [ChildNodeTypeId], [CreatedAtUtc])
SELECT NEWID(), t.[TenantId],
       CASE p.[Parent] WHEN 'U' THEN t.[UnitTypeId] WHEN 'P' THEN t.[PositionTypeId] END,
       CASE p.[Child] WHEN 'U' THEN t.[UnitTypeId] ELSE t.[PositionTypeId] END, @now
FROM #Tenants t CROSS JOIN #Pairs p
WHERE NOT EXISTS (
    SELECT 1 FROM [org_structure].[NodeTypeRules] r
    WHERE r.[TenantId] = t.[TenantId]
      AND ISNULL(r.[ParentNodeTypeId], '00000000-0000-0000-0000-000000000000') = ISNULL(CASE p.[Parent] WHEN 'U' THEN t.[UnitTypeId] WHEN 'P' THEN t.[PositionTypeId] END, '00000000-0000-0000-0000-000000000000')
      AND r.[ChildNodeTypeId] = CASE p.[Child] WHEN 'U' THEN t.[UnitTypeId] ELSE t.[PositionTypeId] END);

-- 3. every existing node that is not already a unit or a position becomes a unit; its old type is remembered
INSERT INTO [org_structure].[KindMigrationRetyped] ([UnitId], [OldNodeTypeId], [TenantId])
SELECT u.[Id], u.[NodeTypeId], u.[TenantId]
FROM [organization].[OrganizationUnits] u JOIN #Tenants t ON t.[TenantId] = u.[TenantId]
WHERE (u.[NodeTypeId] IS NULL OR u.[NodeTypeId] NOT IN (t.[UnitTypeId], t.[PositionTypeId]))
  AND NOT EXISTS (SELECT 1 FROM [org_structure].[KindMigrationRetyped] x WHERE x.[UnitId] = u.[Id])
  AND NOT EXISTS (SELECT 1 FROM [org_structure].[KindMigrationNodes] x WHERE x.[NodeId] = u.[Id]);
UPDATE u SET [NodeTypeId] = t.[UnitTypeId]
FROM [organization].[OrganizationUnits] u JOIN #Tenants t ON t.[TenantId] = u.[TenantId]
WHERE u.[NodeTypeId] IS NULL OR u.[NodeTypeId] NOT IN (t.[UnitTypeId], t.[PositionTypeId]);

-- 4. every post becomes a position node: first the posts with no post above (under their unit, or at the top of their
--    company), then, level by level, the posts under them
WHILE 1 = 1
BEGIN
    INSERT INTO [org_structure].[KindMigrationNodes] ([NodeId], [PostId], [TenantId])
    SELECT NEWID(), p.[Id], p.[TenantId]
    FROM [org_structure].[UnitPositions] p
    WHERE NOT EXISTS (SELECT 1 FROM [org_structure].[KindMigrationNodes] m WHERE m.[PostId] = p.[Id])
      AND (p.[ParentPostId] IS NULL OR EXISTS (SELECT 1 FROM [org_structure].[KindMigrationNodes] m2 WHERE m2.[PostId] = p.[ParentPostId]));
    IF @@ROWCOUNT = 0 BREAK;

    INSERT INTO [organization].[OrganizationUnits] ([Id], [TenantId], [Name], [ManagerUserId], [IsActive], [CreatedAtUtc], [NodeTypeId], [ParentUnitId], [ValidFrom], [ValidTo], [SortOrder])
    SELECT m.[NodeId], m.[TenantId], LEFT(pos.[Title], 160), NULL, pos.[IsActive], @now, t.[PositionTypeId],
           CASE WHEN p.[ParentPostId] IS NOT NULL THEN pm.[NodeId] ELSE p.[UnitId] END,
           un.[ValidFrom], un.[ValidTo], p.[SortOrder]
    FROM [org_structure].[KindMigrationNodes] m
    JOIN [org_structure].[UnitPositions] p ON p.[Id] = m.[PostId]
    JOIN [org_structure].[Positions] pos ON pos.[Id] = p.[PositionId]
    JOIN #Tenants t ON t.[TenantId] = m.[TenantId]
    LEFT JOIN [org_structure].[KindMigrationNodes] pm ON pm.[PostId] = p.[ParentPostId]
    LEFT JOIN [organization].[OrganizationUnits] un ON un.[Id] = p.[UnitId]
    WHERE NOT EXISTS (SELECT 1 FROM [organization].[OrganizationUnits] x WHERE x.[Id] = m.[NodeId]);
END;

-- a post directly in the company: its node is a top node of that company
INSERT INTO [org_structure].[CompanyUnits] ([Id], [TenantId], [CompanyId], [UnitId], [CreatedAtUtc])
SELECT NEWID(), m.[TenantId], p.[CompanyId], m.[NodeId], @now
FROM [org_structure].[KindMigrationNodes] m
JOIN [org_structure].[UnitPositions] p ON p.[Id] = m.[PostId]
WHERE p.[UnitId] IS NULL AND p.[ParentPostId] IS NULL
  AND NOT EXISTS (SELECT 1 FROM [org_structure].[CompanyUnits] c WHERE c.[UnitId] = m.[NodeId]);

-- 5. the holders (appointments open now) become the people of the position; each is the official one
INSERT INTO [org_structure].[NodePersons] ([Id], [TenantId], [UnitId], [UserId], [IsPrimary], [AddedAtUtc], [StartDate], [CreatedAtUtc])
SELECT NEWID(), m.[TenantId], m.[NodeId], up.[UserId], 1, ISNULL(up.[StartDate], up.[CreatedAtUtc]), up.[StartDate], @now
FROM [org_structure].[KindMigrationNodes] m
JOIN [org_structure].[UserPositions] up ON up.[UnitPositionId] = m.[PostId]
WHERE (up.[EndDate] IS NULL OR up.[EndDate] > @now) AND (up.[StartDate] IS NULL OR up.[StartDate] <= @now)
  AND NOT EXISTS (SELECT 1 FROM [org_structure].[NodePersons] x WHERE x.[UnitId] = m.[NodeId] AND x.[UserId] = up.[UserId]);

-- the manager of a position node is its official person
UPDATE n SET [ManagerUserId] = np.[UserId]
FROM [organization].[OrganizationUnits] n
JOIN [org_structure].[KindMigrationNodes] m ON m.[NodeId] = n.[Id]
JOIN [org_structure].[NodePersons] np ON np.[UnitId] = n.[Id] AND np.[IsPrimary] = 1
WHERE n.[ManagerUserId] IS NULL;

-- 6. the manager post of a unit is its head (a node's manager set directly stays as it is)
INSERT INTO [org_structure].[NodeHeads] ([Id], [TenantId], [UnitId], [HeadUnitId], [CreatedAtUtc])
SELECT NEWID(), m.[TenantId], p.[UnitId], m.[NodeId], @now
FROM [org_structure].[KindMigrationNodes] m
JOIN [org_structure].[UnitPositions] p ON p.[Id] = m.[PostId]
WHERE p.[IsManagerPost] = 1 AND p.[UnitId] IS NOT NULL AND p.[ParentPostId] IS NULL
  AND NOT EXISTS (SELECT 1 FROM [org_structure].[NodeHeads] h WHERE h.[TenantId] = m.[TenantId] AND h.[UnitId] = p.[UnitId]);

UPDATE un SET [ManagerUserId] = np.[UserId]
FROM [organization].[OrganizationUnits] un
JOIN [org_structure].[NodeHeads] h ON h.[UnitId] = un.[Id]
JOIN [org_structure].[NodePersons] np ON np.[UnitId] = h.[HeadUnitId] AND np.[IsPrimary] = 1
WHERE un.[ManagerUserId] IS NULL;

DROP TABLE #Pairs;
DROP TABLE #Tenants;
GO

COMMIT;
GO
PRINT N'upgrade-19: done. Check: posts -> position nodes (KindMigrationNodes), people (NodePersons), heads (NodeHeads).';
GO
