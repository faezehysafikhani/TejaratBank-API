-- Sign-in sessions for TejaratBank-API: refresh tokens belong to a family (one sign-in) and remember
-- when they were exchanged, so a token that is used again after it was replaced ends the whole
-- sign-in (identity.RefreshTokens.FamilyId / UsedAtUtc). Matches the EF Core migration
-- SecurityRefreshTokenFamily (NexusCore.Infrastructure) in the NexusCore 0.1.0.1405080302-security3 packages.
-- Idempotent: safe to run again. Both columns are nullable, so rows made before keep working
-- (a row without a family counts as a family of its own).
-- Not needed when Database:SeedOnStartup is true (the default): the API adds the columns at start-up.
--   sqlcmd -S <server> -d TejaratBank -I -i db/upgrade-9-refresh-token-family.sql
-- Take a backup first.
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH(N'identity.RefreshTokens', N'FamilyId') IS NULL
BEGIN
    ALTER TABLE [identity].[RefreshTokens] ADD [FamilyId] uniqueidentifier NULL;
END;
GO

IF COL_LENGTH(N'identity.RefreshTokens', N'UsedAtUtc') IS NULL
BEGIN
    ALTER TABLE [identity].[RefreshTokens] ADD [UsedAtUtc] datetimeoffset NULL;
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RefreshTokens_UserId_FamilyId' AND object_id = OBJECT_ID(N'identity.RefreshTokens'))
BEGIN
    CREATE INDEX [IX_RefreshTokens_UserId_FamilyId] ON [identity].[RefreshTokens] ([UserId], [FamilyId]);
END;
GO
