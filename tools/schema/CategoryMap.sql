/* ===========================================================================
   dbo.CategoryMap and dbo.vDomainCategory — one category vocabulary.

   THE PROBLEM

   dbo.DomainCategory is filled by four sources that do not agree on names:

     ut1   Universite Toulouse corpus  — "ads", "games", "social_networks"
     blp   the Blocklist Project       — "abuse", "porn", "fraud"
     rule  the curated rules here      — "advertising", "gaming", "social"
     model the local LLM               — the same set as the rules

   So 63 distinct values exist for maybe 34 concepts, and a query for one name
   silently misses the synonyms:

     WHERE category = 'finance'      returns    10 of      329 rows   ( 3%)
     WHERE category = 'advertising'  returns 2,309 of   10,488 rows   (22%)
     WHERE category = 'gaming'       returns   226 of      777 rows   (29%)
     WHERE category = 'malware'      returns    68 of      388 rows   (18%)
     WHERE category = 'adult'        returns   363 of      577 rows   (63%)

   Every one of those looks like a complete answer. That is the whole hazard:
   nothing errors, the number is just wrong, and it is wrong in the direction of
   under-reporting the thing you were worried about.

   THE APPROACH

   A view rather than a stored column. A column would need backfilling and every
   loader changing, and would then drift the first time a corpus added a value
   nobody remembered to map. The view cannot drift: it resolves at read time.

   Nothing is hidden. An unmapped source value falls through to itself rather
   than being bucketed as 'unknown', so a new corpus value still works — it is
   simply unmerged until it is added here. The final query in this script lists
   any such value, and it is worth running after every corpus refresh.

   The original value stays available as source_category, because "which list
   called it that" was a requirement in its own right.
   ======================================================================== */

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.CategoryMap', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CategoryMap
    (
        source_category    varchar(40)  NOT NULL,
        canonical_category varchar(40)  NOT NULL,
        -- Why, for the mappings that merged two names or made a judgement call.
        note               nvarchar(200) NULL,

        CONSTRAINT PK_CategoryMap PRIMARY KEY CLUSTERED (source_category)
    );
    PRINT 'created dbo.CategoryMap';
END
ELSE
BEGIN
    PRINT 'dbo.CategoryMap already exists';
END
GO

/* ---------------------------------------------------------------------------
   The map. Re-runnable: MERGE, so editing a line and re-running corrects it.

   Only genuine synonyms are merged. Distinct concepts stay distinct even when
   that leaves a small bucket, because collapsing "gambling" into "shopping" to
   tidy the list would destroy the answer to a question someone actually asks.
   --------------------------------------------------------------------------- */
WITH m (source_category, canonical_category, note) AS
(
    SELECT * FROM (VALUES
        -- Advertising: three names for ad delivery.
        ('ads',             'advertising',    N'ut1/blp name for advertising'),
        ('advertising',     'advertising',    NULL),
        ('marketingware',   'advertising',    N'ut1: marketing/adware delivery'),

        -- Tracking and measurement stay separate: "who is following me" and
        -- "who is counting me" are different questions.
        ('tracking',        'tracking',       NULL),
        ('analytics',       'analytics',      NULL),

        -- Threats. malware means malicious code or its command channel; the
        -- rest are deception and abuse, which is a different response.
        ('malware',         'malware',        NULL),
        ('abuse',           'threat',         N'blp: abusive/malicious activity, not malware itself'),
        ('fraud',           'threat',         N'blp: fraudulent sites'),
        ('scam',            'threat',         N'blp: scam sites'),
        ('stalkerware',     'threat',         N'ut1: covert surveillance apps'),
        ('drugs',           'drugs',          NULL),

        -- Finance. bank and bitcoin are the same question as finance: money.
        ('finance',         'finance',        NULL),
        ('financial',       'finance',        N'ut1 spelling'),
        ('bank',            'finance',        N'ut1: banking'),
        ('bitcoin',         'finance',        N'ut1: crypto is finance'),

        -- Adult. Four names, one concept.
        ('adult',           'adult',          NULL),
        ('porn',            'adult',          N'blp name'),
        ('mixed_adult',     'adult',          N'ut1: mixed adult content'),
        ('lingerie',        'adult',          N'ut1: adjacent to adult, kept with it'),
        ('dating',          'dating',         N'kept apart from adult: different intent'),
        ('gambling',        'gambling',       N'kept apart: a question people ask on its own'),

        -- Gaming.
        ('games',           'gaming',         N'ut1 name'),
        ('gaming',          'gaming',         NULL),

        -- Social and user-published content.
        ('social',          'social',         NULL),
        ('social_networks', 'social',         N'ut1 name'),
        ('forums',          'social',         N'ut1: discussion boards'),
        ('blog',            'social',         N'ut1: user-published content'),

        -- News.
        ('news',            'news',           NULL),
        ('press',           'news',           N'ut1 name'),
        ('fakenews',        'news',           N'ut1: still news content; subcategory keeps the distinction'),

        -- Media that is neither news nor a stream.
        ('celebrity',       'media',          NULL),
        ('manga',           'media',          NULL),
        ('streaming',       'streaming',      NULL),
        ('radio',           'streaming',      N'audio streaming'),
        ('sports',          'sports',         NULL),

        -- Communication.
        ('communication',   'communication',  NULL),
        ('chat',            'communication',  N'ut1 name'),
        ('webmail',         'communication',  N'ut1: mail is communication'),

        -- Filtering-bypass and anonymity tools. Grouped because for a Pi-hole
        -- owner they raise the same concern: traffic that avoids the filter.
        -- doh belongs here for that reason rather than with infrastructure.
        ('vpn',             'privacy',        N'bypass/anonymity tool'),
        ('proxy',           'privacy',        N'bypass/anonymity tool'),
        ('doh',             'privacy',        N'DNS-over-HTTPS resolvers bypass Pi-hole filtering'),

        -- File movement.
        ('torrent',         'filesharing',    NULL),
        ('warez',           'filesharing',    NULL),
        ('filehosting',     'filesharing',    NULL),
        ('download',        'filesharing',    NULL),
        ('shortener',       'shortener',      N'kept apart: a redirect hop, not a destination'),

        -- Plumbing.
        ('infrastructure',  'infrastructure', NULL),
        ('cloud',           'cloud',          NULL),
        ('webhosting',      'cloud',          N'ut1: hosting is cloud'),
        ('software',        'software',       NULL),
        ('update',          'software',       N'ut1: update endpoints are software distribution'),
        ('search',          'search',         NULL),
        ('iot',             'iot',            NULL),
        ('local',           'local',          NULL),
        ('ai',              'ai',             NULL),

        -- Life and work.
        ('work',            'work',           NULL),
        ('jobsearch',       'work',           N'ut1: job hunting is work-related'),
        ('shopping',        'shopping',       NULL),
        ('education',       'education',      NULL),
        ('government',      'government',     NULL),
        ('health',          'health',         NULL),
        ('travel',          'travel',         NULL),

        ('unknown',         'unknown',        N'classified but the model could not tell')
    ) AS v(source_category, canonical_category, note)
)
MERGE dbo.CategoryMap AS t
USING m ON t.source_category = m.source_category
WHEN MATCHED AND (t.canonical_category <> m.canonical_category
                  OR ISNULL(t.note, N'') <> ISNULL(m.note, N''))
    THEN UPDATE SET canonical_category = m.canonical_category, note = m.note
