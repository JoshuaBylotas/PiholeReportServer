/*
    dbo.NightlyFinding — output of the scheduled analysis.

    The nightly job runs a fixed battery of questions over the previous day and
    stores what it found, so anomalies surface without anyone asking. Findings are
    kept as rows rather than a rendered report so they can be filtered, compared
    across days, and dismissed individually.

    A finding is the model's reading of real query results. The SQL it ran is stored
    alongside, because a conclusion nobody can check is worth very little when the
    reasoning came from a 7B model.

    Idempotent: safe to re-run.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.NightlyFinding', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NightlyFinding
    (
        id           int IDENTITY(1,1) NOT NULL,
        run_utc      datetime2(0)  NOT NULL,   -- groups findings from one night
        covers_date  date          NOT NULL,   -- the day analysed

        /*  Which standing question produced this. Stable across runs so a finding
            can be compared with the same question's answer yesterday.           */
        probe        varchar(64)   NOT NULL,
        question     varchar(400)  NOT NULL,

        finding      varchar(2000) NOT NULL,   -- the model's conclusion

        /*  low | normal | high. The model proposes it; used only for ordering and
            highlighting, never to hide anything.                                */
        severity     varchar(8)    NOT NULL
            CONSTRAINT DF_NightlyFinding_sev DEFAULT ('normal'),

        sql_used     varchar(4000) NULL,       -- so the conclusion can be checked
        row_count    int           NULL,
        elapsed_ms   int           NULL,

        /*  Set when someone marks a finding as seen or not worth surfacing again.
            Nothing is ever deleted automatically.                               */
        dismissed_utc datetime2(0) NULL,
        dismissed_by  varchar(256) NULL,

        CONSTRAINT PK_NightlyFinding PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_NightlyFinding_sev CHECK (severity IN ('low', 'normal', 'high'))
    );
    PRINT 'created dbo.NightlyFinding';
END
ELSE
    PRINT 'dbo.NightlyFinding already exists';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_NightlyFinding_recent'
                 AND object_id = OBJECT_ID('dbo.NightlyFinding'))
BEGIN
    -- The overview shows recent, undismissed findings first.
    CREATE NONCLUSTERED INDEX IX_NightlyFinding_recent
        ON dbo.NightlyFinding (covers_date DESC, severity)
        INCLUDE (probe, finding, dismissed_utc);
    PRINT 'created IX_NightlyFinding_recent';
END
GO

/*  The standing questions. A table rather than a hard-coded list so the battery can
    be changed without redeploying anything, and so a probe can be disabled when it
    turns out to produce noise.                                                     */
IF OBJECT_ID('dbo.NightlyProbe', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NightlyProbe
    (
        probe     varchar(64)  NOT NULL,
        question  varchar(400) NOT NULL,
        enabled   bit          NOT NULL CONSTRAINT DF_NightlyProbe_enabled DEFAULT (1),
        ordinal   int          NOT NULL CONSTRAINT DF_NightlyProbe_ord DEFAULT (100),
        CONSTRAINT PK_NightlyProbe PRIMARY KEY CLUSTERED (probe)
    );
    PRINT 'created dbo.NightlyProbe';
END
GO

MERGE dbo.NightlyProbe AS t
USING (VALUES
    ('new-domains',      'Which domains were queried yesterday for the first time ever, and which device asked? Report anything that looks unusual.', 10),
    ('category-shift',   'Compare yesterday''s query volume by category against the previous 7-day daily average. Report any category that moved by more than half.', 20),
    ('malware-contact',  'Did any device query a domain categorised as malware, phishing or cryptojacking yesterday? If so, which devices and which domains?', 30),
    ('top-tracking',     'Which device generated the most advertising and tracking queries yesterday, and how does that compare with its own 7-day average?', 40),
    ('new-device',       'Did any client IP appear yesterday that had never been seen before? What did it query?', 50),
    ('volume-anomaly',   'Was yesterday''s total query volume unusual compared with the previous 14 days? Which device or category accounts for the difference?', 60),
    ('failing-upstream', 'Were there unusual numbers of failed or slow DNS responses yesterday, and against which upstream resolver?', 70)
) AS s (probe, question, ordinal)
    ON t.probe = s.probe
-- Only the wording and ordering are refreshed; whether a probe is enabled is a
-- local decision and must survive a re-run.
WHEN MATCHED THEN UPDATE SET question = s.question, ordinal = s.ordinal
WHEN NOT MATCHED THEN INSERT (probe, question, ordinal) VALUES (s.probe, s.question, s.ordinal);
GO

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    -- The web app reads findings and lets a person dismiss one; the nightly job
    -- (running as this same login) writes them.
    GRANT SELECT, INSERT, UPDATE ON dbo.NightlyFinding TO pihole_report_ro;
    GRANT SELECT, UPDATE ON dbo.NightlyProbe TO pihole_report_ro;
    PRINT 'granted findings access to pihole_report_ro';
END
GO

SELECT 'probes' AS metric, COUNT(*) AS value FROM dbo.NightlyProbe
UNION ALL SELECT 'findings', COUNT(*) FROM dbo.NightlyFinding;
GO
