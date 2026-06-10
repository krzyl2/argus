#!/usr/bin/env bash
# generate-certs.sh — Argus mTLS certificate generation
#
# Usage:
#   GPU_HOST_IP=192.168.1.100 GPU_HOST_NAME=gpu-host bash deploy/generate-certs.sh
#   OR
#   bash deploy/generate-certs.sh <GPU_HOST_IP> <GPU_HOST_NAME>
#
# Outputs: deploy/certs/{ca,server,client}.{crt,key}
# Private keys (.key) are gitignored — never commit them.
#
# Run this script whenever:
#   - The GPU host IP or hostname changes (SAN mismatch will break gRPC)
#   - The 2-year cert expiry is approaching (run before 730 days from issue date)

set -euo pipefail

# ---------------------------------------------------------------------------
# Resolve parameters — env vars take precedence; positional args are fallback
# ---------------------------------------------------------------------------
GPU_HOST_IP="${GPU_HOST_IP:-${1:-}}"
GPU_HOST_NAME="${GPU_HOST_NAME:-${2:-}}"

if [[ -z "${GPU_HOST_IP}" ]]; then
  echo "ERROR: GPU_HOST_IP is not set." >&2
  echo "  Set via env: GPU_HOST_IP=192.168.1.100 GPU_HOST_NAME=gpu-host bash $0" >&2
  echo "  Or pass as args: bash $0 <GPU_HOST_IP> <GPU_HOST_NAME>" >&2
  exit 1
fi

if [[ -z "${GPU_HOST_NAME}" ]]; then
  echo "ERROR: GPU_HOST_NAME is not set." >&2
  echo "  Set via env: GPU_HOST_IP=192.168.1.100 GPU_HOST_NAME=gpu-host bash $0" >&2
  echo "  Or pass as args: bash $0 <GPU_HOST_IP> <GPU_HOST_NAME>" >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# Output directory — always relative to this script's location
# ---------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CERTS_DIR="${SCRIPT_DIR}/certs"
mkdir -p "${CERTS_DIR}"

echo "=== Argus mTLS Certificate Generation ==="
echo "  GPU host IP:       ${GPU_HOST_IP}"
echo "  GPU host hostname: ${GPU_HOST_NAME}"
echo "  SAN:               IP:${GPU_HOST_IP},DNS:${GPU_HOST_NAME}"
echo "  Output:            ${CERTS_DIR}"
echo ""

# ---------------------------------------------------------------------------
# 1. Certificate Authority (self-signed root of trust)
# ---------------------------------------------------------------------------
echo "[1/5] Generating CA key (4096-bit RSA)..."
openssl genrsa -out "${CERTS_DIR}/ca.key" 4096

echo "[2/5] Generating self-signed CA certificate (730-day expiry)..."
openssl req -new -x509 \
  -key "${CERTS_DIR}/ca.key" \
  -out "${CERTS_DIR}/ca.crt" \
  -days 730 \
  -subj "/CN=ArgusCA"

# ---------------------------------------------------------------------------
# 2. GPU host server certificate (SAN includes BOTH IP and DNS — CRITICAL)
#    A mismatch here will break every gRPC call with SSL handshake errors.
#    See deploy/CERTS.md — PITFALL 2.
# ---------------------------------------------------------------------------
echo "[3/5] Generating server key and CSR..."
openssl genrsa -out "${CERTS_DIR}/server.key" 4096
openssl req -new \
  -key "${CERTS_DIR}/server.key" \
  -out "${CERTS_DIR}/server.csr" \
  -subj "/CN=${GPU_HOST_NAME}"

# Write the SAN extension config inline (not from template) so the real values land here.
# server-ext.cnf in this directory is a template with placeholders — this file overwrites it
# with the real values for the duration of the signing step.
cat > "${CERTS_DIR}/server-ext.cnf" <<EOF
subjectAltName=IP:${GPU_HOST_IP},DNS:${GPU_HOST_NAME}
EOF

echo "[4/5] Signing server certificate with CA (SAN: IP:${GPU_HOST_IP},DNS:${GPU_HOST_NAME})..."
openssl x509 -req \
  -in "${CERTS_DIR}/server.csr" \
  -CA "${CERTS_DIR}/ca.crt" \
  -CAkey "${CERTS_DIR}/ca.key" \
  -CAcreateserial \
  -out "${CERTS_DIR}/server.crt" \
  -days 730 \
  -extfile "${CERTS_DIR}/server-ext.cnf"

# ---------------------------------------------------------------------------
# 3. Edge host client certificate (no SAN required — client identity only)
# ---------------------------------------------------------------------------
echo "[5/5] Generating client key, CSR, and signing client certificate..."
openssl genrsa -out "${CERTS_DIR}/client.key" 4096
openssl req -new \
  -key "${CERTS_DIR}/client.key" \
  -out "${CERTS_DIR}/client.csr" \
  -subj "/CN=edge-host"
openssl x509 -req \
  -in "${CERTS_DIR}/client.csr" \
  -CA "${CERTS_DIR}/ca.crt" \
  -CAkey "${CERTS_DIR}/ca.key" \
  -CAcreateserial \
  -out "${CERTS_DIR}/client.crt" \
  -days 730

# ---------------------------------------------------------------------------
# Clean up CSR files — not needed after signing
# ---------------------------------------------------------------------------
rm -f "${CERTS_DIR}/server.csr" "${CERTS_DIR}/client.csr"

# ---------------------------------------------------------------------------
# Verification summary
# ---------------------------------------------------------------------------
echo ""
echo "=== Certificate Chain Verification ==="
openssl verify -CAfile "${CERTS_DIR}/ca.crt" "${CERTS_DIR}/server.crt"
openssl verify -CAfile "${CERTS_DIR}/ca.crt" "${CERTS_DIR}/client.crt"

echo ""
echo "=== Server Certificate SAN ==="
openssl x509 -in "${CERTS_DIR}/server.crt" -noout -text | grep -A1 "Subject Alternative Name"

echo ""
echo "=== Done ==="
echo "Files written to ${CERTS_DIR}/"
echo "  ca.crt, server.crt, server.key, client.crt, client.key"
echo "  IMPORTANT: *.key and *.crt are gitignored. Never commit private keys."
echo "  The generated server-ext.cnf in ${CERTS_DIR}/ contains real host values — it is also gitignored."
echo "  Rotate before: $(date -d '+730 days' '+%Y-%m-%d' 2>/dev/null || date -v+730d '+%Y-%m-%d' 2>/dev/null || echo 'check issue date + 730 days')"
