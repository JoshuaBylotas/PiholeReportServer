-- Everything one client asked for in the window.
SELECT TOP (@top)
       q.domain,
       COUNT_BIG(*)                                       AS queries,
       MAX(q.ts)                                          AS last_seen,
       COALESCE(ds.status_text, q.status_text)            AS last_status,
       COALESCE(dt.type_text, CONCAT('type ', q.type))    AS query_type,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.GravityDomains gd WHERE gd.domain = q.domain)
            THEN 'yes' ELSE 'no' END                      AS on_blocklist
FROM dbo.PiholeQueries AS q
     LEFT JOIN dbo.DimStatus AS ds ON ds.status = q.status
     LEFT JOIN dbo.DimType   AS dt ON dt.type   = q.type
-- client_mac is the stable identity (see docs/09, "point-in-time client
-- attribution") - a device keeps its MAC across a DHCP reassignment, which
-- its IP does not. The IP match is kept only so a value copied from an older
-- bookmark, or a device with no known MAC, still resolves to something.
WHERE (q.client_mac = @client OR q.client = @client)
  AND q.ts >= @from
  AND q.ts <  @to
GROUP BY q.domain, q.status_text, ds.status_text, q.type, dt.type_text
ORDER BY queries DESC;
