-- Average queries per hour-of-day, so a short window is not skewed by day count.
WITH per_hour AS (
    SELECT DATEPART(hour, q.ts) AS hour_of_day,
           CAST(q.ts AS date)   AS [day],
           COUNT_BIG(*)         AS queries
    FROM dbo.PiholeQueries AS q
    WHERE q.ts >= @from
      AND q.ts <  @to
    GROUP BY DATEPART(hour, q.ts), CAST(q.ts AS date)
)
SELECT hour_of_day,
       SUM(queries)                                  AS total_queries,
       COUNT(DISTINCT [day])                         AS days_observed,
       CAST(AVG(CAST(queries AS float)) AS decimal(12,1)) AS avg_per_day,
       MAX(queries)                                  AS busiest_day
FROM per_hour
GROUP BY hour_of_day
ORDER BY hour_of_day;
