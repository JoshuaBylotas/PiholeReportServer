/* ===========================================================================
   Device naming from authoritative sources.

   THE PROBLEM

   DimClient.name comes from FTL's reverse DNS, and reverse DNS on this network
   is not trustworthy: one stale PTR resolves 37 addresses across 34 devices to
   "ALIEN01". 109 of 220 DimClient rows are flagged name_ambiguous.

   AD DNS is not a fix on its own - it is fed by the same dynamic registration,
   so it has the same rot. Of 136 A records in bylotas.net, 127 are dynamic, many
   register the client's own MAC as its hostname (00-A5-54-1F-8B-E1), and one
   address is claimed by four names at once because the lease was reused:

     10.20.1.2 -> 06-CA-8B-D7-ED-EA, Cynthia-s-S20-FE, HOME-JASON, iPhone

   THE APPROACH

   Collect names from every source that has an opinion, keyed by MAC - the only
   identifier that survives a DHCP change - and resolve them by a stated
   precedence rather than trusting whichever wrote last.

     dbo.DeviceNameObservation   what each source says, one row per (mac, source)
     dbo.vDeviceName             the resolved winner per MAC, with its reason
     dbo.vClient                 DimClient with the resolved name attached

   Observations are replaced per source on each collection, so a device removed
   from the controller stops being asserted by it rather than lingering forever.
   ======================================================================== */

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.DeviceNameObservation', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeviceNameObservation
    (
        -- Normalised lower-case colon form, e.g. e0:d3:62:94:9b:90. The collectors
        -- normalise; the constraint below is the backstop.
        mac          varchar(17)   NOT NULL,

        -- 'omada'   the controller: DHCP server AND where devices are named by hand
        -- 'addns'   an A record in the AD forward zone
        -- 'ftl'     Pi-hole reverse DNS, the current source, kept for comparison
        -- 'manual'  set by hand in this table, and it wins
        source       varchar(10)   NOT NULL,

        name         nvarchar(255) NOT NULL,

        -- Last address the source saw for this MAC. Informational: the MAC is the key.
        ip           varchar(45)   NULL,

        -- What the source itself calls this device's kind, when it says.
        device_type  nvarchar(60)  NULL,

        observed_utc datetime2(0)  NOT NULL CONSTRAINT DF_DNO_observed DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_DeviceNameObservation PRIMARY KEY CLUSTERED (mac, source),
        CONSTRAINT CK_DNO_source CHECK (source IN ('omada', 'addns', 'ftl', 'manual')),
        CONSTRAINT CK_DNO_mac CHECK (mac LIKE '[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]')
    );
    PRINT 'created dbo.DeviceNameObservation';
END
ELSE
BEGIN
    PRINT 'dbo.DeviceNameObservation already exists';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_DNO_source' AND object_id = OBJECT_ID('dbo.DeviceNameObservation'))
BEGIN
    CREATE NONCLUSTERED INDEX ix_DNO_source
        ON dbo.DeviceNameObservation (source, observed_utc) INCLUDE (mac, name);
    PRINT 'created ix_DNO_source';
END
GO

/* ---------------------------------------------------------------------------
   dbo.vDeviceName — the resolved name per MAC.

   Precedence, and why:

     1 manual  someone stated it deliberately. Nothing outranks that.
     2 omada   the controller is the DHCP server, so it sees the name the device
               asked for, and it is where devices get named by hand. It is also
               the only source keyed on MAC natively rather than by inference.
     3 addns   an A record. Authoritative for domain-joined machines, which are
               the ones that matter most and the ones FTL gets wrong least often
               - but see the exclusions below.
     4 ftl     Pi-hole reverse DNS. Last, because it is the source that produced
               the ALIEN01 collision in the first place.

   A name that is just a MAC address is not a name. Several clients register
   00-A5-54-1F-8B-E1 as their hostname, which is true and useless, so those are
   excluded rather than allowed to outrank a real name from a lower source.
   --------------------------------------------------------------------------- */
