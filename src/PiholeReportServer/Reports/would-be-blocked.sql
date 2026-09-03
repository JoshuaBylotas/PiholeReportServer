-- Domains that were answered normally but appear on a subscribed blocklist.
-- Pi-hole blocking is off (log-only), so this is the "what it would have
-- caught" report. Statuses 1/4/5/9/10/11 are the actually-blocked ones and are
-- excluded so the list reflects traffic that really did get through.
SELECT TOP (@top)
       q.domain,
       COUNT_BIG(*)                        AS queries,
       COUNT(DISTINCT q.client)            AS clients,
       MAX(q.ts)                           AS last_seen,
       COUNT(DISTINCT gd.adlist_id)        AS matching_lists,
       MIN(al.address)                     AS example_list
FROM dbo.PiholeQueries AS q
     JOIN dbo.GravityDomains AS gd ON gd.domain = q.domain
     LEFT JOIN dbo.Adlists   AS al ON al.id = gd.adlist_id
WHERE q.ts >= @from
  AND q.ts <  @to
  AND q.status NOT IN (1, 4, 5, 9, 10, 11)
GROUP BY q.domain
ORDER BY queries DESC;
