-- Per-device blocklist-matching traffic, as a share of everything it asked for.
WITH totals AS (
    SELECT q.client,
           COUNT_BIG(*) AS total_queries,
           SUM(CASE WHEN EXISTS (SELECT 1 FROM dbo.GravityDomains gd WHERE gd.domain = q.domain)
                    THEN 1 ELSE 0 END) AS blocklist_queries
    FROM dbo.PiholeQueries AS q
    WHERE q.ts >= @from
      AND q.ts <  @to
    GROUP BY q.client
)
SELECT t.client                              AS ip,
       COALESCE(dc.hostname, '(unknown)')    AS hostname,
       COALESCE(dc.vendor, '')               AS vendor,
       t.total_queries,
       t.blocklist_queries,
       CAST(100.0 * t.blocklist_queries
            / NULLIF(t.total_queries, 0) AS decimal(5,2)) AS blocklist_pct
FROM totals AS t
     LEFT JOIN dbo.DimClient AS dc ON dc.ip = t.client
ORDER BY t.blocklist_queries DESC;
