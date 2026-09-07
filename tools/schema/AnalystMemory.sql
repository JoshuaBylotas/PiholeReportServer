/* ===========================================================================
   dbo.AnalystMemory — things the Analyst has been told and should keep knowing.

   The conversation on the Analyst page is in memory and short-lived, which is
   right for "and now just for that device" but wrong for a standing fact like
   "the Pixel is Josh's phone". Facts like that are worth once and then forever:
   without them the model cannot answer "what has Josh's phone been up to",
   because the warehouse only knows hostnames.

   Two kinds:

     device   an alias for a machine. subject is what a person calls it
              ("Josh's phone"), target is what the warehouse calls it
              ("Pixel-9-Pro-XL", an IP, or a MAC). These are the ones that make
              a question answerable rather than merely better informed.

     note     any other standing fact worth remembering ("the guest VLAN is
              10.20.9.x", "the NAS runs backups at 2am").

   Scoped per owner_oid like dbo.SavedReports, and written by the same login, so
   the warehouse itself stays read-only to the application.
   ======================================================================== */

IF OBJECT_ID('dbo.AnalystMemory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AnalystMemory
    (
        id          int IDENTITY(1,1) NOT NULL,
        owner_oid   varchar(64)       NOT NULL,   -- Entra "oid" claim, immutable
        owner_name  nvarchar(200)     NULL,       -- for readability only

        kind        varchar(10)       NOT NULL,   -- 'device' | 'note'

        -- What the person calls it. Unique per owner so teaching the same alias
        -- twice corrects it rather than accumulating contradictions.
        subject     nvarchar(100)     NOT NULL,

        -- What the warehouse calls it: DimClient.name, an ip, or a mac. Null for
        -- a note, which has no single referent.
        target      nvarchar(255)     NULL,

        -- The sentence given to the model. Held rather than composed at read time
        -- so a fact reads the way it was taught.
        fact        nvarchar(500)     NOT NULL,

        created_utc datetime2(0)      NOT NULL CONSTRAINT DF_AnalystMemory_created DEFAULT SYSUTCDATETIME(),
        updated_utc datetime2(0)      NOT NULL CONSTRAINT DF_AnalystMemory_updated DEFAULT SYSUTCDATETIME(),

        -- How often it has actually been in a prompt. Lets an unused fact be
        -- pruned later on evidence rather than guesswork.
        used_count  int               NOT NULL CONSTRAINT DF_AnalystMemory_used DEFAULT 0,

        CONSTRAINT PK_AnalystMemory PRIMARY KEY CLUSTERED (id),
        CONSTRAINT UQ_AnalystMemory_owner_subject UNIQUE (owner_oid, subject),
        CONSTRAINT CK_AnalystMemory_kind CHECK (kind IN ('device', 'note')),
        -- A device alias without a target cannot be turned into a WHERE clause,
        -- which is the whole reason for storing it.
        CONSTRAINT CK_AnalystMemory_device_target
            CHECK (kind <> 'device' OR (target IS NOT NULL AND LEN(target) > 0))
    );

    PRINT 'Created dbo.AnalystMemory.';
END
ELSE
BEGIN
    PRINT 'dbo.AnalystMemory already exists.';
END
GO

-- Every read is scoped to one owner and ordered for the prompt, so lead on
-- owner_oid and keep the kinds together.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_AnalystMemory_owner' AND object_id = OBJECT_ID('dbo.AnalystMemory'))
BEGIN
    CREATE NONCLUSTERED INDEX ix_AnalystMemory_owner
        ON dbo.AnalystMemory (owner_oid, kind, subject)
        INCLUDE (target, fact);
    PRINT 'Created ix_AnalystMemory_owner.';
END
GO

-- The application's login needs write access to THIS table only. The warehouse
-- tables stay SELECT-only, which is what the SQL console's safety argument rests
-- on: a compromised session can still not change history.
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.AnalystMemory TO pihole_report_ro;
    PRINT 'Granted CRUD on dbo.AnalystMemory to pihole_report_ro.';
END
GO
