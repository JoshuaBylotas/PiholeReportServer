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
-- Grouped by client_mac, not the raw IP - see docs/09, "point-in-time client
-- attribution". Rows ingested before that migration have no client_mac and
-- fall under NULL, distinct from any real device.
WITH flagged AS (
    SELECT q.client_mac, q.client_vendor,
           CASE WHEN EXISTS (SELECT 1
                             FROM dbo.GravityDomains AS gd
                             WHERE gd.domain = q.domain)
                THEN 1 ELSE 0 END AS on_blocklist
    FROM dbo.PiholeQueries AS q
    WHERE q.ts >= @from
      AND q.ts <  @to
),
totals AS (
    SELECT client_mac,
           MAX(client_vendor) AS vendor,
           COUNT_BIG(*)       AS total_queries,
           SUM(on_blocklist)  AS blocklist_queries
    FROM flagged
    GROUP BY client_mac
)
SELECT t.client_mac                          AS mac,
       COALESCE(dn.name, '(unknown)')        AS hostname,
       COALESCE(t.vendor, '')                AS vendor,
       t.total_queries,
       t.blocklist_queries,
       CAST(100.0 * t.blocklist_queries
            / NULLIF(t.total_queries, 0) AS decimal(5,2)) AS blocklist_pct
FROM totals AS t
     LEFT JOIN dbo.vDeviceName AS dn ON dn.mac = t.client_mac
ORDER BY t.blocklist_queries DESC;
