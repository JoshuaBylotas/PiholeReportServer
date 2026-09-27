/* ===========================================================================
   One table to reference: MAC, current IP, device name.

   THE FAULT THIS CLOSES

   dbo.vDeviceName answers "what is this device called", resolved across sources
   by precedence. Two things were still missing.

   First, nobody could state the answer. The 'manual' tier existed and outranked
   everything, but there was no place to write it and no row ever used it. Of 64
   MACs only 8 carried a name a person had actually typed - the other 56 were
   whatever the device announced about itself.

   Second, no source held the CURRENT address. Observations carry the address the
   source last saw, refreshed on that source's own schedule, so they drift: addns
   asserted JOSHLT01 at 10.20.0.229 while the live A record said 10.20.0.231.
   Anything joining name to address got one of them, with no way to tell which.

   THE APPROACH

     dbo.DeviceTruth      the name a person states. Its own table, so no
                          collector run can ever overwrite what was typed -
                          observations are replaced per source, this is not.
     dbo.DeviceVendor     OUI vendor, so an unnamed MAC still says "Espressif".
     dbo.vDeviceCurrentIp the address, most recent observation wins.
     dbo.vDeviceTruth     THE view to reference: mac, current_ip, device_name.
     dbo.DnsPushRequest   a queue. The site asks for a DNS push; the agent on
     dbo.DnsPushLog       WINAD02 performs it and writes back what it did.

   WHY A QUEUE RATHER THAN THE SITE WRITING DNS

   The site would need DNS write rights on the zone to do it directly, which
   makes a web application a DNS administrator. Instead the button inserts a
   request and the scheduled agent on WINAD02 - which already runs as a domain
   administrator, and is where the Omada controller lives - performs the writes
   and records the outcome. Every push is attributable and replayable.

   Idempotent.
   ======================================================================== */

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------------
   Network discovery adds sources. nmap/mDNS/SSDP/SNMP/NetBIOS ask the device
   what it calls itself over protocols that carry a name a person chose -
   an Echo publishes "Living Room", a printer answers SNMP sysName - where DHCP
   only ever saw "wlan0".
   --------------------------------------------------------------------------- */
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_DNO_source' AND parent_object_id = OBJECT_ID('dbo.DeviceNameObservation'))
BEGIN
    ALTER TABLE dbo.DeviceNameObservation DROP CONSTRAINT CK_DNO_source;
    PRINT 'dropped CK_DNO_source';
END
GO

ALTER TABLE dbo.DeviceNameObservation WITH CHECK
    ADD CONSTRAINT CK_DNO_source CHECK (source IN
        ('manual', 'omada', 'kasa', 'mdns', 'ssdp', 'snmp', 'netbios',
         'addns', 'omadadhcp', 'nmap', 'ftl'));
GO
PRINT 'CK_DNO_source now allows the discovery sources';
GO

