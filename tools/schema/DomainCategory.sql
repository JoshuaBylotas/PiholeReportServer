/*
    Domain categorisation.

    Two tables, deliberately separate:

      dbo.Ut1Domains      the imported UT1 (Universite Toulouse) categorised domain
                          corpus - reference data, replaced wholesale on refresh.
      dbo.DomainCategory  the resolved answer for one FQDN, whatever decided it.

    Keeping the corpus separate means a re-import never destroys a manual
    correction, and the "source" column on DomainCategory records WHICH pass
    decided each row - so a ut1 or rule match can be trusted differently from a
    model guess.

    Idempotent: safe to re-run.
*/

SET NOCOUNT ON;
GO

-- ── Reference corpus ────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.Ut1Domains', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Ut1Domains
    (
        domain   varchar(253) NOT NULL,
        category varchar(32)  NOT NULL,
        CONSTRAINT PK_Ut1Domains PRIMARY KEY CLUSTERED (domain, category)
    );
    PRINT 'created dbo.Ut1Domains';
END
ELSE
    PRINT 'dbo.Ut1Domains already exists';
GO

-- ── Resolved categories ─────────────────────────────────────────────────────
IF OBJECT_ID('dbo.DomainCategory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DomainCategory
    (
        domain        varchar(253) NOT NULL,
        category      varchar(32)  NOT NULL,
        subcategory   varchar(64)  NULL,
        description   varchar(400) NULL,

        /*  Which pass decided this, in descending order of trust:
              manual    - a person corrected it; wins over everything, survives re-runs
              rule      - curated for this network (CDN, reverse DNS, local, vendors)
              ut1       - matched the Universite Toulouse categorised corpus
              blp       - matched the Blocklist Project corpus (US-oriented)
              blocklist - matched one of the Pi-hole adlists (verdict, not content)
              fetch     - derived from the site's own metadata
              model     - inferred by the local LLM; the only guess in the set        */
        source        varchar(16)  NOT NULL,

        /*  0-100. Deterministic sources are 100 by definition; the model supplies
            its own, and low-confidence rows can be re-run without touching the rest. */
        confidence    tinyint      NOT NULL CONSTRAINT DF_DomainCategory_conf DEFAULT (100),

        matched_on    varchar(253) NULL,   -- the value that matched, when it was a parent domain
        classified_utc datetime2(0) NOT NULL CONSTRAINT DF_DomainCategory_utc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_DomainCategory PRIMARY KEY CLUSTERED (domain),
        CONSTRAINT CK_DomainCategory_source
            CHECK (source IN ('manual', 'rule', 'ut1', 'blp', 'blocklist', 'fetch', 'model')),
        CONSTRAINT CK_DomainCategory_conf CHECK (confidence <= 100)
    );
    PRINT 'created dbo.DomainCategory';
END
ELSE
    PRINT 'dbo.DomainCategory already exists';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_DomainCategory_category'
                 AND object_id = OBJECT_ID('dbo.DomainCategory'))
BEGIN
    -- Reports group by category far more often than they look up one domain.
    CREATE NONCLUSTERED INDEX IX_DomainCategory_category
        ON dbo.DomainCategory (category)
        INCLUDE (domain, source, confidence);
    PRINT 'created IX_DomainCategory_category';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_DomainCategory_source'
                 AND object_id = OBJECT_ID('dbo.DomainCategory'))
BEGIN
    -- So "show me only what a rule decided, not what the model guessed" is cheap.
    CREATE NONCLUSTERED INDEX IX_DomainCategory_source
        ON dbo.DomainCategory (source, classified_utc);
    PRINT 'created IX_DomainCategory_source';
END
GO

-- ── Grants ──────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    -- Read-only on the corpus. Write on the resolved table only, so a person can
    -- correct a category from the UI without the warehouse becoming writable.
    GRANT SELECT ON dbo.Ut1Domains TO pihole_report_ro;
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.DomainCategory TO pihole_report_ro;
    PRINT 'granted read on Ut1Domains, CRUD on DomainCategory to pihole_report_ro';
END
ELSE
    PRINT 'WARNING: login pihole_report_ro not found; grants skipped';
GO

SELECT 'Ut1Domains'     AS tbl, COUNT_BIG(*) AS rows FROM dbo.Ut1Domains
UNION ALL
SELECT 'DomainCategory', COUNT_BIG(*) FROM dbo.DomainCategory;
GO
