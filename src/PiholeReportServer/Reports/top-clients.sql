-- Busiest clients, grouped by client_mac - the device's stable identity, not
-- whichever IP it happened to hold on a given query. See docs/09,
-- "point-in-time client attribution": grouping by the raw IP instead let a
-- DHCP reassignment mid-window blend two devices' traffic into one row.
-- Rows ingested before that migration have no client_mac and fall under
-- NULL, distinct from any real device.
SELECT TOP (@top)
       q.client_mac                              AS mac,
       COALESCE(dn.name, '(unknown)')             AS hostname,
       COALESCE(MAX(q.client_vendor), '')         AS vendor,
       COUNT_BIG(*)                               AS queries,
       COUNT(DISTINCT q.domain)                   AS distinct_domains,
       MIN(q.ts)                                  AS first_seen,
       MAX(q.ts)                                  AS last_seen
FROM dbo.PiholeQueries AS q
     LEFT JOIN dbo.vDeviceName AS dn ON dn.mac = q.client_mac
WHERE q.ts >= @from
  AND q.ts <  @to
GROUP BY q.client_mac, dn.name
ORDER BY queries DESC;