/* ---------------------------------------------------------------------------
   dbo.DeviceTruth - the stated name.

   device_name is constrained to something that can actually become a DNS label,
   because that is where it is going. Single label, 1-63 characters, letters,
   digits, hyphen and underscore, no leading or trailing hyphen.

   Underscore is permitted deliberately. It is not legal in a hostname per
   RFC 1123, but Microsoft DNS accepts it and this zone already contains
   GE_Light_687B and HS210 style names. Forbidding it would mean the existing
   names could not be restated here.

   A name that is merely the MAC is rejected outright - that is the fault being
   fixed, and there is no reason to let it be typed back in by hand.
   --------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.DeviceTruth', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeviceTruth
    (
        mac          varchar(17)   NOT NULL,

        device_name  nvarchar(63)  NOT NULL,

        -- Free text for the human: "kitchen ceiling, behind the cabinet".
        notes        nvarchar(400) NULL,

        -- Whether this device should be published to DNS at all. A guest phone
        -- or a randomised-MAC device is worth naming in reports without being
        -- given an A record that goes stale the moment it leaves.
        publish_dns  bit           NOT NULL CONSTRAINT DF_DeviceTruth_publish DEFAULT 1,

        -- Who stated it. The Entra oid claim and the display name, following
        -- dbo.SavedReports - never taken from the request.
        updated_by_oid  nvarchar(100) NOT NULL,
        updated_by_name nvarchar(200) NULL,
        updated_utc     datetime2(0)  NOT NULL CONSTRAINT DF_DeviceTruth_updated DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_DeviceTruth PRIMARY KEY CLUSTERED (mac),

        CONSTRAINT CK_DeviceTruth_mac CHECK (mac LIKE
            '[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]'),

        -- A legal single DNS label.
        CONSTRAINT CK_DeviceTruth_name_chars CHECK (device_name NOT LIKE '%[^0-9A-Za-z_-]%'),
        CONSTRAINT CK_DeviceTruth_name_edges CHECK (device_name NOT LIKE '-%' AND device_name NOT LIKE '%-'),
        CONSTRAINT CK_DeviceTruth_name_len   CHECK (LEN(device_name) BETWEEN 1 AND 63),

        -- Not a MAC address wearing a name's clothes, in either separator style.
        CONSTRAINT CK_DeviceTruth_not_mac CHECK (
            device_name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]'
        AND device_name NOT LIKE '[0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F]')
    );
    PRINT 'created dbo.DeviceTruth';
END
ELSE
BEGIN
    PRINT 'dbo.DeviceTruth already exists';
END
GO

/* ---------------------------------------------------------------------------
   dbo.DeviceVendor - OUI lookup, populated by the discovery collector from the
   Pi's own macvendor.db. Separate from the observation table because a vendor
   is not a name and must never be ranked as one.
   --------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.DeviceVendor', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeviceVendor
    (
        mac          varchar(17)   NOT NULL,
        vendor       nvarchar(200) NOT NULL,
        observed_utc datetime2(0)  NOT NULL CONSTRAINT DF_DeviceVendor_observed DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_DeviceVendor PRIMARY KEY CLUSTERED (mac),
        CONSTRAINT CK_DeviceVendor_mac CHECK (mac LIKE
            '[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]:[0-9a-f][0-9a-f]')
    );
    PRINT 'created dbo.DeviceVendor';
END
ELSE
BEGIN
    PRINT 'dbo.DeviceVendor already exists';
END
GO

/* ---------------------------------------------------------------------------
   dbo.DnsPushRequest - the queue the button writes to.

   status: 'queued' -> 'running' -> 'done' | 'failed'
   The agent claims a row by moving it to 'running' in a single UPDATE, so two
   agent runs overlapping cannot both act on the same request.

   dry_run exists because the first thing anyone wants from a button that edits
   DNS is to see what it would do without it doing it.
   --------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.DnsPushRequest', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DnsPushRequest
    (
        id             int IDENTITY(1,1) NOT NULL,

        -- NULL means every publishable device; a MAC means just that one.
        mac            varchar(17)   NULL,

        dry_run        bit           NOT NULL CONSTRAINT DF_DPR_dryrun DEFAULT 1,

        status         varchar(10)   NOT NULL CONSTRAINT DF_DPR_status DEFAULT 'queued',

        requested_by_oid  nvarchar(100) NOT NULL,
        requested_by_name nvarchar(200) NULL,
        requested_utc     datetime2(0)  NOT NULL CONSTRAINT DF_DPR_requested DEFAULT SYSUTCDATETIME(),

        claimed_utc    datetime2(0)  NULL,
        completed_utc  datetime2(0)  NULL,

        -- Which host ran it, so a stuck 'running' row can be chased.
        agent_host     nvarchar(100) NULL,

        records_changed int          NULL,
        records_failed  int          NULL,
        message         nvarchar(2000) NULL,

        CONSTRAINT PK_DnsPushRequest PRIMARY KEY CLUSTERED (id),
        CONSTRAINT CK_DPR_status CHECK (status IN ('queued', 'running', 'done', 'failed'))
    );
    PRINT 'created dbo.DnsPushRequest';
END
ELSE
BEGIN
    PRINT 'dbo.DnsPushRequest already exists';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_DPR_queued' AND object_id = OBJECT_ID('dbo.DnsPushRequest'))
BEGIN
    -- The agent's only query: the oldest thing still waiting.
    CREATE NONCLUSTERED INDEX ix_DPR_queued
        ON dbo.DnsPushRequest (status, requested_utc) INCLUDE (mac, dry_run);
    PRINT 'created ix_DPR_queued';
END
GO

/* ---------------------------------------------------------------------------
   dbo.DnsPushLog - what the agent actually did, one row per record touched.

   This is the audit trail for a button that edits DNS. 'removed' rows matter
   most: clearing the OTHER names pointing at an address is how one IP stops
   carrying five names, and it is the part worth being able to review after.
   --------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.DnsPushLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DnsPushLog
    (
        id          bigint IDENTITY(1,1) NOT NULL,
        request_id  int           NOT NULL,
        mac         varchar(17)   NULL,

        -- 'added' | 'updated' | 'removed' | 'skipped' | 'failed'
        action      varchar(10)   NOT NULL,

        -- 'A' | 'PTR' | 'zone' | 'omada'
        record_type varchar(8)    NOT NULL,

        record_name nvarchar(255) NULL,
        ip          varchar(45)   NULL,
        zone        nvarchar(255) NULL,

        -- Why, in words. For 'skipped' and 'failed' this is the whole point.
        detail      nvarchar(1000) NULL,

        acted_utc   datetime2(0)  NOT NULL CONSTRAINT DF_DPL_acted DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_DnsPushLog PRIMARY KEY CLUSTERED (id),
        CONSTRAINT FK_DnsPushLog_request FOREIGN KEY (request_id)
            REFERENCES dbo.DnsPushRequest (id),
        CONSTRAINT CK_DPL_action CHECK (action IN ('added','updated','removed','skipped','failed')),
        CONSTRAINT CK_DPL_type   CHECK (record_type IN ('A','PTR','zone'))
    );
    PRINT 'created dbo.DnsPushLog';
END
ELSE
BEGIN
    PRINT 'dbo.DnsPushLog already exists';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'ix_DPL_request' AND object_id = OBJECT_ID('dbo.DnsPushLog'))
BEGIN
    CREATE NONCLUSTERED INDEX ix_DPL_request ON dbo.DnsPushLog (request_id, id);
    PRINT 'created ix_DPL_request';
END
GO

/* ---------------------------------------------------------------------------
   dbo.vDeviceName - precedence extended for the discovery sources.

   The existing order is preserved exactly; the new sources are inserted between
   'omada' and 'addns' so that no existing pair of sources changes places. A
   device's resolved name can therefore only change if a discovery source has an
   opinion where previously nothing did - or where only the device's own DHCP
   announcement did.

     1 manual     stated by a person here. Nothing outranks it.
     2 omada      typed into the controller by a person.
     3 kasa       the alias typed into the Kasa app when the device was
                installed. A stated name, straight from the device.
     3 mdns       the device publishes it over Bonjour: "Living Room".
     4 ssdp       UPnP friendlyName, which is how a TV or a plug says what it is.
     5 snmp       sysName. Printers and switches answer this and mean it.
     6 netbios    the Windows name, from the node itself.
     7 addns      an A record in the AD forward zone.
     8 omadadhcp  a hostname the device announced at DHCP. "wlan0" lives here.
     9 nmap       reverse DNS seen during the sweep. Same rot as ftl.
    10 ftl        Pi-hole reverse DNS. The source of ALIEN01.

   mDNS and SSDP outrank AD DNS deliberately: both are names the vendor or the
   owner set on the device, whereas the A record for an IoT device is whatever
   junk its DHCP announcement registered. For domain-joined machines nothing
   changes - they publish no mDNS and their A record still wins.
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
                         WHEN 'manual'    THEN 1
                         WHEN 'omada'     THEN 2
                         WHEN 'kasa'      THEN 3
                         WHEN 'mdns'      THEN 4
                         WHEN 'ssdp'      THEN 5
                         WHEN 'snmp'      THEN 6
                         WHEN 'netbios'   THEN 7
                         WHEN 'addns'     THEN 8
                         WHEN 'omadadhcp' THEN 9
                         WHEN 'nmap'      THEN 10
                         ELSE 11
                     END,
                     o.observed_utc DESC)
    FROM dbo.DeviceNameObservation AS o
    WHERE LEN(LTRIM(RTRIM(o.name))) > 0
      -- A hostname that is simply the MAC, in either separator style.
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]'
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]'
      -- And the separatorless form, which is how SSDP and mDNS spell it:
      -- b0be76b2c722 is no more a name than b0:be:76:b2:c7:22 is.
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F][0-9a-fA-F]'
      -- Names that identify a network stack rather than a device.
      AND LOWER(LTRIM(RTRIM(o.name))) NOT IN
          ('wlan0', 'wlan1', 'eth0', 'eth1', 'en0', 'en1', 'localhost',
           'localhost.localdomain', 'unknown', 'android', 'device', 'client',
           'dhcp', 'new-host', 'espressif', 'esp32', 'esp8266', '(null)', 'null', '-')
      AND LOWER(o.name) NOT LIKE 'android-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]%'
)
SELECT
    mac,
    name,
    source,
    ip,
    device_type,
    observed_utc,
    sources_offering = (SELECT COUNT(*) FROM dbo.DeviceNameObservation d
                        WHERE d.mac = ranked.mac AND LEN(LTRIM(RTRIM(d.name))) > 0),
    distinct_names   = (SELECT COUNT(DISTINCT d.name) FROM dbo.DeviceNameObservation d
                        WHERE d.mac = ranked.mac AND LEN(LTRIM(RTRIM(d.name))) > 0)
FROM ranked
WHERE rank_in_mac = 1;
GO

/* ---------------------------------------------------------------------------
   dbo.vDeviceCurrentIp - where the device is now.

   Most recent observation that carried an address wins, whatever said it. That
   is the honest rule: an ARP sweep from five minutes ago is better evidence
   than a lease table from this morning, and vice versa. The source and the
   timestamp come along so a surprising address can be traced.
   --------------------------------------------------------------------------- */
