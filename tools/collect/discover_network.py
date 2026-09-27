#!/usr/bin/env python3
"""Ask the devices themselves what they are called.

Runs on linserver05, which is on the same layer 2 as everything it scans - that
is the whole reason it runs there. ARP only answers on-link, and an ARP reply is
the only thing on this network that ties an address to a MAC without trusting a
lease table or a DNS record.

WHY THIS EXISTS

The controller knows what a device announced at DHCP, which for most IoT gear is
useless: "wlan0", "espressif", or its own MAC. But the same device will happily
tell you its real name over a protocol nobody was asking:

    mDNS      an Echo publishes "Living Room"; Apple gear publishes the name
              its owner typed during setup
    SSDP      UPnP friendlyName - how a TV, a Roku or a smart plug identifies
              itself to anything that asks
    SNMP      sysName. Printers and switches answer this and mean it
    NetBIOS   the Windows name, from the node rather than from DNS

Each becomes a source in dbo.DeviceNameObservation, ranked in dbo.vDeviceName
above AD DNS but below a name a person typed.

The sweep also produces the only trustworthy current address, which is what
dbo.vDeviceCurrentIp is built on.

Observations are REPLACED per source on each run, matching the other collectors:
a device that stops answering mDNS stops being asserted by mDNS rather than
lingering forever.
"""
import argparse
import configparser
import ipaddress
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

import pymssql

CONFIG = "/etc/pihole-sqlsync/config.ini"

# Names that are true and useless. The SQL view filters these too - doing it
# here as well keeps the raw table smaller without hiding anything that matters,
# since a name in this list identifies nothing by definition.
JUNK = {
    "wlan0", "wlan1", "eth0", "eth1", "en0", "en1", "localhost",
    "localhost.localdomain", "unknown", "device", "client", "dhcp",
    "new-host", "espressif", "esp32", "esp8266", "null", "(null)", "-",
    "android", "raspberrypi",
    # Seen on this network: Amazon devices publish "amzn" or "none" as their
    # mDNS instance name. Two different devices both answered "amzn".
    "none", "amzn", "amazon",
    # An operating system is not a device name. Three devices published
    # "linux-7", "linux-27" and "linux-2183" in their mDNS TXT records.
    "linux", "ubuntu", "debian", "windows", "macbook", "iphone", "ipad",
}

# A name that is just an address. Some mDNS TXT records set name= to the
# device's own IP, which cleans up to "10-20-0-120" and looks like a name
# without being one.
IPV4_NAME_RE = re.compile(r"^\d{1,3}[-.]\d{1,3}[-.]\d{1,3}[-.]\d{1,3}$")

# Service names that several unrelated devices all publish. "SpotifyConnect"
# came back from three different devices in one sweep: it names a capability,
# not a device, and letting it through would give three machines one name.
JUNK_PREFIX = ("spotifyconnect", "nearby-presence", "airplay", "googlecast",
               "chromecast-", "matter-",
               # A platform name plus a serial: "amazon-1210-45928",
               # "Android_2DID32T3". The platform is not the device.
               "amazon-", "amzn", "android-", "android_")

# avahi appends -2, -3 when two devices publish the same instance name. The
# suffix is avahi's, not the device's, so it is removed before the name is
# judged: "none-3" and "Android-3" are the junk names "none" and "Android".
# Only for the junk test - a real name like "PI5-03-3" keeps its suffix.
AVAHI_DEDUPE_SUFFIX_RE = re.compile(r"-\d{1,3}$")

MAC_RE = re.compile(r"^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$")

# A MAC anywhere inside a name, either separator. mDNS instance names routinely
# carry one in brackets: "PI5-03 - 3 [88:a2:9e:01:f9:2b]".
MAC_ANYWHERE_RE = re.compile(r"(?:[0-9a-fA-F]{2}[:\-]){5}[0-9a-fA-F]{2}")

# A long unbroken hex run is a serial number or a device id, not a name:
# XB5839F36FA6C, 8EFFE82DA7B72D4F. Ten is the threshold because real names do
# carry short hex tails a person can still read - GE_Light_687B, Ring-a7ed55 -
# and those stay.
HEX_RUN_RE = re.compile(r"[0-9a-fA-F]{10,}")

