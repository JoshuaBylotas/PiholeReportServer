-- How queries were answered.
SELECT COALESCE(ds.status_text, q.status_text, CONCAT('status ', q.status)) AS status,
       q.status                                                            AS status_code,
       COUNT_BIG(*)                                                        AS queries,
       CAST(100.0 * COUNT_BIG(*)
            / NULLIF(SUM(COUNT_BIG(*)) OVER (), 0) AS decimal(5,2))        AS pct
FROM dbo.PiholeQueries AS q
     LEFT JOIN dbo.DimStatus AS ds ON ds.status = q.status
WHERE q.ts >= @from
  AND q.ts <  @to
GROUP BY q.status, q.status_text, ds.status_text
ORDER BY queries DESC;
