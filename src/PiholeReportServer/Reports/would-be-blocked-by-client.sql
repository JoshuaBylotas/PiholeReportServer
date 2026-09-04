-- Per-device blocklist-matching traffic, as a share of everything it asked for.
--
-- The blocklist test has to be evaluated per row and aggregated afterwards:
-- SUM(CASE WHEN EXISTS (...) THEN 1 ELSE 0 END) is rejected outright with
--   Msg 130: Cannot perform an aggregate function on an expression containing
--   an aggregate or a subquery
-- so the flag is computed in a CTE and summed in the next one. EXISTS is used
-- rather than a join to dbo.GravityDomains because that table holds one row per
-- (domain, adlist) pair -- joining it directly would multiply the row counts by
-- the number of lists a domain appears on.
WITH flagged AS (
    SELECT q.client,
           CASE WHEN EXISTS (SELECT 1
                             FROM dbo.GravityDomains AS gd
                             WHERE gd.domain = q.domain)
                THEN 1 ELSE 0 END AS on_blocklist
    FROM dbo.PiholeQueries AS q
    WHERE q.ts >= @from
      AND q.ts <  @to
),
totals AS (
    SELECT client,
           COUNT_BIG(*)      AS total_queries,
           SUM(on_blocklist) AS blocklist_queries
    FROM flagged
    GROUP BY client
)
SELECT t.client                              AS ip,
       COALESCE(dc.name, '(unknown)')        AS hostname,
       COALESCE(dc.mac_vendor, '')           AS vendor,
       t.total_queries,
       t.blocklist_queries,
       CAST(100.0 * t.blocklist_queries
            / NULLIF(t.total_queries, 0) AS decimal(5,2)) AS blocklist_pct
FROM totals AS t
     LEFT JOIN dbo.DimClient AS dc ON dc.ip = t.client
ORDER BY t.blocklist_queries DESC;