# avahi-browse -p escapes with a backslash and three decimal digits, so a space
# arrives as \032, a colon as \058 and a bracket as \091. Undo that before
# anything else touches the string, or "Xerox\040R\041\032C230" is cleaned into
# "Xerox-040R-041-032C230" - which is what the first run of this collector did.
AVAHI_ESCAPE_RE = re.compile(r"\\(\d{3})")


def log(msg):
    print(f"[{datetime.now():%H:%M:%S}] {msg}", flush=True)


def run(cmd, timeout=1800):
    """Run a command, returning stdout. Never raises on a non-zero exit.

    nmap exits non-zero when a host script fails on one target out of fifty,
    and that must not lose the other forty-nine results.
    """
    try:
        # errors="replace": a device on this link publishes invalid UTF-8 in its
        # mDNS records, which killed the whole run on a decode error. One
        # mangled character in one name must not lose the other seventy hosts.
        p = subprocess.run(cmd, capture_output=True, text=True,
                           errors="replace", timeout=timeout)
        if p.returncode != 0 and not p.stdout:
            log(f"  ! {cmd[0]} exit {p.returncode}: {p.stderr.strip()[:200]}")
        return p.stdout
    except subprocess.TimeoutExpired:
        log(f"  ! {cmd[0]} timed out after {timeout}s")
        return ""
    except FileNotFoundError:
        log(f"  ! {cmd[0]} not installed")
        return ""


def clean_name(raw):
    """Reduce a discovered name to a single usable label, or None.

    Discovery protocols return names in shapes DNS cannot hold:
    "Living Room Echo._airplay._tcp.local" from mDNS, "Joshua's iPhone" with an
    apostrophe and spaces, "HS200 (Kitchen)" from a UPnP friendlyName. All of
    them become a DNS label here or they are dropped - because the whole point
    of collecting them is that they can be pushed to DNS later.
    """
    if not raw:
        return None

    # Undo avahi's escaping first. Everything below assumes real characters.
    n = AVAHI_ESCAPE_RE.sub(lambda m: chr(int(m.group(1))), raw.strip())

    # mDNS service instance: keep the instance, drop the service and .local.
    n = re.split(r"\._(?:tcp|udp)\b", n)[0]
    if n.lower().endswith(".local"):
        n = n[:-6]
    # A fully qualified name reduces to its first label.
    n = n.split(".")[0]

    # Drop an embedded MAC before the punctuation pass, or its separators become
    # hyphens and it survives as part of the name.
    n = MAC_ANYWHERE_RE.sub(" ", n)

    # Spaces and punctuation to hyphens, then collapse.
    n = re.sub(r"[^0-9A-Za-z_-]+", "-", n).strip("-")
    n = re.sub(r"-{2,}", "-", n)

    if not n or len(n) > 63:
        return None

    low = n.lower()
    base = AVAHI_DEDUPE_SUFFIX_RE.sub("", low)
    if low in JUNK or base in JUNK:
        return None
    if IPV4_NAME_RE.match(low):
        return None
    if low.startswith(JUNK_PREFIX) or base.startswith(JUNK_PREFIX):
        return None
    # A MAC is not a name, in any spelling.
    if MAC_RE.match(low.replace("-", ":")):
        return None
    # A serial number or device id rather than a name.
    if HEX_RUN_RE.search(n):
        return None
    # "android-a1b2c3d4" - a platform plus a random tail.
    if re.match(r"^android-[0-9a-f]{4,}$", low):
        return None
    return n


def norm_mac(m):
    if not m:
        return None
    m = m.strip().lower().replace("-", ":")
    return m if MAC_RE.match(m) else None


# ---------------------------------------------------------------------------
# The sweep. ARP for the MAC, reverse DNS for whatever the resolver thinks.
# ---------------------------------------------------------------------------

