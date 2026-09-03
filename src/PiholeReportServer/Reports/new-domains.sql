-- Domains seen for the very first time inside the window. The first_seen
-- subquery scans the whole history deliberately: a domain that appeared once
-- last year is not "new" just because it reappeared today.
WITH first_seen AS (
    SELECT domain, MIN(ts) AS first_ts
    FROM dbo.PiholeQueries
    GROUP BY domain
)
SELECT TOP (@top)
       fs.domain,
       fs.first_ts                                AS first_seen,
       COUNT_BIG(*)                                AS queries_since,
       COUNT(DISTINCT q.client)                    AS clients,
       MIN(q.client)                               AS example_client,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.GravityDomains gd WHERE gd.domain = fs.domain)
            THEN 'yes' ELSE 'no' END               AS on_blocklist
FROM first_seen AS fs
     JOIN dbo.PiholeQueries AS q ON q.domain = fs.domain
WHERE fs.first_ts >= @from
  AND fs.first_ts <  @to
GROUP BY fs.domain, fs.first_ts
ORDER BY fs.first_ts DESC;
