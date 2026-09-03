-- Forwarded traffic per upstream resolver, with reply-time distribution.
-- reply_time is stored in seconds; multiplied out to milliseconds here.
--
-- PERCENTILE_CONT in SQL Server is an analytic function only - there is no
-- aggregate form - so the p95 is computed over the row set in its own CTE and
-- then joined to the grouped aggregates.
WITH forwarded AS (
    SELECT q.forward,
           q.reply_time,
           q.domain
    FROM dbo.PiholeQueries AS q
    WHERE q.ts >= @from
      AND q.ts <  @to
      AND q.forward IS NOT NULL
      AND q.forward <> ''
),
pctl AS (
    SELECT DISTINCT
           forward,
           PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY reply_time)
               OVER (PARTITION BY forward) AS p95_seconds
    FROM forwarded
),
agg AS (
    SELECT forward,
           COUNT_BIG(*)                     AS queries,
           COUNT(DISTINCT domain)           AS distinct_domains,
           AVG(NULLIF(reply_time, 0))       AS avg_seconds,
           MIN(NULLIF(reply_time, 0))       AS min_seconds,
           MAX(reply_time)                  AS max_seconds
    FROM forwarded
    GROUP BY forward
)
SELECT a.forward                                          AS upstream,
       a.queries,
       a.distinct_domains,
       CAST(a.avg_seconds * 1000 AS decimal(10,2))        AS avg_ms,
       CAST(a.min_seconds * 1000 AS decimal(10,2))        AS min_ms,
       CAST(p.p95_seconds * 1000 AS decimal(10,2))        AS p95_ms,
       CAST(a.max_seconds * 1000 AS decimal(10,2))        AS max_ms
FROM agg AS a
     LEFT JOIN pctl AS p ON p.forward = a.forward
ORDER BY a.queries DESC;