def sweep(subnets):
    """Ping/ARP sweep. Returns {ip: {mac, vendor, rdns}} for hosts that answered.

    -R asks for reverse DNS on every host even though it is the least reliable
    name here; it is collected as its own low-ranked source precisely so the two
    can be compared.
    """
    hosts = {}
    for net in subnets:
        log(f"sweep {net}")
        # Explicit probes: nmap's default off-link set includes ICMP timestamp,
        # which the Omada gateway's IPS reports as an attack from this host.
        xml = run(["nmap", "-sn", "-PE", "-PS443", "-PA80", "-R", "-T4",
                   "--host-timeout", "30s", "-oX", "-", net])
        if not xml.strip():
            continue
        try:
            root = ET.fromstring(xml)
        except ET.ParseError as e:
            log(f"  ! unparseable nmap XML for {net}: {e}")
            continue
        for h in root.iter("host"):
            st = h.find("status")
            if st is None or st.get("state") != "up":
                continue
            ip = mac = vendor = rdns = None
            for a in h.iter("address"):
                if a.get("addrtype") == "ipv4":
                    ip = a.get("addr")
                elif a.get("addrtype") == "mac":
                    mac = norm_mac(a.get("addr"))
                    vendor = a.get("vendor")
            hn = h.find("hostnames/hostname")
            if hn is not None:
                rdns = hn.get("name")
            if ip:
                hosts[ip] = {"mac": mac, "vendor": vendor, "rdns": rdns}
        log(f"  {len(hosts)} hosts up so far")
    return hosts


def probe_mdns():
    """avahi-browse: every service instance on the link.

    Preferred over nmap's dns-service-discovery because avahi is a resident
    mDNS stack - it already holds the cache and sees announcements a one-shot
    probe misses.

    Wrapped in GNU timeout rather than trusting subprocess's. avahi-browse -t
    is supposed to stop when the cache is exhausted, and usually returns in
    about five seconds - but on a busy moment it hangs, and one run hit 120s.
    subprocess kills it and DISCARDS its output, so all 17 names were lost.
    Under timeout(1) it is signalled instead and its partial output still
    arrives on stdout, which is worth far more than nothing.

    Returns ({ip: name}, {ip: device_type}).
    """
    log("mDNS (avahi-browse)")
    out = run(["timeout", "-k", "2", "45", "avahi-browse", "-a", "-r", "-t", "-p"],
              timeout=60)
    found = {}
    types = {}
    for line in out.splitlines():
        # =;iface;proto;instance;service;domain;host;ip;port;txt...
        if not line.startswith("="):
            continue
        f = line.split(";")
        if len(f) < 8 or f[2] != "IPv4":
            continue
        ip = f[7].strip()
        if not ip:
            continue

        # The TXT record is where the good name usually is. A phone publishes
        # its instance as a 32-character hash and its real name as
        # "name=Galaxy S25 Ultra" in TXT; Apple and HomeKit devices put the
        # model in "md=". Without reading TXT, that device is unnameable.
        txt = ";".join(f[9:]) if len(f) > 9 else ""
        tx = dict(re.findall(r'"([^"=]+)=([^"]*)"', txt))

        name = (clean_name(tx.get("name"))
                or clean_name(tx.get("fn"))
                or clean_name(f[3])
                or clean_name(f[6]))
        model = tx.get("md") or tx.get("model") or tx.get("type")

        if name and ip not in found:
            found[ip] = name
        if model and ip not in types:
            types[ip] = model[:60]

    log(f"  {len(found)} named by mDNS")
    return found, types


def probe_netbios(ips):
    """nmap nbstat: the NetBIOS name held by the node itself."""
    if not ips:
        return {}
    log(f"NetBIOS on {len(ips)} hosts")
    xml = run(["nmap", "-sU", "-p", "137", "--script", "nbstat",
               "-T4", "--host-timeout", "30s", "-oX", "-", *ips])
    found = {}
    if not xml.strip():
        return found
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return found
    for h in root.iter("host"):
        ip = next((a.get("addr") for a in h.iter("address")
                   if a.get("addrtype") == "ipv4"), None)
        if not ip:
            continue
        for s in h.iter("script"):
            if s.get("id") != "nbstat":
                continue
            m = re.search(r"NetBIOS name:\s*([^,<\n]+)", s.get("output") or "")
            if m:
                name = clean_name(m.group(1))
                if name:
                    found[ip] = name
    log(f"  {len(found)} named by NetBIOS")
    return found


def probe_snmp(ips):
    """nmap snmp-sysdescr: sysName, which printers and switches answer."""
    if not ips:
        return {}
    log(f"SNMP on {len(ips)} hosts")
    xml = run(["nmap", "-sU", "-p", "161", "--script", "snmp-sysdescr,snmp-info",
               "-T4", "--host-timeout", "30s", "-oX", "-", *ips])
    found = {}
    if not xml.strip():
        return found
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return found
    for h in root.iter("host"):
        ip = next((a.get("addr") for a in h.iter("address")
                   if a.get("addrtype") == "ipv4"), None)
        if not ip:
            continue
        for s in h.iter("script"):
            out = s.get("output") or ""
            m = re.search(r"(?:sysName|System name):\s*([^\n<]+)", out)
            if m:
                name = clean_name(m.group(1))
                if name:
                    found[ip] = name
    log(f"  {len(found)} named by SNMP")
    return found