WHEN NOT MATCHED BY TARGET
    THEN INSERT (source_category, canonical_category, note)
         VALUES (m.source_category, m.canonical_category, m.note);

-- Assign first: a subquery inside PRINT/CONCAT is a COMPILE error, and because
-- SQL Server compiles the whole batch before running any of it, that would stop
-- the MERGE above from executing at all - silently leaving the map empty.
DECLARE @mappings int = (SELECT COUNT(*) FROM dbo.CategoryMap);
PRINT CONCAT('dbo.CategoryMap now holds ', @mappings, ' mappings.');
GO

/* ---------------------------------------------------------------------------
   dbo.vDomainCategory — what reports and the model should read.

   COALESCE, not ISNULL to 'unknown': an unmapped value passes through as
   itself. A new corpus category then still groups sensibly on its own rather
   than vanishing into an 'unknown' bucket where nobody would notice it.
   --------------------------------------------------------------------------- */
CREATE OR ALTER VIEW dbo.vDomainCategory
AS
SELECT
    c.domain,
    -- The one to filter and group on.
    canonical_category = COALESCE(m.canonical_category, c.category),
    -- What the corpus actually called it, kept because "which list said so" is
    -- a question in its own right.
    source_category    = c.category,
    c.subcategory,
    c.description,
    c.source,
    c.confidence,
    c.classified_utc,
    -- 1 when no mapping exists, so a corpus refresh that introduces a new name
    -- is visible instead of quietly splitting a total again.
    is_unmapped        = CASE WHEN m.source_category IS NULL THEN CONVERT(bit, 1) ELSE CONVERT(bit, 0) END
FROM dbo.DomainCategory AS c
     LEFT JOIN dbo.CategoryMap AS m ON m.source_category = c.category;
GO

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    GRANT SELECT ON dbo.CategoryMap      TO pihole_report_ro;
    GRANT SELECT ON dbo.vDomainCategory  TO pihole_report_ro;
    PRINT 'granted SELECT on dbo.CategoryMap and dbo.vDomainCategory to pihole_report_ro';
END
GO

/* ---------------------------------------------------------------------------
   Verification. Run this after every corpus refresh.
   --------------------------------------------------------------------------- */

PRINT '--- any source category with no mapping (should be empty) ---';
SELECT source_category = c.category, domains = COUNT(*)
FROM dbo.DomainCategory AS c
     LEFT JOIN dbo.CategoryMap AS m ON m.source_category = c.category
WHERE m.source_category IS NULL
GROUP BY c.category
ORDER BY COUNT(*) DESC;

PRINT '--- what the merge fixed: concepts that were split across names ---';
SELECT canonical_category,
       names_merged = COUNT(DISTINCT source_category),
       names        = STRING_AGG(CONVERT(varchar(400), source_category), ', '),
       domains      = SUM(domains)
FROM (
    SELECT v.canonical_category, v.source_category, domains = COUNT(*)
    FROM dbo.vDomainCategory AS v
    GROUP BY v.canonical_category, v.source_category
) AS x
GROUP BY canonical_category
HAVING COUNT(DISTINCT source_category) > 1
ORDER BY SUM(domains) DESC;
GO
