/*
    dbo.SavedReports — per-user saved reports for the builder and the SQL console.

    Idempotent: safe to re-run.

    This is the ONLY table the web application is allowed to write to. The warehouse
    itself stays strictly read-only, which is what the SQL console's safety argument
    rests on, so the grant at the bottom is deliberately table-scoped rather than
    db_datawriter.

    Ownership is keyed on the Entra ID object id (the "oid" claim), not the UPN or
    display name: an oid is immutable, whereas a UPN changes when someone is renamed
    and would orphan their saved reports.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.SavedReports', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SavedReports
    (
        id          int IDENTITY(1,1)  NOT NULL,
        owner_oid   varchar(64)        NOT NULL,   -- Entra "oid" claim, immutable
        owner_name  varchar(256)       NULL,       -- display only, may go stale
        name        varchar(128)       NOT NULL,
        description varchar(512)       NULL,
        kind        varchar(16)        NOT NULL,   -- 'Builder' | 'Sql'
        payload     nvarchar(max)      NOT NULL,   -- BuilderSpec JSON, or SQL text
        created_utc datetime2(0)       NOT NULL
            CONSTRAINT DF_SavedReports_created_utc DEFAULT (SYSUTCDATETIME()),
        updated_utc datetime2(0)       NOT NULL
            CONSTRAINT DF_SavedReports_updated_utc DEFAULT (SYSUTCDATETIME()),
        run_count   int                NOT NULL
            CONSTRAINT DF_SavedReports_run_count DEFAULT (0),
        last_run_utc datetime2(0)      NULL,

        CONSTRAINT PK_SavedReports PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_SavedReports_kind CHECK (kind IN ('Builder', 'Sql')),
        -- One name per person. Saving over an existing name is an update, which is
        -- what makes "Save" idempotent from the UI's point of view.
        CONSTRAINT UQ_SavedReports_owner_name UNIQUE (owner_oid, name)
    );

    PRINT 'created dbo.SavedReports';
END
ELSE
    PRINT 'dbo.SavedReports already exists';
GO

-- Every read is scoped to one owner, so lead on owner_oid.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SavedReports_owner'
                 AND object_id = OBJECT_ID('dbo.SavedReports'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_SavedReports_owner
        ON dbo.SavedReports (owner_oid, kind, name);
    PRINT 'created IX_SavedReports_owner';
END
GO

/*
    Table-scoped write grant for the reporting login.

    Deliberately NOT db_datawriter: that would make the whole warehouse writable and
    dismantle the defence-in-depth behind the SQL console. The console's SqlGuard
    rejects INSERT/UPDATE/DELETE anyway, and even if it were defeated, the login can
    only write to this one table.
*/
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.SavedReports TO pihole_report_ro;
    PRINT 'granted CRUD on dbo.SavedReports to pihole_report_ro';
END
ELSE
    PRINT 'WARNING: login pihole_report_ro not found; grant skipped';
GO

SELECT 'SavedReports rows' AS check_name, COUNT(*) AS value FROM dbo.SavedReports;
GO