def kasa_encrypt(payload):
    """TP-Link Kasa local protocol: XOR autokey, seeded with 171.

    Not encryption in any meaningful sense - there is no secret - just
    obfuscation, and it is the whole of the local protocol. Over TCP the
    ciphertext is prefixed with its length as four big-endian bytes.
    """
    key = 171
    out = bytearray()
    for b in payload.encode():
        key ^= b
        out.append(key)
    return len(out).to_bytes(4, "big") + bytes(out)


def kasa_decrypt(data):
    key = 171
    out = bytearray()
    for b in data:
        out.append(key ^ b)
        key = b
    return bytes(out).decode("utf-8", errors="replace")


def probe_kasa(ips, timeout=2):
    """Ask TP-Link Kasa devices what they are, on TCP 9999.

    The best source on this network, and the reason it is worth a vendor
    specific probe: get_sysinfo returns "alias", which is the name typed into
    the Kasa app by the person who installed the device. That is a stated name,
    not a self-description - so it ranks with a name typed into the Omada
    controller rather than with a DHCP announcement.

    It also returns "model" (HS200(US)) and "dev_name" ("Smart Wi-Fi Light
    Switch"), which become device_type, and the device's own MAC - better
    evidence than joining on an ARP entry.

    Returns {ip: (name, mac_or_none, device_type_or_none)}.
    """
    import json
    import socket

    if not ips:
        return {}
    log(f"Kasa (TP-Link) on {len(ips)} hosts")
    req = kasa_encrypt('{"system":{"get_sysinfo":{}}}')
    found = {}
    for ip in ips:
        try:
            with socket.create_connection((ip, 9999), timeout=timeout) as s:
                s.sendall(req)
                buf = b""
                # The length prefix says how much to expect; without honouring it
                # a large sysinfo arrives truncated and the JSON will not parse.
                while len(buf) < 4:
                    chunk = s.recv(4096)
                    if not chunk:
                        break
                    buf += chunk
                if len(buf) < 4:
                    continue
                want = int.from_bytes(buf[:4], "big")
                while len(buf) - 4 < want:
                    chunk = s.recv(4096)
                    if not chunk:
                        break
                    buf += chunk
            info = json.loads(kasa_decrypt(buf[4:]))["system"]["get_sysinfo"]
        except (OSError, ValueError, KeyError, TypeError):
            # Almost every host will refuse the connection. That is not news.
            continue

        name = clean_name(info.get("alias"))
        mac = norm_mac(info.get("mac") or info.get("mic_mac"))
        model = info.get("model") or ""
        dev = info.get("dev_name") or ""
        dtype = (f"{model} {dev}".strip() or None)
        if dtype:
            dtype = dtype[:60]
        if name:
            found[ip] = (name, mac, dtype)
            log(f"  {ip} {name} ({model})")
    log(f"  {len(found)} named by Kasa")
    return found


def probe_fingerprint(ips):
    """Light service/OS fingerprint, for device_type only - never for a name.

    This is the 'thorough' half: it is what distinguishes a mystery MAC that is
    a printer from one that is a thermostat, when neither will say its name.
    """
    if not ips:
        return {}
    log(f"fingerprint {len(ips)} hosts (this is the slow one)")
    xml = run(["nmap", "-sS", "-sV", "--version-light", "-O", "--osscan-limit",
               "--top-ports", "50", "-T4", "--host-timeout", "90s",
               "-oX", "-", *ips], timeout=3600)
    found = {}
    if not xml.strip():
        return found
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return found
    for h in root.iter("host"):
        ip = next((a.get("addr") for a in h.iter("address")
                   if a.get("addrtype") == "ipv4"), None)
        if not ip:
            continue
        osm = h.find("os/osmatch")
        if osm is not None and osm.get("name"):
            found[ip] = osm.get("name")[:60]
    log(f"  {len(found)} fingerprinted")
    return found