CREATE OR ALTER VIEW dbo.vDeviceCurrentIp
AS
WITH ranked AS
(
    SELECT
        o.mac,
        o.ip,
        o.source,
        o.observed_utc,
        rank_in_mac = ROW_NUMBER() OVER (
            PARTITION BY o.mac
            ORDER BY o.observed_utc DESC,
                     -- Same instant, two sources: prefer the one that saw the
                     -- wire over the one that read a table.
                     CASE o.source
                         WHEN 'nmap'      THEN 1
                         WHEN 'kasa'      THEN 2
                         WHEN 'mdns'      THEN 3
                         WHEN 'ssdp'      THEN 4
                         WHEN 'netbios'   THEN 5
                         WHEN 'snmp'      THEN 6
                         WHEN 'omadadhcp' THEN 7
                         WHEN 'omada'     THEN 8
                         WHEN 'addns'     THEN 9
                         ELSE 10
                     END)
    FROM dbo.DeviceNameObservation AS o
    WHERE o.ip IS NOT NULL
      AND LEN(LTRIM(RTRIM(o.ip))) > 0
)
SELECT mac, ip, ip_source = source, ip_observed_utc = observed_utc
FROM ranked
WHERE rank_in_mac = 1;
GO

/* ---------------------------------------------------------------------------
   dbo.vDeviceTruth - THE view to reference.

   One row per known MAC: what it is called, where it is, and who says so.

   device_name falls back: the stated name, else the resolved name from
   precedence, else NULL. It is never a MAC and never "wlan0", because both are
   filtered out of vDeviceName rather than allowed to win.

   needs_review is the Editor's worklist: no stated name, and the best the
   sources could offer was the device talking about itself - or nothing at all.

   ad_dns_agrees compares against the LAST addns COLLECTION, not the live zone.
   It is a reconciliation hint, not proof: the collector runs on its own
   schedule, which is exactly how JOSHLT01 came to be asserted at .229 here
   while the live A record said .231. The push agent reads the live zone.
   --------------------------------------------------------------------------- */
