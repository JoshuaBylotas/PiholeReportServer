-- Domains that are not valid host names.
--
-- Two distinct causes show up here:
--   1. Non-UTF-8 bytes in the source row. The loader substitutes U+FFFD, which
--      lands as '?' because dbo.PiholeQueries.domain is varchar under a CP1252
--      collation. Those rows look like '192.0.2.58:443?http'.
--   2. Clients emitting genuinely malformed queries - embedded ports, stray
--      service labels, whitespace.
--
-- CHARINDEX is used rather than LIKE N'%' + NCHAR(65533) + N'%': matching an
-- nvarchar pattern against a varchar column makes U+FFFD collation-ignorable,
-- which matches every row instead of none.
SELECT q.domain,
       COUNT_BIG(*)                     AS queries,
       COUNT(DISTINCT q.client)         AS clients,
       MIN(q.client)                    AS example_client,
       MIN(q.ts)                        AS first_seen,
       MAX(q.ts)                        AS last_seen,
       CASE
           WHEN CHARINDEX('?', q.domain) > 0 THEN 'substituted byte'
           WHEN CHARINDEX(':', q.domain) > 0 THEN 'embedded port'
           WHEN CHARINDEX(' ', q.domain) > 0 THEN 'whitespace'
           WHEN q.domain LIKE '%..%'         THEN 'empty label'
           ELSE 'other'
       END                              AS defect
FROM dbo.PiholeQueries AS q
WHERE q.ts >= @from
  AND q.ts <  @to
  AND (CHARINDEX('?', q.domain) > 0
       OR CHARINDEX(':', q.domain) > 0
       OR CHARINDEX(' ', q.domain) > 0
       OR CHARINDEX('_', q.domain) > 0
       OR q.domain LIKE '%..%')
GROUP BY q.domain
ORDER BY queries DESC;