def probe_ssdp(timeout=6):
    """SSDP M-SEARCH, then read each device description for its friendlyName.

    Done directly rather than through nmap's upnp-info because upnp-info reports
    the SERVER header - "Linux/3.4 UPnP/1.0" - which names an SDK, not a device.
    The friendlyName in the description XML is the string the owner sees in an
    app, and for a TV or a smart plug it is the best name in existence.

    Returns {ip: name}.
    """
    import socket
    import urllib.request
    from urllib.parse import urlparse

    log("SSDP M-SEARCH")
    msg = ("M-SEARCH * HTTP/1.1\r\n"
           "HOST: 239.255.255.250:1900\r\n"
           "MAN: \"ssdp:discover\"\r\n"
           "MX: 3\r\n"
           "ST: ssdp:all\r\n\r\n").encode()

    locations = {}
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 2)
        s.settimeout(timeout)
        s.sendto(msg, ("239.255.255.250", 1900))
        while True:
            try:
                data, addr = s.recvfrom(65507)
            except socket.timeout:
                break
            m = re.search(rb"(?im)^LOCATION:\s*(\S+)", data)
            if m:
                locations.setdefault(addr[0], m.group(1).decode(errors="ignore"))
        s.close()
    except OSError as e:
        log(f"  ! SSDP socket: {e}")
        return {}

    found = {}
    for ip, url in locations.items():
        # Only fetch from the host that answered - a LOCATION pointing elsewhere
        # is either broken or hostile, and either way is not this device's name.
        try:
            if urlparse(url).hostname != ip:
                continue
            with urllib.request.urlopen(url, timeout=5) as r:
                body = r.read(65536).decode("utf-8", errors="ignore")
        except Exception as e:
            log(f"  ! {ip} description: {type(e).__name__}")
            continue
        m = re.search(r"<friendlyName>\s*([^<]+)</friendlyName>", body)
        if m:
            name = clean_name(m.group(1))
            if name:
                found[ip] = name
    log(f"  {len(locations)} answered, {len(found)} named by SSDP")
    return found


# ---------------------------------------------------------------------------
# Persistence
# ---------------------------------------------------------------------------

