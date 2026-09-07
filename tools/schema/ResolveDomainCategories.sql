/*
    dbo.ResolveDomainCategories — populate dbo.DomainCategory from the deterministic
    sources, in precedence order.

        manual  (never touched here)  a person corrected it; survives every re-run
        rule                          curated for THIS network
        ut1                           Universite Toulouse categorised corpus
        blp                           Blocklist Project (US-oriented)
        model   (not set here)        the local LLM fills what remains

    Rules outrank the public corpora deliberately: a corpus is generic, a rule is
    curated for this environment. Within a source, the LONGEST suffix match wins,
    so a specific entry beats a general one.

    Matching walks each FQDN up its parents — traffic is 'occ-0-2851.1.netflix.com'
    while a corpus lists 'netflix.com'. The walk is capped at 8 levels; beyond that
    a parent match carries no meaning, and uncapped recursion blows up on deep
    reverse-DNS names.

    @OnlyNew = 1 (default) leaves existing rows alone, so this is cheap to re-run
    as new domains appear. @OnlyNew = 0 re-resolves everything except manual rows.
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.ResolveDomainCategories
    @OnlyNew bit = 1,
    @Since   datetime2(0) = NULL   -- only consider domains queried since this time
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @started datetime2(0) = SYSUTCDATETIME();

    -- 1. The domains we care about: those actually seen in the warehouse.
    CREATE TABLE #mine (domain varchar(253) NOT NULL PRIMARY KEY);
    INSERT INTO #mine (domain)
    SELECT DISTINCT q.domain
    FROM dbo.PiholeQueries AS q
    WHERE q.domain IS NOT NULL
      AND q.domain <> ''
      AND (@Since IS NULL OR q.ts >= @Since)
      AND (@OnlyNew = 0 OR NOT EXISTS (SELECT 1 FROM dbo.DomainCategory dc WHERE dc.domain = q.domain));

    -- 2. Every parent suffix of each, for equality joins against the indexed corpora.
    --    A leading-wildcard LIKE cannot use an index and is unusably slow here.
    CREATE TABLE #sfx (domain varchar(253) NOT NULL, suffix varchar(253) NOT NULL, lvl tinyint NOT NULL);

    ;WITH walk AS (
        SELECT domain, CAST(domain AS varchar(253)) AS suffix, CAST(0 AS tinyint) AS lvl FROM #mine
        UNION ALL
        SELECT domain, CAST(STUFF(suffix, 1, CHARINDEX('.', suffix), '') AS varchar(253)),
               CAST(lvl + 1 AS tinyint)
        FROM walk
        WHERE CHARINDEX('.', suffix) > 0 AND lvl < 8
    )
    INSERT INTO #sfx (domain, suffix, lvl) SELECT domain, suffix, lvl FROM walk OPTION (MAXRECURSION 0);

    CREATE INDEX IX_sfx ON #sfx (suffix) INCLUDE (domain);

    -- 3. Candidate matches from each source, ranked. Precedence first, then the
    --    longest (most specific) suffix within that source.
    CREATE TABLE #cand (
        domain      varchar(253) NOT NULL,
        category    varchar(32)  NOT NULL,
        subcategory varchar(64)  NULL,
        source      varchar(16)  NOT NULL,
        matched_on  varchar(253) NOT NULL,
        rank_source tinyint      NOT NULL
    );

    INSERT INTO #cand (domain, category, subcategory, source, matched_on, rank_source)
    SELECT s.domain, r.category, r.subcategory, 'rule', s.suffix, 1
    FROM #sfx s JOIN dbo.DomainRule r ON r.suffix = s.suffix;

    INSERT INTO #cand (domain, category, subcategory, source, matched_on, rank_source)
    SELECT s.domain, u.category, NULL, 'ut1', s.suffix, 2
    FROM #sfx s JOIN dbo.Ut1Domains u ON u.domain = s.suffix;

    IF OBJECT_ID('dbo.BlpDomains', 'U') IS NOT NULL
    BEGIN
        INSERT INTO #cand (domain, category, subcategory, source, matched_on, rank_source)
        SELECT s.domain, b.category, NULL, 'blp', s.suffix, 3
        FROM #sfx s JOIN dbo.BlpDomains b ON b.domain = s.suffix;
    END;

    -- 4. One winner per domain.
    WITH ranked AS (
        SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY domain
                    ORDER BY rank_source, LEN(matched_on) DESC, category) AS rn
        FROM #cand
    )
    MERGE dbo.DomainCategory AS t
    USING (SELECT domain, category, subcategory, source, matched_on FROM ranked WHERE rn = 1) AS s
        ON t.domain = s.domain
    WHEN MATCHED AND t.source <> 'manual' AND @OnlyNew = 0 THEN
        UPDATE SET category = s.category, subcategory = s.subcategory, source = s.source,
                   matched_on = s.matched_on, confidence = 100, classified_utc = SYSUTCDATETIME()
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (domain, category, subcategory, source, matched_on, confidence, classified_utc)
        VALUES (s.domain, s.category, s.subcategory, s.source, s.matched_on, 100, SYSUTCDATETIME());

    DECLARE @resolved int = @@ROWCOUNT;

    SELECT 'considered'  AS metric, COUNT(*) AS value FROM #mine
    UNION ALL SELECT 'resolved_this_run', @resolved
    UNION ALL SELECT 'still_uncategorised',
        (SELECT COUNT(*) FROM #mine m WHERE NOT EXISTS (SELECT 1 FROM dbo.DomainCategory dc WHERE dc.domain = m.domain))
    UNION ALL SELECT 'seconds', DATEDIFF(second, @started, SYSUTCDATETIME());

    DROP TABLE #cand; DROP TABLE #sfx; DROP TABLE #mine;
END
GO

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
    GRANT EXECUTE ON dbo.ResolveDomainCategories TO pihole_report_ro;
GO

PRINT 'dbo.ResolveDomainCategories ready';
GO
