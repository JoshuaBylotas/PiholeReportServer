-- Top domains by query volume.
SELECT TOP (@top)
       q.domain,
       COUNT_BIG(*)             AS queries,
       COUNT(DISTINCT q.client) AS clients,
       MIN(q.ts)                AS first_seen,
       MAX(q.ts)                AS last_seen,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.GravityDomains gd WHERE gd.domain = q.domain)
            THEN 'yes' ELSE 'no' END AS on_blocklist
FROM dbo.PiholeQueries AS q
WHERE q.ts >= @from
  AND q.ts <  @to
GROUP BY q.domain
ORDER BY queries DESC;