def write_observations(cfg, by_source, vendors, types, dry_run):
    """Replace this run's sources wholesale, then upsert vendors.

    Each source is deleted and reinserted in one transaction so a reader never
    sees a source half-present. A source that found nothing is still cleared -
    that is the point of replacing rather than merging.
    """
    total = sum(len(v) for v in by_source.values())
    log(f"{total} observations across {len(by_source)} sources; "
        f"{len(vendors)} vendors")
    if dry_run:
        for src, rows in sorted(by_source.items()):
            for mac, (name, ip) in sorted(rows.items()):
                log(f"  [dry] {src:10} {mac}  {ip or '-':15} {name}")
        return 0

    m = cfg["mssql"]
    conn = pymssql.connect(server=m["server"], port=m.get("port", "1433"),
                           user=m["user"], password=m["password"],
                           database=m["database"], autocommit=False,
                           timeout=120, login_timeout=30)
    written = 0
    try:
        cur = conn.cursor()
        for src, rows in by_source.items():
            cur.execute("DELETE FROM dbo.DeviceNameObservation WHERE source = %s", (src,))
            for mac, (name, ip) in rows.items():
                cur.execute(
                    "INSERT INTO dbo.DeviceNameObservation "
                    "(mac, source, name, ip, device_type, observed_utc) "
                    "VALUES (%s, %s, %s, %s, %s, %s)",
                    (mac, src, name, ip, types.get(mac), datetime.now(timezone.utc)))
                written += 1

        for mac, vendor in vendors.items():
            cur.execute(
                "UPDATE dbo.DeviceVendor SET vendor = %s, observed_utc = %s WHERE mac = %s",
                (vendor, datetime.now(timezone.utc), mac))
            if cur.rowcount == 0:
                cur.execute(
                    "INSERT INTO dbo.DeviceVendor (mac, vendor, observed_utc) "
                    "VALUES (%s, %s, %s)",
                    (mac, vendor, datetime.now(timezone.utc)))
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()
    return written


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--subnets", default=None,
                    help="comma-separated CIDRs; default from config, "
                         "else 10.20.0.0/24,10.20.1.0/24")
    ap.add_argument("--no-fingerprint", action="store_true",
                    help="skip the slow -sV/-O pass")
    ap.add_argument("--dry-run", action="store_true",
                    help="discover and print, write nothing")
    args = ap.parse_args()

    cfg = configparser.ConfigParser()
    if not cfg.read(CONFIG):
        sys.exit(f"[fatal] cannot read config {CONFIG}")

    raw = args.subnets or cfg.get("discovery", "subnets",
                                  fallback="10.20.0.0/24,10.20.1.0/24")
    subnets = []
    for s in raw.split(","):
        s = s.strip()
        if not s:
            continue
        try:
            subnets.append(str(ipaddress.ip_network(s, strict=False)))
        except ValueError as e:
            sys.exit(f"[fatal] bad subnet {s!r}: {e}")

    hosts = sweep(subnets)
    if not hosts:
        sys.exit("[fatal] sweep found nothing - is this host on the LAN?")

    # Only an on-link host has a MAC, and without a MAC an observation cannot be
    # keyed. The Pi's own address and anything routed are skipped for that reason.
    ip_to_mac = {ip: h["mac"] for ip, h in hosts.items() if h["mac"]}
    log(f"{len(hosts)} hosts up, {len(ip_to_mac)} with a MAC on this link")

    live = sorted(hosts, key=lambda i: ipaddress.ip_address(i))

    mdns_names, mdns_types = probe_mdns()
    named = {
        "mdns": mdns_names,
        "ssdp": probe_ssdp(),
        "netbios": probe_netbios(live),
        "snmp": probe_snmp(live),
        "nmap": {ip: n for ip, h in hosts.items()
                 if (n := clean_name(h.get("rdns")))},
    }

    types = {}

    # An mDNS "md=" is the device stating its own model. Taken before the Kasa
    # and nmap passes so a more specific answer can still override it.
    for ip, t in mdns_types.items():
        if mac := ip_to_mac.get(ip):
            types[mac] = t

    # Kasa is handled apart from the others because it answers with more than a
    # name: the device's own MAC, which is better evidence than an ARP join, and
    # its model, which is the only device_type any source here supplies.
    kasa_rows = {}
    for ip, (name, own_mac, dtype) in probe_kasa(live).items():
        mac = own_mac or ip_to_mac.get(ip)
        if not mac:
            continue
        kasa_rows[mac] = (name, ip)
        if dtype:
            types[mac] = dtype

    if not args.no_fingerprint:
        for ip, t in probe_fingerprint(live).items():
            # A Kasa model string is a fact; an nmap OS guess is a guess. Never
            # overwrite the former with the latter.
            if (mac := ip_to_mac.get(ip)) and mac not in types:
                types[mac] = t

    # Key everything on MAC. An IP with no MAC on this link is dropped rather
    # than guessed at.
    by_source = {}
    for src, found in named.items():
        rows = {}
        for ip, name in found.items():
            mac = ip_to_mac.get(ip)
            if mac:
                rows[mac] = (name, ip)
        # Reverse DNS on this network hands one name to many devices: a single
        # sweep saw ALIEN01 on 14 MACs and OMEN01 on 4. A name that cannot pick
        # out one device is not evidence about any of them, so it is dropped
        # rather than asserted 14 times.
        #
        # mDNS does it too, more quietly: two Amazon devices both published
        # "amzn", which would have named both of them the same thing.
        #
        # NetBIOS is exempt. It legitimately repeats - WINSERVER01 answers on
        # both its NICs - and that is one machine with two addresses, not a
        # collision between two devices.
        if src != "netbios":
            seen = {}
            for mac, (name, ip) in rows.items():
                seen.setdefault(name, []).append(mac)
            shared = {n: m for n, m in seen.items() if len(m) > 1}
            for name, macs_sharing in sorted(shared.items()):
                log(f"  dropped {name!r}: {src} claims it for "
                    f"{len(macs_sharing)} MACs")
                for mac in macs_sharing:
                    rows.pop(mac, None)

        by_source[src] = rows
        log(f"{src:10} {len(rows)} usable of {len(found)} found")

    by_source["kasa"] = kasa_rows
    log(f"{'kasa':10} {len(kasa_rows)} usable")

    vendors = {}
    for ip, h in hosts.items():
        if h["mac"] and h.get("vendor"):
            vendors[h["mac"]] = h["vendor"][:200]

    written = write_observations(cfg, by_source, vendors, types, args.dry_run)
    log(f"done: {written} observations written")


if __name__ == "__main__":
    main()