CREATE OR ALTER VIEW dbo.vDeviceTruth
AS
WITH macs AS
(
    SELECT mac FROM dbo.DeviceNameObservation
    UNION
    SELECT mac FROM dbo.DeviceTruth
    UNION
    SELECT mac FROM dbo.DeviceVendor
),
ad AS
(
    SELECT mac, ad_name = name, ad_ip = ip
    FROM dbo.DeviceNameObservation
    WHERE source = 'addns'
)
SELECT
    m.mac,

    -- The answer.
    device_name = COALESCE(NULLIF(LTRIM(RTRIM(t.device_name)), ''), n.name),

    current_ip  = c.ip,

    -- Where each half of the answer came from.
    name_source = CASE
                      WHEN NULLIF(LTRIM(RTRIM(t.device_name)), '') IS NOT NULL THEN 'manual'
                      WHEN n.name IS NOT NULL THEN n.source
                      ELSE 'none'
                  END,
    c.ip_source,
    c.ip_observed_utc,

    vendor      = v.vendor,
    device_type = n.device_type,

    -- CAST to bit deliberately: ISNULL(bit, 0) returns INT by data type
    -- precedence, and a caller reading this column with GetBoolean then throws
    -- InvalidCastException. Same for the two CASE expressions below - a CASE
    -- returning 1/0 is an int no matter what it is compared against.
    publish_dns = CAST(ISNULL(t.publish_dns, 0) AS bit),
    notes       = t.notes,

    stated_by   = t.updated_by_name,
    stated_utc  = t.updated_utc,

    -- Reconciliation against AD DNS as last collected.
    ad_dns_name = a.ad_name,
    ad_dns_ip   = a.ad_ip,
    ad_dns_agrees = CAST(CASE
                             WHEN a.ad_name IS NULL THEN NULL
                             WHEN a.ad_name = COALESCE(NULLIF(LTRIM(RTRIM(t.device_name)), ''), n.name)
                              AND a.ad_ip   = c.ip THEN 1
                             ELSE 0
                         END AS bit),

    -- How much disagreement sits behind this row.
    candidate_names = ISNULL(n.distinct_names, 0),

    needs_review = CAST(CASE
                            WHEN NULLIF(LTRIM(RTRIM(t.device_name)), '') IS NOT NULL THEN 0
                            WHEN n.source IN ('manual','omada','kasa','mdns','ssdp','snmp','netbios','addns') THEN 0
                            ELSE 1
                        END AS bit)
