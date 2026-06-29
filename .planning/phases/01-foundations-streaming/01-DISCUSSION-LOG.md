# Phase 1: Foundations + Streaming - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in 01-CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-09
**Phase:** 1-Foundations + Streaming
**Mode:** --auto (all areas auto-resolved with recommended defaults)
**Areas discussed:** proto-sentinel-values, hysteresis-placement, hst-normalization, dev-topology, ha-reconnect-strategy, mqtt-unique-id

---

## Proto Sentinel Values (score=0.0)

| Option | Description | Selected |
|--------|-------------|----------|
| `google.protobuf.DoubleValue` wrapper | Idiomatic proto3 null-aware wrapper; presence is explicit | ✓ |
| `has_score: bool` alongside `double score` | Explicit bool field; more verbose, less idiomatic | |

**Auto-selected:** DoubleValue wrapper (recommended default)
**Notes:** Research (PITFALLS.md) flagged this as a high-priority Phase 1 correctness trap. Apply to `score`, `expected`, `lower`, `upper` in Verdict.

---

## Hysteresis Gate Placement

| Option | Description | Selected |
|--------|-------------|----------|
| Orchestrator layer (.NET) | Owns MQTT state; stateless Python detector | ✓ |
| Detector layer (Python) | Per-entity state in detector; more latency | |
| Both | Redundant; complex coordination | |

**Auto-selected:** Orchestrator layer (recommended default)
**Notes:** Python detector returns raw scores; .NET orchestrator debounces before MQTT publish. Defaults: high=0.7, low=0.3, min_consecutive=3, per-entity overridable.

---

## River HST Feature Normalization

| Option | Description | Selected |
|--------|-------------|----------|
| Online per-entity min-max | Self-calibrating; no config overhead | ✓ |
| Static bounds from config | Deterministic; requires knowing sensor ranges upfront | |

**Auto-selected:** Online per-entity min-max (recommended default)
**Notes:** River HST requires features in [0,1]. Learn bounds from stream, clip until stable. Warm-up period expected.

---

## Development Topology

| Option | Description | Selected |
|--------|-------------|----------|
| Two-host with mTLS from day one | Validates cert setup early; no prod surprise | ✓ |
| Single-host local dev, mTLS later | Faster initial dev; mTLS pain deferred | |

**Auto-selected:** Two-host with mTLS from day one (recommended default)
**Notes:** Research flagged mTLS SAN mismatch as a top pitfall. Better to catch it in Phase 1 than after streaming is working.

---

## HA WebSocket Reconnect Strategy

| Option | Description | Selected |
|--------|-------------|----------|
| get_states + 60s suppression | Snapshot state; suppress binary_sensor for 60s | ✓ |
| Replay burst | Simple; but causes false anomaly cascade | |

**Auto-selected:** get_states + 60s suppression (recommended default)
**Notes:** Research (PITFALLS.md) documented the false-anomaly-cascade failure mode from reconnect bursts.

---

## MQTT unique_id Formula

| Option | Description | Selected |
|--------|-------------|----------|
| `argus_{entity_slug}_{detector}_{suffix}` | Deterministic from config; stable | ✓ |
| Random/UUID-based | Stable per instance but not across reinstalls | |

**Auto-selected:** Deterministic formula (recommended default)
**Notes:** Set `object_id` to same slug. Replace `.` with `_` in entity_id for the slug. Avoids Polish-character mangling in HA entity_id derivation.

---

## Claude's Discretion

- Polly retry parameters for MQTTnet reconnect
- gRPC channel keepalive and max message size
- .NET logging: `ILogger<T>` with structured properties (no external framework in Phase 1)
- Python logging: standard `logging` module with JSON formatter

## Deferred Ideas

- InfluxDB batch path → Phase 2
- STL seasonal detection → Phase 2/3
- GPU detectors → Phase 4
- Hot-reload for entities.yaml → v2
- CPU-only edge detector replica → Phase 2 decision
