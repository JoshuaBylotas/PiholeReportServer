#!/usr/bin/env bash
#
# Sets up Ollama as a GPU-backed inference host for the Pi-hole Report Server.
#
# Run this ON the machine with the NVIDIA GPU, as a user with sudo.
#
# Sized for a 16 GB card: qwen2.5:14b-instruct at Q4_K_M is about 9 GB, leaving
# comfortable room for a large context. A 7B would run far faster, but the 14B is
# the point of having the card - the 7B currently in use ignored an explicit
# prompt rule and produced a JOIN that inflates per-device counts, which a 14B is
# much less prone to.
#
# Usage:
#   chmod +x install-ollama-gpu.sh
#   ./install-ollama-gpu.sh
#   MODEL='qwen2.5-coder:14b' ./install-ollama-gpu.sh
#   ALLOW_FROM='' ./install-ollama-gpu.sh        # skip firewall rules
#
# Model choices for a 16 GB card:
#   qwen2.5:14b-instruct   ~9 GB   recommended: good reasoning AND decent SQL
#   qwen2.5-coder:14b      ~9 GB   better SQL, weaker at prose conclusions
#   qwen2.5:32b-instruct  ~20 GB   DOES NOT FIT - spills to CPU and crawls
# Only one is loaded at a time; two 9 GB models exceed 16 GB.

set -euo pipefail

MODEL="${MODEL:-qwen2.5:14b-instruct}"
# The report server's two addresses. Space-separated; empty to skip firewalling.
ALLOW_FROM="${ALLOW_FROM-10.20.0.15 10.20.0.16}"

step() { printf '\n=== %s ===\n' "$1"; }
ok()   { printf '  OK   %s\n' "$1"; }
warn() { printf '  WARN %s\n' "$1"; }

# ── 0. Preconditions ────────────────────────────────────────────────────────
step 'Checking prerequisites'

if ! sudo -n true 2>/dev/null; then
    echo "  (sudo will prompt for a password)"
fi

# The whole point of this host is the GPU, so fail loudly rather than silently
# falling back to CPU and being slower than what we already have.
if ! command -v nvidia-smi >/dev/null 2>&1; then
    echo "ERROR: nvidia-smi not found. Install the NVIDIA driver first." >&2
    echo "       Ubuntu/Debian:  sudo ubuntu-drivers autoinstall   (then reboot)" >&2
    echo "       The CUDA toolkit is NOT required - Ollama bundles what it needs." >&2
    exit 1
fi
ok "GPU: $(nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader)"

VRAM_MB="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits | head -1 | tr -d ' ')"
if [ "$VRAM_MB" -lt 15000 ]; then
    warn "Only ${VRAM_MB} MiB of VRAM. A 14B Q4 needs ~9 GB plus context; consider MODEL='qwen2.5:7b-instruct'."
fi

# ── 1. Install ──────────────────────────────────────────────────────────────
step 'Installing Ollama'

# Fetch the official installer to disk first rather than piping straight into a
# root shell, so it can be inspected before it runs.
curl -fsSL https://ollama.com/install.sh -o /tmp/ollama-install.sh
ok "installer fetched ($(wc -l < /tmp/ollama-install.sh) lines, sha256 $(sha256sum /tmp/ollama-install.sh | cut -c1-16)...)"
echo "  review it if you like:  less /tmp/ollama-install.sh"
sudo sh /tmp/ollama-install.sh
rm -f /tmp/ollama-install.sh
ok "ollama $(ollama --version 2>&1 | head -1)"

# ── 2. Configuration ────────────────────────────────────────────────────────
step 'Configuring'

sudo mkdir -p /etc/systemd/system/ollama.service.d
sudo tee /etc/systemd/system/ollama.service.d/override.conf >/dev/null <<'UNIT'
[Service]
# Must listen off-loopback for the report server to reach it.
Environment="OLLAMA_HOST=0.0.0.0:11434"
# A cold load costs seconds even on GPU; keep the model resident.
Environment="OLLAMA_KEEP_ALIVE=60m"
# Two 9 GB models will not fit in 16 GB.
Environment="OLLAMA_MAX_LOADED_MODELS=1"
# 2 slots so a long batch job cannot block an interactive question. This was a
# real problem at 1: a classification backfill blocked a question for 148s.
Environment="OLLAMA_NUM_PARALLEL=2"
# Meaningful speed-up on Ampere and later, and it reduces KV cache size.
Environment="OLLAMA_FLASH_ATTENTION=1"
# Quantised KV cache: roughly halves context memory for no practical quality
# loss, which is what lets a 14B keep a big context on a 16 GB card.
Environment="OLLAMA_KV_CACHE_TYPE=q8_0"
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now ollama
ok 'service configured and enabled'

