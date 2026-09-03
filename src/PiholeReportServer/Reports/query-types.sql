-- Record-type breakdown via the type dimension.
SELECT COALESCE(dt.type_text, CONCAT('type ', q.type)) AS query_type,
       q.type                                          AS type_code,
       COUNT_BIG(*)                                     AS queries,
       CAST(100.0 * COUNT_BIG(*)
            / NULLIF(SUM(COUNT_BIG(*)) OVER (), 0) AS decimal(5,2)) AS pct,
       COUNT(DISTINCT q.client)                         AS clients
FROM dbo.PiholeQueries AS q
     LEFT JOIN dbo.DimType AS dt ON dt.type = q.type
WHERE q.ts >= @from
  AND q.ts <  @to
GROUP BY q.type, dt.type_text
ORDER BY queries DESC;
