-- Worst average upstream reply time, ignoring domains with too few samples to
-- be meaningful and cache hits (reply_time 0) which are not upstream latency.
SELECT TOP (@top)
       q.domain,
       COUNT_BIG(*)                                                 AS queries,
       CAST(AVG(q.reply_time) * 1000 AS decimal(10,2))              AS avg_ms,
       CAST(MAX(q.reply_time) * 1000 AS decimal(10,2))              AS max_ms,
       COUNT(DISTINCT q.client)                                     AS clients,
       MIN(q.forward)                                               AS example_upstream
FROM dbo.PiholeQueries AS q
WHERE q.ts >= @from
  AND q.ts <  @to
  AND q.reply_time > 0
GROUP BY q.domain
HAVING COUNT_BIG(*) >= @minQueries
ORDER BY avg_ms DESC;