printf '  waiting for the API'
for _ in $(seq 1 30); do
    if curl -sf -m 3 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
        printf '\n'; ok 'API responding on 11434'; break
    fi
    printf '.'; sleep 2
done
if ! curl -sf -m 3 http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
    printf '\n'
    echo "ERROR: Ollama did not start. Check: sudo journalctl -u ollama -n 50" >&2
    exit 1
fi

# ── 3. Firewall ────────────────────────────────────────────────────────────
step 'Firewall'

if [ -z "${ALLOW_FROM// /}" ]; then
    warn 'skipped by request - the port is open to anything that can route here'
elif command -v ufw >/dev/null 2>&1 && sudo ufw status | grep -q '^Status: active'; then
    for ip in $ALLOW_FROM; do
        sudo ufw allow from "$ip" to any port 11434 proto tcp comment 'Ollama report server' >/dev/null
    done
    sudo ufw deny 11434/tcp comment 'Ollama: deny all others' >/dev/null
    ok "ufw: allowed from $ALLOW_FROM, denied otherwise"
elif command -v nft >/dev/null 2>&1 || [ -x /usr/sbin/nft ]; then
    NFT="$(command -v nft || echo /usr/sbin/nft)"
    # A separate table with policy accept, so only port 11434 is filtered and any
    # existing rules (Docker, for instance) are left completely alone.
    IPS="$(echo "$ALLOW_FROM" | tr ' ' ',')"
    sudo mkdir -p /etc/nftables.d
    sudo tee /etc/nftables.d/ollama.nft >/dev/null <<NFTEOF
#!/usr/sbin/nft -f
# Ollama has no authentication, so only the report server may reach it.
destroy table inet ollama_guard
table inet ollama_guard {
    chain input {
        type filter hook input priority 0; policy accept;
        iif lo accept
        tcp dport 11434 ip saddr { $IPS } accept
        tcp dport 11434 drop
    }
}
NFTEOF
    sudo tee /etc/systemd/system/ollama-firewall.service >/dev/null <<'UNIT'
[Unit]
Description=Restrict Ollama (11434) to the report server
After=network-online.target
Wants=network-online.target
Before=ollama.service
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/sbin/nft -f /etc/nftables.d/ollama.nft
[Install]
WantedBy=multi-user.target
UNIT
    sudo systemctl daemon-reload
    sudo systemctl enable --now ollama-firewall.service
    ok "nftables: allowed from $ALLOW_FROM, dropped otherwise"
else
    warn 'no ufw or nft found - port 11434 is unrestricted. Ollama has no authentication.'
fi

# ── 4. Model ───────────────────────────────────────────────────────────────
step "Pulling $MODEL"
ollama pull "$MODEL"
ok 'model pulled'

# ── 5. Verify it is actually on the GPU ────────────────────────────────────
step 'Verifying GPU offload'

# "100% GPU" is the thing to confirm. Any CPU share means part of the model
# spilled to system RAM and throughput will be a fraction of what it should be.
curl -sf -m 600 http://127.0.0.1:11434/api/generate \
  -d "{\"model\":\"$MODEL\",\"prompt\":\"Reply with the single word: ready\",\"stream\":false,\"options\":{\"num_predict\":8}}" \
  >/dev/null
echo '  processor split:'
ollama ps

step 'Benchmark'
curl -sf -m 600 http://127.0.0.1:11434/api/generate \
  -d "{\"model\":\"$MODEL\",\"prompt\":\"Write one paragraph explaining what a DNS resolver does.\",\"stream\":false,\"options\":{\"num_predict\":200,\"temperature\":0.1}}" \
  -o /tmp/ollama-bench.json

python3 - <<'PY'
import json
d = json.load(open('/tmp/ollama-bench.json'))
out = d.get('eval_count', 0); od = d.get('eval_duration', 1) / 1e9
inn = d.get('prompt_eval_count', 0); idur = d.get('prompt_eval_duration', 1) / 1e9
print(f"  output: {out} tokens at {out/max(od,0.001):.1f} tok/s")
print(f"  prompt: {inn} tokens at {inn/max(idur,0.001):.1f} tok/s")
PY
rm -f /tmp/ollama-bench.json

step 'Done'
cat <<EOF
  Report back:  the tok/s figures above, and this machine's IP
                ($(hostname -I 2>/dev/null | awk '{print $1}')).

  For reference, the current CPU host manages 9.8 tok/s on a 7B. Anything above
  about 30 tok/s on this 14B makes the analysis agent genuinely usable - a
  four-step investigation drops from roughly ten minutes to under one.

  I then repoint the report server with two settings, no redeploy:
      Ai__Endpoint = http://<this machine's IP>:11434
      Ai__Model    = $MODEL
EOF
