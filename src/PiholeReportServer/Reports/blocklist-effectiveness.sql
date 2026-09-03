-- Which subscribed list would have caught the most traffic.
WITH matched AS (
    SELECT gd.adlist_id,
           q.domain,
           COUNT_BIG(*) AS queries
    FROM dbo.PiholeQueries AS q
         JOIN dbo.GravityDomains AS gd ON gd.domain = q.domain
    WHERE q.ts >= @from
      AND q.ts <  @to
      AND q.status NOT IN (1, 4, 5, 9, 10, 11)
    GROUP BY gd.adlist_id, q.domain
),
list_size AS (
    SELECT adlist_id, COUNT_BIG(*) AS domains_on_list
    FROM dbo.GravityDomains
    GROUP BY adlist_id
)
SELECT COALESCE(al.address, CONCAT('adlist ', ls.adlist_id)) AS blocklist,
       ls.domains_on_list,
       COALESCE(SUM(m.queries), 0)                           AS queries_caught,
       COUNT(m.domain)                                       AS domains_hit,
       CAST(100.0 * COUNT(m.domain)
            / NULLIF(ls.domains_on_list, 0) AS decimal(6,3)) AS list_utilisation_pct
FROM list_size AS ls
     LEFT JOIN matched      AS m  ON m.adlist_id = ls.adlist_id
     LEFT JOIN dbo.Adlists  AS al ON al.id       = ls.adlist_id
GROUP BY ls.adlist_id, al.address, ls.domains_on_list
ORDER BY queries_caught DESC;
