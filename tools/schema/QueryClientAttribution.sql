/* ===========================================================================
   Point-in-time client identity on the fact table.

   THE FAULT THIS CLOSES

   Every client-facing report resolved "Allie's iPhone" to a single CURRENT
   IP address (dbo.DimClient / dbo.vClient, one row per IP, overwritten daily
   by dims.py) and then filtered dbo.PiholeQueries by that IP across the
   whole requested date range. DHCP reassigns IPs over time, so a report
   window that spans a reassignment pulls in another device's queries -
   observed as OMEN01's traffic appearing under Allie's iPhone.

   THE FIX

   Stamp client_mac (+ the hostname/vendor FTL knew at that instant) onto
   each row AS IT IS INGESTED, when the IP-to-MAC mapping is known with
   certainty. Reports then group/filter by client_mac - a stable identity -
   instead of a reused IP, and join dbo.vDeviceName for the current best
   display name.

   client_hostname and client_vendor are FTL's raw, unfiltered values at
   ingest time - audit/diagnostic columns, mirroring dbo.DimClient's
   reported_name. They are not the display name; dbo.vDeviceName is.

   Nullable and backfilled as NULL: rows written before this migration have
   no reliable point-in-time identity and should not be guessed at. They age
   out of the default 7-day report window quickly.
   ======================================================================== */

IF COL_LENGTH('dbo.PiholeQueries', 'client_mac') IS NULL
BEGIN
    ALTER TABLE dbo.PiholeQueries ADD client_mac varchar(32) NULL;
    PRINT 'added dbo.PiholeQueries.client_mac';
END
GO

IF COL_LENGTH('dbo.PiholeQueries', 'client_hostname') IS NULL
BEGIN
    ALTER TABLE dbo.PiholeQueries ADD client_hostname varchar(255) NULL;
    PRINT 'added dbo.PiholeQueries.client_hostname';
END
GO

IF COL_LENGTH('dbo.PiholeQueries', 'client_vendor') IS NULL
BEGIN
    ALTER TABLE dbo.PiholeQueries ADD client_vendor varchar(128) NULL;
    PRINT 'added dbo.PiholeQueries.client_vendor';
END
GO

-- Every client-facing report groups or filters by this. Without it, each one
-- is a scan of the fact table.
--
-- A filtered index requires QUOTED_IDENTIFIER ON for the session that creates
-- it - sqlcmd defaults it OFF, which fails with Msg 1934 otherwise.
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_PiholeQueries_client_mac_ts'
                 AND object_id = OBJECT_ID('dbo.PiholeQueries'))
BEGIN
    CREATE NONCLUSTERED INDEX ix_PiholeQueries_client_mac_ts
        ON dbo.PiholeQueries (client_mac, ts) WHERE client_mac IS NOT NULL;
    PRINT 'created ix_PiholeQueries_client_mac_ts';
END
GO

-- The loader (pihole_ingest) already has db_datawriter; nothing new to grant
-- there. The report login only ever needed SELECT on the table, which it has.
PRINT '--- coverage ---';
SELECT
    total_rows       = COUNT_BIG(*),
    with_client_mac  = SUM(CASE WHEN client_mac IS NOT NULL THEN 1 ELSE 0 END),
    newest           = MAX(ts)
FROM dbo.PiholeQueries;
GO
