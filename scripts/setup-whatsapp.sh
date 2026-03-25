#!/usr/bin/env bash
# OpenClaw WhatsApp Worker Setup
# Detects runtimes, builds engines, validates setup.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

ok()   { echo -e "${GREEN}[OK]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
fail() { echo -e "${RED}[FAIL]${NC} $*"; }

# ── Runtime Detection ───────────────────────────────────────

check_node() {
    if command -v node &>/dev/null; then
        local ver
        ver=$(node --version 2>/dev/null | sed 's/^v//')
        local major
        major=$(echo "$ver" | cut -d. -f1)
        if [ "$major" -ge 18 ] 2>/dev/null; then
            ok "Node.js v$ver found"
            return 0
        else
            warn "Node.js v$ver found but 18+ is required"
            return 1
        fi
    else
        warn "Node.js not found"
        return 1
    fi
}

check_go() {
    if command -v go &>/dev/null; then
        local ver
        ver=$(go version 2>/dev/null | grep -oE '[0-9]+\.[0-9]+' | head -1)
        ok "Go $ver found"
        return 0
    else
        warn "Go not found"
        return 1
    fi
}

# ── Baileys Setup ───────────────────────────────────────────

setup_baileys() {
    local dir="$ROOT_DIR/src/whatsapp-baileys-worker"
    if [ ! -f "$dir/package.json" ]; then
        fail "Baileys worker directory not found at $dir"
        return 1
    fi

    echo "Installing Baileys worker dependencies..."
    cd "$dir"
    npm install --production
    ok "Baileys worker dependencies installed"
}

# ── whatsmeow Setup ────────────────────────────────────────

setup_whatsmeow() {
    local dir="$ROOT_DIR/src/whatsapp-whatsmeow-worker"
    if [ ! -f "$dir/go.mod" ]; then
        fail "whatsmeow worker directory not found at $dir"
        return 1
    fi

    echo "Building whatsmeow worker..."
    cd "$dir"
    go build -o whatsapp-whatsmeow-worker .
    ok "whatsmeow worker built successfully"
}

# ── Main ────────────────────────────────────────────────────

echo "=== OpenClaw WhatsApp Worker Setup ==="
echo ""

HAS_NODE=false
HAS_GO=false

if check_node; then HAS_NODE=true; fi
if check_go; then HAS_GO=true; fi
echo ""

if [ "$HAS_NODE" = false ] && [ "$HAS_GO" = false ]; then
    fail "Neither Node.js 18+ nor Go was found."
    echo "Install one of:"
    echo "  - Node.js 18+: https://nodejs.org/"
    echo "  - Go 1.21+:    https://go.dev/dl/"
    exit 1
fi

# Setup available engines
SETUP_BAILEYS=false
SETUP_WHATSMEOW=false

if [ "$HAS_NODE" = true ]; then
    read -rp "Set up Baileys (Node.js) worker? [Y/n] " yn
    if [[ "$yn" != [nN] ]]; then
        setup_baileys && SETUP_BAILEYS=true
    fi
fi

echo ""

if [ "$HAS_GO" = true ]; then
    read -rp "Set up whatsmeow (Go) worker? [Y/n] " yn
    if [[ "$yn" != [nN] ]]; then
        setup_whatsmeow && SETUP_WHATSMEOW=true
    fi
fi

echo ""
echo "=== Setup Complete ==="
echo ""

if [ "$SETUP_BAILEYS" = true ]; then
    ok "Baileys driver ready. Use Driver: \"baileys\""
fi
if [ "$SETUP_WHATSMEOW" = true ]; then
    ok "whatsmeow driver ready. Use Driver: \"whatsmeow\""
fi

echo ""
echo "Add to your appsettings.json or environment:"
echo ""
cat <<'EOF'
  "Channels": {
    "WhatsApp": {
      "Enabled": true,
      "Type": "first_party_worker",
      "FirstPartyWorker": {
        "Driver": "baileys",
        "StoragePath": "./memory/whatsapp-worker",
        "Accounts": [{
          "AccountId": "default",
          "SessionPath": "./session/default",
          "PairingMode": "qr",
          "DeviceName": "OpenClaw"
        }]
      }
    }
  }
EOF

echo ""
echo "See docs/WHATSAPP_SETUP.md for full configuration reference."
