-- Is the streaming loader keeping up, and did it leave gaps?
--
-- The loader advances its watermark past a row it cannot insert, so a stall
-- shows up as a hole rather than as a stopped clock. Any hour in the last week
-- with zero rows is worth investigating.
WITH bounds AS (
    SELECT MAX(ts) AS newest,
           MIN(ts) AS oldest,
           MAX(id) AS max_id,
           COUNT_BIG(*) AS total_rows
    FROM dbo.PiholeQueries
),
hours AS (
    SELECT DATEADD(hour, DATEDIFF(hour, 0, ts), 0) AS hour_bucket,
           COUNT_BIG(*) AS rows_in_hour
    FROM dbo.PiholeQueries
    WHERE ts >= DATEADD(day, -7, SYSUTCDATETIME())
    GROUP BY DATEADD(hour, DATEDIFF(hour, 0, ts), 0)
)
SELECT 'newest row (UTC)'            AS metric, CONVERT(varchar(19), b.newest, 120) AS value FROM bounds AS b
UNION ALL
SELECT 'oldest row (UTC)',           CONVERT(varchar(19), b.oldest, 120) FROM bounds AS b
UNION ALL
SELECT 'lag behind now (minutes)',   CAST(DATEDIFF(minute, b.newest, SYSUTCDATETIME()) AS varchar(20)) FROM bounds AS b
UNION ALL
SELECT 'total rows',                 FORMAT(b.total_rows, 'N0') FROM bounds AS b
UNION ALL
SELECT 'max FTL id',                 CAST(b.max_id AS varchar(20)) FROM bounds AS b
UNION ALL
SELECT 'hours covered (last 7d)',    CAST(COUNT(*) AS varchar(20)) FROM hours
UNION ALL
SELECT 'empty hours (last 7d)',      CAST(168 - COUNT(*) AS varchar(20)) FROM hours
UNION ALL
SELECT 'quietest hour (last 7d)',    CONCAT(CONVERT(varchar(19), MIN(h.hour_bucket), 120), ' — ',
                                            FORMAT(MIN(h.rows_in_hour), 'N0'), ' rows')
FROM hours AS h;
