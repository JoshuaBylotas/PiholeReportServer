/* ===========================================================================
   Split the Omada source in two, and stop generic names winning.

   THE FAULT

   The first run resolved two devices to "wlan0". That is the Fire TV reporting
   its own interface name as its DHCP hostname, and because everything from the
   controller was recorded as one source ranked above AD DNS, it beat the far
   more useful firestick-0a0a273294170242 that DNS had.

   The controller carries two different things under one roof:

     name      what someone typed in the controller. Authoritative, because a
               person chose it to identify this device.
     hostName  what the device announced at DHCP. Sometimes excellent
               ("HBG-BYLOTASJ4"), sometimes "wlan0", "android-a1b2c3" or
               "localhost", which identify nothing.

   Ranking those together meant the worst case of the second outranked the best
   case of every other source. So they become two sources:

     manual     stated by hand here                      highest
     omada      a name typed into the controller
     addns      an A record in the AD forward zone
     omadadhcp  a hostname the device announced itself
     ftl        Pi-hole reverse DNS                      lowest

   A device-announced hostname now sits BELOW DNS, which is the honest ordering:
   both are self-reported, but the DNS one has at least survived registration.

   Idempotent.
   ======================================================================== */

SET NOCOUNT ON;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_DNO_source' AND parent_object_id = OBJECT_ID('dbo.DeviceNameObservation'))
BEGIN
    ALTER TABLE dbo.DeviceNameObservation DROP CONSTRAINT CK_DNO_source;
END
GO

ALTER TABLE dbo.DeviceNameObservation WITH CHECK
    ADD CONSTRAINT CK_DNO_source
        CHECK (source IN ('manual', 'omada', 'addns', 'omadadhcp', 'ftl'));
PRINT 'source now allows manual, omada, addns, omadadhcp, ftl';
GO

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
                         WHEN 'omada'     THEN 2   -- typed by a person
                         WHEN 'addns'     THEN 3
                         WHEN 'omadadhcp' THEN 4   -- announced by the device
                         ELSE 5                    -- ftl reverse DNS
                     END,
                     o.observed_utc DESC)
    FROM dbo.DeviceNameObservation AS o
    WHERE LEN(LTRIM(RTRIM(o.name))) > 0
      -- A hostname that is simply the MAC is true and useless.
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]-[0-9a-fA-F][0-9a-fA-F]'
      AND o.name NOT LIKE '[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]:[0-9a-fA-F][0-9a-fA-F]'
      -- Names that identify a network stack rather than a device. Excluded at any
      -- rank: "wlan0" from the controller is not better than "firestick-…" from
      -- DNS, and it is not better than nothing either.
      AND LOWER(LTRIM(RTRIM(o.name))) NOT IN
          ('wlan0', 'wlan1', 'eth0', 'eth1', 'en0', 'en1', 'localhost',
           'localhost.localdomain', 'unknown', 'android', 'device', 'client',
           'dhcp', 'new-host', 'espressif', 'esp32', 'esp8266', '(null)', 'null', '-')
      -- "android-a1b2c3d4e5f6" and friends: a platform plus a random hex tail.
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

PRINT '--- names by winning source ---';
SELECT source, devices = COUNT(*) FROM dbo.vDeviceName GROUP BY source ORDER BY COUNT(*) DESC;

PRINT '--- generic names now excluded (they will fall through to another source) ---';
SELECT o.source, o.name, macs = COUNT(*)
FROM dbo.DeviceNameObservation o
WHERE LOWER(LTRIM(RTRIM(o.name))) IN
      ('wlan0','wlan1','eth0','eth1','en0','en1','localhost','unknown','android','espressif')
GROUP BY o.source, o.name
ORDER BY COUNT(*) DESC;
GO
