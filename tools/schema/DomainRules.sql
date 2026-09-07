/*
    dbo.DomainRule — hand-curated categorisation rules.

    These exist because the public corpora (UT1, Blocklist Project) categorise
    *bad* domains and have nothing to say about the traffic that actually
    dominates a home network: cloud, telemetry, updates and IoT. Measured on this
    warehouse, UT1 alone covered 17% of query volume; the top 100 base domains
    are 90% of it, and almost all of them are things a rule can name exactly.

    A rule is a suffix match: 'akamaiedge.net' matches any host ending in
    '.akamaiedge.net' and the bare domain itself. Longest match wins, so a
    specific rule beats a general one.

    Precedence when resolving DomainCategory:
        manual > rule > ut1 > blocklistproject > pihole adlist > model
    Rules outrank the corpora deliberately: they are curated for THIS network,
    where a generic corpus is not.

    Idempotent: safe to re-run. Re-running replaces the seeded rules but leaves
    any rule marked is_custom = 1 alone.
*/

SET NOCOUNT ON;
GO

IF OBJECT_ID('dbo.DomainRule', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DomainRule
    (
        suffix      varchar(253) NOT NULL,
        category    varchar(32)  NOT NULL,
        subcategory varchar(64)  NULL,
        note        varchar(200) NULL,
        is_custom   bit          NOT NULL CONSTRAINT DF_DomainRule_custom DEFAULT (0),
        CONSTRAINT PK_DomainRule PRIMARY KEY CLUSTERED (suffix)
    );
    PRINT 'created dbo.DomainRule';
END
GO

-- Replace the seeded set, preserve anything hand-added.
DELETE FROM dbo.DomainRule WHERE is_custom = 0;
GO

INSERT INTO dbo.DomainRule (suffix, category, subcategory, note) VALUES
-- ── Local and organisational ────────────────────────────────────────────────
 ('bylotas.net',              'local',          'own network',        'This network''s own domain'),
 ('bylotas.com',              'local',          'own network',        NULL),
 ('tcore.com',                'work',           'employer',           'Work domain, not household traffic'),
 ('transcore.com',            'work',           'employer',           NULL),
 ('tcroot.net',               'work',           'employer infra',     NULL),

-- ── Reverse DNS and protocol plumbing ───────────────────────────────────────
 ('in-addr.arpa',             'infrastructure', 'reverse DNS',        'PTR lookups, not user traffic'),
 ('ip6.arpa',                 'infrastructure', 'reverse DNS',        NULL),
 ('arpa',                     'infrastructure', 'protocol',           NULL),
 ('local',                    'infrastructure', 'mDNS',               NULL),
 ('home.arpa',                'infrastructure', 'local naming',       NULL),

-- ── Time ────────────────────────────────────────────────────────────────────
 ('ntp.org',                  'infrastructure', 'time sync',          NULL),
 ('nist.gov',                 'infrastructure', 'time sync',          'time.nist.gov'),
 ('windows.net',              'cloud',          'Azure',              NULL),

-- ── CDN and edge ────────────────────────────────────────────────────────────
 ('akamaiedge.net',           'infrastructure', 'CDN',                NULL),
 ('akadns.net',               'infrastructure', 'CDN',                NULL),
 ('akamai.net',               'infrastructure', 'CDN',                NULL),
 ('akamaized.net',            'infrastructure', 'CDN',                NULL),
 ('cloudfront.net',           'infrastructure', 'CDN',                NULL),
 ('fastly.net',               'infrastructure', 'CDN',                NULL),
 ('cloudflare.com',           'infrastructure', 'CDN',                NULL),
 ('cloudflare.net',           'infrastructure', 'CDN',                NULL),
 ('gstatic.com',              'infrastructure', 'CDN',                'Google static assets'),
 ('ggpht.com',                'infrastructure', 'CDN',                NULL),
 ('trafficmanager.net',       'infrastructure', 'CDN',                'Azure Traffic Manager'),
 ('ln-msedge.net',            'infrastructure', 'CDN',                'Microsoft edge network'),
 ('msedge.net',               'infrastructure', 'CDN',                NULL),
 ('apple-dns.net',            'infrastructure', 'CDN',                NULL),
 ('aaplimg.com',              'infrastructure', 'CDN',                'Apple image CDN'),
 ('edgekey.net',              'infrastructure', 'CDN',                NULL),
 ('edgesuite.net',            'infrastructure', 'CDN',                NULL),
 ('steamserver.net',          'infrastructure', 'CDN',                'Steam content delivery'),
 ('llnwd.net',                'infrastructure', 'CDN',                NULL),

-- ── Cloud platform and SaaS ─────────────────────────────────────────────────
 ('amazonaws.com',            'cloud',          'AWS',                NULL),
 ('aws.dev',                  'cloud',          'AWS',                NULL),
 ('amazon.dev',               'cloud',          'AWS',                NULL),
 ('a2z.com',                  'cloud',          'Amazon infra',       NULL),
 ('azure.com',                'cloud',          'Azure',              NULL),
 ('azureedge.net',            'cloud',          'Azure',              NULL),
 ('arcdataservices.com',      'cloud',          'Azure Arc',          NULL),
 ('googleapis.com',           'cloud',          'Google APIs',        NULL),
 ('microsoftonline.com',      'cloud',          'Entra ID / M365',    NULL),
 ('office.com',               'cloud',          'Microsoft 365',      NULL),
 ('office.net',               'cloud',          'Microsoft 365',      NULL),
 ('live.com',                 'cloud',          'Microsoft account',  NULL),
 ('live.net',                 'cloud',          'Microsoft account',  NULL),
 ('sharepoint.com',           'cloud',          'Microsoft 365',      NULL),
 ('zoho.com',                 'cloud',          'SaaS',               NULL),
 ('icloud.com',               'cloud',          'Apple iCloud',       NULL),

-- ── Software and updates ────────────────────────────────────────────────────
 ('microsoft.com',            'software',       'vendor',             NULL),
 ('windowsupdate.com',        'software',       'updates',            NULL),
 ('windows.com',              'software',       'updates',            NULL),
 ('ubuntu.com',               'software',       'updates',            NULL),
 ('canonical.com',            'software',       'updates',            NULL),
 ('debian.org',               'software',       'updates',            NULL),
 ('apple.com',                'software',       'vendor',             NULL),
 ('mozilla.org',              'software',       'vendor',             NULL),
 ('mozilla.net',              'software',       'vendor',             NULL),
 ('github.com',               'software',       'development',        NULL),
 ('githubusercontent.com',    'software',       'development',        NULL),
 ('nvidia.com',               'software',       'vendor',             NULL),
 ('adobe.com',                'software',       'vendor',             NULL),

-- ── Gaming ──────────────────────────────────────────────────────────────────
 ('xboxlive.com',             'gaming',         'Xbox',               NULL),
 ('gamepass.com',             'gaming',         'Xbox',               NULL),
 ('xbox.com',                 'gaming',         'Xbox',               NULL),
 ('steampowered.com',         'gaming',         'Steam',              NULL),
 ('steamcommunity.com',       'gaming',         'Steam',              NULL),
 ('minecraft.net',            'gaming',         'Minecraft',          NULL),
 ('mojang.com',               'gaming',         'Minecraft',          NULL),
 ('epicgames.com',            'gaming',         'Epic',               NULL),
 ('nintendo.net',             'gaming',         'Nintendo',           NULL),
 ('playstation.net',          'gaming',         'PlayStation',        NULL),

-- ── Streaming and media ─────────────────────────────────────────────────────
 ('netflix.com',              'streaming',      'video',              NULL),
 ('nflxvideo.net',            'streaming',      'video CDN',          NULL),
 ('nflxso.net',               'streaming',      'video',              NULL),
 ('googlevideo.com',          'streaming',      'YouTube CDN',        NULL),
 ('youtube.com',              'streaming',      'video',              NULL),
 ('ytimg.com',                'streaming',      'video',              NULL),
 ('spotify.com',              'streaming',      'audio',              NULL),
 ('scdn.co',                  'streaming',      'audio CDN',          NULL),
 ('hulu.com',                 'streaming',      'video',              NULL),
 ('disney-plus.net',          'streaming',      'video',              NULL),
 ('primevideo.com',           'streaming',      'video',              NULL),
 ('roku.com',                 'streaming',      'device',             NULL),
 ('plex.tv',                  'streaming',      'self-hosted media',  NULL),

-- ── Voice assistants and IoT ────────────────────────────────────────────────
 ('amazonalexa.com',          'iot',            'voice assistant',    NULL),
 ('alexa.com',                'iot',            'voice assistant',    NULL),
 ('ring.com',                 'iot',            'camera / doorbell',  NULL),
 ('vesync.com',               'iot',            'smart home',         NULL),
 ('tplinkcloud.com',          'iot',            'smart home',         NULL),
 ('tplinkra.com',             'iot',            'smart home',         NULL),
 ('samsungcloudsolution.com', 'iot',            'Samsung device',     NULL),
 ('samsungqbe.com',           'iot',            'Samsung TV',         NULL),
 ('samsungnyc.com',           'iot',            'Samsung device',     NULL),
 ('samsungiotcloud.com',      'iot',            'Samsung device',     NULL),
 ('samsungosp.com',           'iot',            'Samsung device',     NULL),
 ('smartthings.com',          'iot',            'smart home',         NULL),
 ('nest.com',                 'iot',            'smart home',         NULL),
 ('wyze.com',                 'iot',            'camera',             NULL),
 ('ecobee.com',               'iot',            'thermostat',         NULL),
 ('hp.com',                   'iot',            'printer',            NULL),
 ('brother.com',              'iot',            'printer',            NULL),

-- ── Captive portal / connectivity checks ────────────────────────────────────
 ('avsxappcaptiveportal.com',  'infrastructure', 'captive portal check', 'Amazon device connectivity probe'),
 ('mmechocaptiveportal.com',   'infrastructure', 'captive portal check', 'Echo connectivity probe'),
 ('msftconnecttest.com',       'infrastructure', 'connectivity check',   NULL),
 ('msftncsi.com',              'infrastructure', 'connectivity check',   NULL),
 ('connectivitycheck.gstatic.com','infrastructure','connectivity check', NULL),

-- ── Advertising and analytics ───────────────────────────────────────────────
 ('doubleclick.net',          'advertising',    'ad network',         NULL),
 ('googleadservices.com',     'advertising',    'ad network',         NULL),
 ('googlesyndication.com',    'advertising',    'ad network',         NULL),
 ('amazon-adsystem.com',      'advertising',    'ad network',         NULL),
 ('adnxs.com',                'advertising',    'ad network',         NULL),
 ('nr-data.net',              'analytics',      'New Relic',          NULL),
 ('google-analytics.com',     'analytics',      'Google Analytics',   NULL),
 ('scorecardresearch.com',    'analytics',      'measurement',        NULL),
 ('branch.io',                'analytics',      'attribution',        NULL),

-- ── Search, social, shopping, comms ─────────────────────────────────────────
 ('google.com',               'search',         'search',             NULL),
 ('bing.com',                 'search',         'search',             NULL),
 ('duckduckgo.com',           'search',         'search',             NULL),
 ('facebook.com',             'social',         'social network',     NULL),
 ('fbcdn.net',                'social',         'social CDN',         NULL),
 ('instagram.com',            'social',         'social network',     NULL),
 ('twitter.com',              'social',         'social network',     NULL),
 ('reddit.com',               'social',         'social network',     NULL),
 ('amazon.com',               'shopping',       'retail',             NULL),
 ('ebay.com',                 'shopping',       'retail',             NULL),
 ('skype.com',                'communication',  'voice / video',      NULL),
 ('teams.microsoft.com',      'communication',  'voice / video',      NULL),
 ('zoom.us',                  'communication',  'voice / video',      NULL);
GO

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'pihole_report_ro')
BEGIN
    -- Read for reporting; write so a category can be corrected from the UI.
    GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.DomainRule TO pihole_report_ro;
END
GO

SELECT 'rules=' + FORMAT(COUNT(*),'N0') AS r FROM dbo.DomainRule;
GO
