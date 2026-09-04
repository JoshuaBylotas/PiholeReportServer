-- Busiest clients, resolved through the client dimension.
SELECT TOP (@top)
       q.client                                  AS ip,
       COALESCE(dc.name, '(unknown)')        AS hostname,
       COALESCE(dc.mac_vendor, '')                   AS vendor,
       COALESCE(dc.mac, '')                      AS mac,
       COUNT_BIG(*)                              AS queries,
       COUNT(DISTINCT q.domain)                  AS distinct_domains,
       MIN(q.ts)                                 AS first_seen,
       MAX(q.ts)                                 AS last_seen
FROM dbo.PiholeQueries AS q
     LEFT JOIN dbo.DimClient AS dc ON dc.ip = q.client
WHERE q.ts >= @from
  AND q.ts <  @to
GROUP BY q.client, dc.name, dc.mac_vendor, dc.mac
ORDER BY queries DESC;
