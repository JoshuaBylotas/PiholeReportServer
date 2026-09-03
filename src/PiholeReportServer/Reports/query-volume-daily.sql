-- Daily volume with cache and blocklist shares.
SELECT CAST(q.ts AS date) AS [day],
       COUNT_BIG(*)                                            AS queries,
       COUNT(DISTINCT q.client)                                AS active_clients,
       COUNT(DISTINCT q.domain)                                AS distinct_domains,
       SUM(CASE WHEN q.status IN (3, 17) THEN 1 ELSE 0 END)    AS from_cache,
       CAST(100.0 * SUM(CASE WHEN q.status IN (3, 17) THEN 1 ELSE 0 END)
            / NULLIF(COUNT_BIG(*), 0) AS decimal(5,1))         AS cache_pct,
       SUM(CASE WHEN q.status IN (1, 4, 5, 9, 10, 11) THEN 1 ELSE 0 END) AS blocked
FROM dbo.PiholeQueries AS q
WHERE q.ts >= @from
  AND q.ts <  @to
GROUP BY CAST(q.ts AS date)
ORDER BY [day] DESC;