CREATE OR ALTER VIEW dbo.vDeviceName
AS
WITH ranked AS
(
    SELECT
        o.mac,
        o.name,
        o.source,
        o.ip,
        o.device_type,
        o.observed_utc,
        rank_in_mac = ROW_NUMBER() OVER (
            PARTITION BY o.mac
            ORDER BY CASE o.source
                         WHEN 'manual' THEN 1
                         WHEN 'omada'  THEN 2
                         WHEN 'addns'  THEN 3
                         ELSE 4
                     END,
                     -- Tie-break on freshness, though the source ordering above is
                     -- total so this only matters if a source is added later.
                     o.observed_utc DESC)
    FROM dbo.DeviceNameObservation AS o
    WHERE LEN(LTRIM(RTRIM(o.name))) > 0
      -- A hostname that is simply the MAC, in either separator style. Excluded
      -- here rather than at collection time so the raw observation is still
      -- visible when working out why a device is named what it is.
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]'
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]'
)
SELECT
    mac,
    name,
    -- Which source won, so a surprising name can be traced without guessing.
    source,
    ip,
    device_type,
    observed_utc,
    -- How many sources offered a name, and whether they agreed. Disagreement is
    -- normal (a device renamed in the controller still has an old A record) but
    -- worth being able to see.
    sources_offering = (SELECT COUNT(*) FROM dbo.DeviceNameObservation d
                        WHERE d.mac = ranked.mac AND LEN(LTRIM(RTRIM(d.name))) > 0),
    distinct_names   = (SELECT COUNT(DISTINCT d.name) FROM dbo.DeviceNameObservation d
                        WHERE d.mac = ranked.mac AND LEN(LTRIM(RTRIM(d.name))) > 0)
FROM ranked
WHERE rank_in_mac = 1;
GO

/* ---------------------------------------------------------------------------
   dbo.vClient — DimClient with a name worth using.

   display_name falls back through the resolved name, then FTL's, then the raw
   address, so it is never null and a report can group on it unconditionally.
   --------------------------------------------------------------------------- */
CREATE OR ALTER VIEW dbo.vClient
AS
SELECT
    c.ip,
    c.mac,
    c.mac_vendor,
    c.interface,
    c.num_queries,
    c.last_query,

    -- The one to display and group on.
    display_name = COALESCE(NULLIF(LTRIM(RTRIM(n.name)), ''),
                            NULLIF(LTRIM(RTRIM(c.name)), ''),
                            c.ip),
    name_source  = COALESCE(n.source, CASE WHEN NULLIF(LTRIM(RTRIM(c.name)), '') IS NOT NULL
                                           THEN 'ftl' ELSE 'ip' END),

    -- What FTL thought, kept so the improvement is auditable.
    ftl_name        = c.name,
    reported_name   = c.reported_name,
    name_ambiguous  = c.name_ambiguous
FROM dbo.DimClient AS c
     LEFT JOIN dbo.vDeviceName AS n ON n.mac = c.mac;
GO

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    GRANT SELECT ON dbo.vDeviceName TO pihole_report_ro;
    GRANT SELECT ON dbo.vClient     TO pihole_report_ro;
    -- The collectors run as this login too.
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.DeviceNameObservation TO pihole_report_ro;
    PRINT 'granted on dbo.vDeviceName, dbo.vClient, dbo.DeviceNameObservation';
END
GO

PRINT '--- coverage ---';
SELECT source, macs = COUNT(*) FROM dbo.DeviceNameObservation GROUP BY source ORDER BY source;

SELECT
    devices_with_mac  = COUNT(*),
    named_from_source = SUM(CASE WHEN name_source IN ('manual','omada','addns') THEN 1 ELSE 0 END),
    still_ftl         = SUM(CASE WHEN name_source = 'ftl' THEN 1 ELSE 0 END),
    unnamed           = SUM(CASE WHEN name_source = 'ip'  THEN 1 ELSE 0 END)
FROM dbo.vClient
WHERE mac IS NOT NULL;
GO