FROM macs AS m
     LEFT JOIN dbo.DeviceTruth      AS t ON t.mac = m.mac
     LEFT JOIN dbo.vDeviceName      AS n ON n.mac = m.mac
     LEFT JOIN dbo.vDeviceCurrentIp AS c ON c.mac = m.mac
     LEFT JOIN dbo.DeviceVendor     AS v ON v.mac = m.mac
     LEFT JOIN ad                   AS a ON a.mac = m.mac;
GO

/* ---------------------------------------------------------------------------
   Grants.

   The site reads everything and writes only what a person states and the
   requests they raise. It is given no rights over the observation tables - the
   collectors own those - and none at all over DnsPushLog, which only the agent
   writes.
   --------------------------------------------------------------------------- */
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    GRANT SELECT ON dbo.vDeviceTruth     TO pihole_report_ro;
    GRANT SELECT ON dbo.vDeviceCurrentIp TO pihole_report_ro;
    GRANT SELECT ON dbo.DeviceVendor     TO pihole_report_ro;
    GRANT SELECT ON dbo.DnsPushLog       TO pihole_report_ro;

    -- The Editor.
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.DeviceTruth TO pihole_report_ro;

    -- The button. No UPDATE: the site raises a request, the agent owns its
    -- lifecycle, and the site only ever reads back the outcome.
    GRANT SELECT, INSERT ON dbo.DnsPushRequest TO pihole_report_ro;

    PRINT 'granted to pihole_report_ro';
END
ELSE
BEGIN
    PRINT 'pihole_report_ro not found - grants skipped';
END
GO

