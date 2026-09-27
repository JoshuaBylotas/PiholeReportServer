"""On-demand, read-only Omada Controller lookup for a single MAC.

Companion to sync.py: when a query's client_mac has no good name yet, this asks
the controller directly for that one client rather than waiting on
Collect-OmadaNames.ps1's schedule (currently broken - see docs/09, its SQL
Server leg fails with an untrusted-domain error, unrelated to the Omada call
itself) or discover_network.py's 30-minute sweep.

Uses GET /openapi/v1/{omadacId}/sites/{siteId}/clients/{mac} - a single-client
read, not the paged client list Collect-OmadaNames.ps1 walks, because this is
one MAC on demand rather than a full poll.

Credentials: /etc/pihole-sqlsync/omada.json, a VIEW-scoped OpenAPI app
(Settings -> Platform Integration -> OpenAPI -> Add New App -> Client Mode).
Same shape Collect-OmadaNames.ps1 uses on WINAD02, but its own registration so
this host holds a separate, independently-revocable read-only credential.

Missing file = feature disabled, not an error: sync.py must keep ingesting
whether or not this has been set up yet, and a lookup failure must never be
able to take the ingest loop down.
"""
import json
import re
import ssl
import time
import urllib.error
import urllib.request

CREDENTIAL_PATH = "/etc/pihole-sqlsync/omada.json"

# The controller ships a self-signed certificate on the LAN; nothing to
# validate it against. Matches the approach Collect-OmadaNames.ps1 already
# uses (a compiled TLS callback there, for the same reason).
_INSECURE_CTX = ssl._create_unverified_context()


def _post(url, payload, timeout=10):
    req = urllib.request.Request(
        url, data=json.dumps(payload).encode(), method="POST",
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout, context=_INSECURE_CTX) as r:
        return json.loads(r.read())


def _get(url, token, timeout=10):
    req = urllib.request.Request(url, headers={"Authorization": f"AccessToken={token}"})
    with urllib.request.urlopen(req, timeout=timeout, context=_INSECURE_CTX) as r:
        return json.loads(r.read())


class OmadaLookup:
    """One instance per process. Token and site list are cached; nothing is
    fetched until the first unresolved MAC asks for it."""

    def __init__(self, controller="https://10.20.0.3:8043", credential_path=CREDENTIAL_PATH):
        self.controller = controller
        self.credential_path = credential_path
        self._cred = None
        self._token = None
        self._token_expiry = 0.0
        self._sites = None
        self._disabled_reason = None

    @property
    def available(self):
        if self._disabled_reason:
            return False
        if self._cred is None:
            try:
                with open(self.credential_path) as f:
                    cred = json.load(f)
                for k in ("omadacId", "clientId", "clientSecret"):
                    if not cred.get(k):
                        raise ValueError(f"missing '{k}'")
                self._cred = cred
            except FileNotFoundError:
                self._disabled_reason = "no credential file yet"
                return False
            except Exception as e:
                self._disabled_reason = f"bad credential file: {e}"
                print(f"[omada] disabled: {self._disabled_reason}", flush=True)
                return False
        return True

    def _ensure_token(self):
        if self._token and time.time() < self._token_expiry:
            return
        tok = _post(
            f"{self.controller}/openapi/authorize/token?grant_type=client_credentials",
            {"omadacId": self._cred["omadacId"], "client_id": self._cred["clientId"],
             "client_secret": self._cred["clientSecret"]})
        if tok.get("errorCode") != 0:
            raise RuntimeError(f"token request failed: {tok}")
        self._token = tok["result"]["accessToken"]
        # Refresh a little early rather than racing expiry mid-call.
        self._token_expiry = time.time() + max(60, tok["result"].get("expiresIn", 300) - 30)

    def _ensure_sites(self):
        if self._sites is not None:
            return
        self._ensure_token()
        resp = _get(
            f"{self.controller}/openapi/v1/{self._cred['omadacId']}/sites?pageSize=100&page=1",
            self._token)
        if resp.get("errorCode") != 0:
            raise RuntimeError(f"site list failed: {resp}")
        self._sites = [s["siteId"] for s in resp["result"]["data"]]

    def resolve(self, mac):
        """Best (name, source) for one MAC from the controller, or None.

        mac is colon-separated lowercase, e.g. 'aa:bb:cc:dd:ee:ff' - the same
        shape dbo.DeviceNameObservation.mac expects. Never raises: a lookup
        failure is logged and treated as "no answer this time", not fatal.
        """
        if not self.available:
            return None
        try:
            self._ensure_sites()
        except Exception as e:
            print(f"[omada] auth/sites failed: {type(e).__name__}: {e}", flush=True)
            return None

        controller_mac = mac.replace(":", "-").upper()
        for site_id in self._sites:
            url = (f"{self.controller}/openapi/v1/{self._cred['omadacId']}"
                   f"/sites/{site_id}/clients/{controller_mac}")
            try:
                resp = _get(url, self._token)
            except urllib.error.HTTPError as e:
                if e.code == 404:
                    continue  # not on this site
                print(f"[omada] lookup HTTP error for {mac}: {e}", flush=True)
                continue
            except Exception as e:
                print(f"[omada] lookup failed for {mac}: {type(e).__name__}: {e}", flush=True)
                continue

            if resp.get("errorCode") != 0:
                continue
            c = resp.get("result") or {}

            # Same precedence Collect-OmadaNames.ps1 uses: a name a person
            # typed outranks the hostname the device announced at DHCP, and
            # the controller falls back to the announced one when nothing
            # was typed - so a name equal to hostName is not evidence anyone
            # chose it.
            typed = (c.get("name") or "").strip()
            announced = (c.get("hostName") or "").strip()
            ip = (c.get("ip") or "").strip()
            if typed == announced:
                typed = ""
            # With nothing typed, the controller reports the MAC itself as the
            # name. True and useless - the exact junk docs/09 cleaned out of DNS.
            if _usable(typed, ip):
                return (_strip_domain(typed), "omada")
            if _usable(announced, ip):
                return (_strip_domain(announced), "omadadhcp")
        return None


_MAC_SHAPED = re.compile(r"^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$|^[0-9a-fA-F]{12}$")


def _usable(name, ip):
    return bool(name) and not _MAC_SHAPED.match(name) and name != ip


def _strip_domain(name):
    return re.sub(r"\.bylotas\.(net|com)$", "", name, flags=re.IGNORECASE)
