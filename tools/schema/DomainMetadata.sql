/*
    dbo.DomainMetadata — what the domain itself says it is.

    Populated by tools/fetch/fetch_domain_metadata.py, which runs on the isolated
    Pi, NOT on the database or web server. That separation is the point: this is
    the only component that makes outbound connections to arbitrary hosts drawn
    from DNS logs, some of which are malware and phishing infrastructure. Keeping
    it off the servers that hold the data limits what a hostile response can reach.

    A failed fetch is still useful data. "No HTTP service on 443 or 80" is a
    strong signal that a domain is a telemetry or API endpoint rather than a web
    site, so errors are recorded rather than discarded.

    Idempotent: safe to re-run.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.DomainMetadata', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DomainMetadata
    (
        domain        varchar(253)  NOT NULL,

        -- Transport outcome
        scheme        varchar(8)    NULL,   -- whichever of https/http answered
        http_status   int           NULL,
        final_url     varchar(1000) NULL,
        final_host    varchar(253)  NULL,   -- differs from domain when redirected away
        content_type  varchar(128)  NULL,
        server_header varchar(200)  NULL,
        body_bytes    int           NULL,
        elapsed_ms    int           NULL,

        -- What the page says about itself
        title         varchar(400)  NULL,
        description   varchar(1000) NULL,
        keywords      varchar(500)  NULL,
        og_site_name  varchar(200)  NULL,
        lang          varchar(16)   NULL,

        /*  Null on success. Recorded rather than discarded because the failure mode
            is itself informative: connection refused on both ports usually means an
            API or telemetry endpoint, not a web site.                              */
        error         varchar(300)  NULL,

        fetched_utc   datetime2(0)  NOT NULL
            CONSTRAINT DF_DomainMetadata_utc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_DomainMetadata PRIMARY KEY CLUSTERED (domain)
    );
    PRINT 'created dbo.DomainMetadata';
END
ELSE
    PRINT 'dbo.DomainMetadata already exists';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_DomainMetadata_fetched'
                 AND object_id = OBJECT_ID('dbo.DomainMetadata'))
BEGIN
    -- So a re-run can skip anything fetched recently without a full scan.
    CREATE NONCLUSTERED INDEX IX_DomainMetadata_fetched
        ON dbo.DomainMetadata (fetched_utc) INCLUDE (domain, http_status);
    PRINT 'created IX_DomainMetadata_fetched';
END
GO

/*  A dedicated login for the fetcher. It writes metadata and reads the target
    list, and can do nothing else — notably it cannot read dbo.PiholeQueries in
    full, only the aggregated view it needs. The Pi is the machine touching
    hostile hosts, so it gets the narrowest credential of anything here.        */
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_fetch')
    PRINT 'NOTE: create the pihole_fetch login separately, then re-run for grants';
ELSE
BEGIN
    GRANT SELECT, INSERT, UPDATE ON dbo.DomainMetadata TO pihole_fetch;
    PRINT 'granted metadata write to pihole_fetch';
END
GO

-- The report server reads metadata; it never writes it.
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    GRANT SELECT ON dbo.DomainMetadata TO pihole_report_ro;
    PRINT 'granted metadata read to pihole_report_ro';
END
GO

/*  The fetcher's work list: domains worth visiting, most-queried first, so the
    highest-value metadata lands even if a run is interrupted.                  */
CREATE OR ALTER VIEW dbo.FetchTargets
AS
SELECT TOP 100000
       q.domain,
       COUNT_BIG(*) AS queries
FROM dbo.PiholeQueries AS q
WHERE q.domain IS NOT NULL
  AND q.domain <> ''
  -- Reverse-DNS and local names have no web presence to inspect.
  AND q.domain NOT LIKE '%.arpa'
  AND q.domain NOT LIKE '%.local'
  AND q.domain NOT LIKE '%.internal'
GROUP BY q.domain
HAVING COUNT_BIG(*) > 25
ORDER BY COUNT_BIG(*) DESC;
GO

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_fetch')
    GRANT SELECT ON dbo.FetchTargets TO pihole_fetch;
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
    GRANT SELECT ON dbo.FetchTargets TO pihole_report_ro;
GO

SELECT 'fetch_targets' AS metric, COUNT(*) AS value FROM dbo.FetchTargets
UNION ALL
SELECT 'already_fetched', COUNT(*) FROM dbo.DomainMetadata;
GO