/* ---------------------------------------------------------------------------
   The push agent runs on WINAD02 as SYSTEM, which reaches SQL as the machine
   account BYLOTAS\WINAD02$ - the same principal the collectors already use, and
   already granted narrowly rather than through a role.

   It is deliberately NOT given write access to DeviceTruth. The agent publishes
   what a person stated; it has no business changing what was stated.
   --------------------------------------------------------------------------- */
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'BYLOTAS\WINAD02$')
BEGIN
    GRANT SELECT ON dbo.vDeviceTruth     TO [BYLOTAS\WINAD02$];
    GRANT SELECT ON dbo.vDeviceCurrentIp TO [BYLOTAS\WINAD02$];
    GRANT SELECT ON dbo.DeviceVendor     TO [BYLOTAS\WINAD02$];

    -- Claims a request and reports the outcome. No INSERT: the agent does not
    -- raise work for itself.
    GRANT SELECT, UPDATE ON dbo.DnsPushRequest TO [BYLOTAS\WINAD02$];

    -- Append-only. The log is evidence, so the agent may add to it and nothing
    -- more - it cannot rewrite what a previous run recorded.
    GRANT INSERT, SELECT ON dbo.DnsPushLog TO [BYLOTAS\WINAD02$];

    PRINT 'granted to BYLOTAS\WINAD02$ (push agent)';
END
ELSE
BEGIN
    PRINT 'BYLOTAS\WINAD02$ not found - agent grants skipped';
END
GO
/* ---------------------------------------------------------------------------
   The push also writes the stated name back into the Omada controller, so the
   name a person chose is what the controller shows instead of the model string
   ("HS210", "KL125", "HS105"). That needs its own record_type in the log.

   Verified against the controller's OpenAPI: PATCH
   /openapi/v1/{omadacId}/sites/{siteId}/clients/{mac}/name is the only writable
   route that matters here. There is NO route for the DHCP address reservation
   (ipSetting.useFixedAddr is readable and not writable) and no description or
   location route, so those stay manual.

   Note on provenance: once written, Collect-OmadaNames reads that name back as
   source 'omada' - "typed by a person" - which it now is, but by us. It is an
   echo of dbo.DeviceTruth rather than independent corroboration. 'manual' still
   outranks it, so nothing changes in resolution.
   --------------------------------------------------------------------------- */
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_DPL_type' AND parent_object_id = OBJECT_ID('dbo.DnsPushLog'))
BEGIN
    ALTER TABLE dbo.DnsPushLog DROP CONSTRAINT CK_DPL_type;
    PRINT 'dropped CK_DPL_type';
END
GO

-- varchar(4) fitted 'A', 'PTR' and 'zone' exactly. 'omada' is five characters,
-- so SQL truncated it to 'omad', failed the constraint, and every controller
-- write logged as an error while the write itself had succeeded.
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.DnsPushLog')
             AND name = 'record_type' AND max_length < 8)
BEGIN
    ALTER TABLE dbo.DnsPushLog ALTER COLUMN record_type varchar(8) NOT NULL;
    PRINT 'widened DnsPushLog.record_type to varchar(8)';
END
GO

ALTER TABLE dbo.DnsPushLog WITH CHECK
    ADD CONSTRAINT CK_DPL_type CHECK (record_type IN ('A', 'PTR', 'zone', 'omada'));
GO
PRINT 'CK_DPL_type now allows the omada record type';
GO
PRINT '--- where naming stands ---';
SELECT
    devices        = COUNT(*),
    stated         = SUM(CASE WHEN name_source = 'manual' THEN 1 ELSE 0 END),
    from_a_person  = SUM(CASE WHEN name_source IN ('manual','omada','kasa') THEN 1 ELSE 0 END),
    self_reported  = SUM(CASE WHEN name_source IN ('omadadhcp','nmap','ftl') THEN 1 ELSE 0 END),
    unnamed        = SUM(CASE WHEN name_source = 'none' THEN 1 ELSE 0 END),
    needs_review   = SUM(CAST(needs_review AS int)),
    no_current_ip  = SUM(CASE WHEN current_ip IS NULL THEN 1 ELSE 0 END)
FROM dbo.vDeviceTruth;
GO
