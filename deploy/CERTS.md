# Argus mTLS Certificate Management

## Overview

Argus uses mutual TLS (mTLS) on the gRPC channel between the edge host (orchestrator) and the GPU host (detector). All certificates are self-signed under a project-specific CA (`ArgusCA`). No external PKI is required.

**Files in `deploy/certs/` after generation:**

| File | Description | Git |
|------|-------------|-----|
| `ca.crt` | Self-signed CA root — shared with both hosts | gitignored |
| `server.crt` | GPU host server cert — SAN includes LAN IP + hostname | gitignored |
| `server.key` | GPU host server private key | gitignored |
| `client.crt` | Edge host client cert — CN=edge-host | gitignored |
| `client.key` | Edge host client private key | gitignored |
| `server-ext.cnf` | SAN extension template (placeholders only — committed) | committed |

**All `.key` and `.crt` files are gitignored** — they must never enter version control. Only the placeholder `server-ext.cnf` template is committed.

---

## Generating Certificates

Run `generate-certs.sh` from the repo root, providing the GPU host's static LAN IP and hostname:

```bash
GPU_HOST_IP=192.168.1.100 GPU_HOST_NAME=gpu-host bash deploy/generate-certs.sh
```

Or pass as positional arguments:

```bash
bash deploy/generate-certs.sh 192.168.1.100 gpu-host
```

The script will:
1. Generate a 4096-bit RSA CA key and self-signed CA certificate (730-day expiry)
2. Generate a server key + CSR for the GPU host
3. Sign the server cert with the CA, embedding both the LAN IP and hostname in the SAN
4. Generate a client key + CSR for the edge host
5. Sign the client cert with the CA
6. Verify the chain and print the SAN for confirmation

### CRITICAL: GPU Host IP and Hostname

The server certificate SAN must contain **both** the LAN IP and the hostname that the orchestrator uses to dial the detector:

```
subjectAltName=IP:192.168.1.100,DNS:gpu-host
```

If the orchestrator dials `https://192.168.1.100:50051`, the IP must be in the SAN.
If it dials `https://gpu-host:50051`, the hostname must be in the SAN.
**A SAN mismatch causes every gRPC call to fail with an SSL handshake error** (PITFALL 2).

If the GPU host's IP is not static, configure a DHCP reservation or local DNS entry before generating certs.

---

## Distributing Certificates

After generation, copy the cert files to both hosts:

**GPU host (detector):**
```
deploy/certs/ca.crt      → /etc/argus/certs/ca.crt
deploy/certs/server.crt  → /etc/argus/certs/server.crt
deploy/certs/server.key  → /etc/argus/certs/server.key
```

**Edge host (orchestrator):**
```
deploy/certs/ca.crt      → /etc/argus/certs/ca.crt
deploy/certs/client.crt  → /etc/argus/certs/client.crt
deploy/certs/client.key  → /etc/argus/certs/client.key
```

In Docker deployments, the certs are volume-mounted at the paths configured in `docker-compose.gpu.yml` and `docker-compose.edge.yml`.

---

## Inspecting Certificates

### Verify the server cert chain

```bash
openssl verify -CAfile deploy/certs/ca.crt deploy/certs/server.crt
```

Expected output: `deploy/certs/server.crt: OK`

### Confirm the SAN

```bash
openssl x509 -in deploy/certs/server.crt -noout -text | grep -A1 "Subject Alternative Name"
```

Expected output contains both the IP and hostname:
```
X509v3 Subject Alternative Name:
    IP Address:192.168.1.100, DNS:gpu-host
```

### Check expiry

```bash
openssl x509 -in deploy/certs/server.crt -noout -dates
```

### Validate the client cert

```bash
openssl verify -CAfile deploy/certs/ca.crt deploy/certs/client.crt
```

---

## Certificate Rotation (2-Year Reminder)

All certs are issued with **730-day (2-year) expiry**. Set a calendar reminder to regenerate before expiry.

To rotate:
1. Re-run `generate-certs.sh` with the same GPU host IP/hostname (or updated values if the host moved)
2. Restart the detector service on the GPU host with the new server cert/key
3. Restart the orchestrator service on the edge host with the new CA cert and client cert/key
4. Validate with `openssl verify` and a Health RPC call before declaring rotation complete

**Note:** The CA cert also expires at 730 days. Regenerating certs with `generate-certs.sh` creates a new CA each time — both sides must receive the updated `ca.crt` together.

---

## IMPORTANT: Placeholder Certs (Development)

The certs in this repo (when generated with `GPU_HOST_IP=192.168.1.100 GPU_HOST_NAME=gpu-host`) are **development placeholders only**. They were generated with placeholder values to unblock Phase 1 development.

**Before deploying to a real environment:**
1. Determine the actual GPU host LAN IP (set a DHCP reservation if needed)
2. Determine the actual GPU host hostname
3. Re-run `generate-certs.sh` with the real values
4. Distribute the regenerated certs to both hosts
5. Validate with `openssl verify` and a live Health RPC call

The placeholder certs will not work in production because the SAN (`192.168.1.100`, `gpu-host`) will not match the real host addresses.
