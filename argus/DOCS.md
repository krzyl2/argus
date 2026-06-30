# Argus Anomaly Detection

Argus provides streaming and batch anomaly detection for Home Assistant environmental sensors
(temperature, humidity, pressure — indoor and outdoor). Detected anomalies are surfaced back
into Home Assistant as auto-created `binary_sensor` (flag) and `sensor` (score) entities via
MQTT discovery — no manual entity creation and no HA restart required. The add-on is
fully self-hosted with no cloud dependency.

## Prerequisites

### Mosquitto Broker Add-on

The official **Mosquitto broker** add-on (slug: `core_mosquitto`) must be installed and running
**before** starting Argus.

Argus declares `services: [mqtt:need]` in its add-on manifest. The HA Supervisor uses this
declaration to auto-inject the MQTT host, username, and password into the add-on at startup.
**You must never enter MQTT credentials manually** — they are fetched automatically and any
manual entry would be ignored or cause a conflict. If no MQTT service is registered with the
Supervisor when Argus starts, the Supervisor will fail the add-on immediately (exit non-zero)
and the add-on will not run.

To install Mosquitto:
1. Open **Settings → Add-ons → Add-on Store** in Home Assistant.
2. Search for **"Mosquitto broker"** and click **Install**.
3. Start the Mosquitto broker and confirm it is in the **Running** state before starting Argus.

### Home Assistant API Access

Argus uses `homeassistant_api: true`. Your HA URL and long-lived access token are provided
automatically by the Supervisor — **you do not need to enter a HA URL or token** in the
configuration.

### InfluxDB (Optional)

InfluxDB is only required if you want to enable the **batch anomaly detection** path (historical
data analysis). Streaming detection (real-time `state_changed` events) works without InfluxDB.
Leave all `influx_*` fields empty to run in streaming-only mode.

## Installation

1. **Add the Argus custom repository** to your HA add-on store:
   - Open **Settings → Add-ons → Add-on Store**.
   - Click the **⋮** (three-dot menu) in the top-right corner and choose **Repositories**.
   - Paste `https://github.com/krzyl2/argus` and click **Add**, then **Close**.

2. **Find and install Argus**: search for **"Argus Anomaly Detection"** in the store and click
   **Install**.

3. **Open the Configuration tab** and set the entities you want to monitor and, if needed,
   your InfluxDB connection details. See the [Configuration](#configuration) section below.

4. **Start the add-on**. On first start, Argus logs the numeric HA sensor entity IDs it
   discovered so you can copy them into the `entities` list. Check the **Log** tab after
   startup to see the discovery output.

## Configuration

### `entities`

**Type:** list of strings | **Default:** `[]`

Home Assistant `entity_id` strings to monitor for anomalies (e.g. `sensor.salon_temperatura`).
Enter one entity ID per line. Only numeric sensors produce meaningful anomaly scores; non-numeric
entities are silently skipped.

Use either `entities` for an explicit list or `include_patterns`/`exclude_patterns` for
glob-based selection — both methods can be combined (include_patterns are applied first, then
exclude_patterns, then the explicit entity list is unioned in).

**Default:** `[]` (no entities monitored until you configure this or `include_patterns`)

---

### `include_patterns`

**Type:** list of strings | **Default:** `[]`

Glob patterns to filter which entities are monitored (e.g. `sensor.outdoor_*`). Leave empty
to use the explicit entity list only. When patterns are specified, any entity whose `entity_id`
matches at least one pattern is included (before `exclude_patterns` are applied).

**Default:** `[]`

---

### `exclude_patterns`

**Type:** list of strings | **Default:** `[]`

Glob patterns to exclude entities from monitoring (e.g. `sensor.*_voltage`). Applied after
`include_patterns`. An entity matching an exclude pattern is removed from the monitored set
even if it matched an include pattern or appears in `entities`.

**Default:** `[]`

---

### `influx_url`

**Type:** URL (optional) | **Default:** *(empty — batch path disabled)*

InfluxDB v2 server URL (e.g. `http://192.168.1.10:8086`). Leave empty to disable batch
anomaly detection. When empty, Argus runs in streaming-only mode and the remaining `influx_*`
fields are ignored. No error is raised when `influx_url` is empty — the batch path is simply
not started.

**Default:** *(empty)*

---

### `influx_token`

**Type:** password (optional) | **Default:** *(empty)*

InfluxDB v2 API token with read access to the sensor bucket. Only used when `influx_url` is
set. The value is stored as a password field (masked in the UI).

**Default:** *(empty)*

---

### `influx_org`

**Type:** string (optional) | **Default:** *(empty)*

InfluxDB organization name. Required when `influx_url` is set.

**Default:** *(empty)*

---

### `influx_bucket`

**Type:** string (optional) | **Default:** *(empty)*

InfluxDB bucket containing Home Assistant sensor history. Required when `influx_url` is set.

**Default:** *(empty)*

---

### `influx_measurement`

**Type:** string | **Default:** `homeassistant`

InfluxDB measurement name for HA states. This is the measurement that the HA InfluxDB
integration writes to. Change this only if you configured a non-default measurement name in
your HA InfluxDB integration settings.

**Default:** `homeassistant`

---

### `influx_value_field`

**Type:** string | **Default:** `value`

InfluxDB field key for sensor values. This is the field that the HA InfluxDB integration uses
for the numeric state. Change this only if your InfluxDB schema uses a different field name.

**Default:** `value`

---

### `detector_endpoint`

**Type:** string (optional) | **Default:** *(empty — bundled local detector)*

Remote gRPC detector URL (e.g. `https://gpu-host:50051`). Leave empty to run the bundled
local detector inside the add-on container. Set this to the address of an external detector
instance (e.g. a GPU-enabled server) to offload ML inference. When set, the local detector
process is not started.

**Default:** *(empty — bundled local detector runs)*

---

### `batch_interval_minutes`

**Type:** integer (1–1440) | **Default:** `10`

How often (in minutes) to run batch anomaly detection against InfluxDB historical data.
Only relevant when `influx_url` is configured. A value of `10` runs batch detection every
10 minutes. The minimum is 1 minute, the maximum is 1440 minutes (24 hours).

**Default:** `10`

---

### `nightly_fit_hour`

**Type:** integer (0–23) | **Default:** `2`

UTC hour (0–23) at which the nightly model refit runs. The refit trains or updates the
anomaly detection models using recent historical data. The default of `2` schedules the
refit at 02:00 UTC to avoid peak HA activity hours.

**Default:** `2`

---

### `log_level`

**Type:** one of `debug`, `info`, `warning` | **Default:** `info`

Logging verbosity for the detector and orchestrator.

| Value | Meaning |
|-------|---------|
| `debug` | Verbose output including gRPC frames and internal state changes |
| `info` | Normal operation messages |
| `warning` | Errors and warnings only |

**Default:** `info`

## Troubleshooting

### Check the Argus health entity

Open **Developer Tools → States** in Home Assistant and look for:

```
binary_sensor.argus_addon_health
```

Friendly name: **Argus — status** | Device class: **problem**

| State | Meaning |
|-------|---------|
| `OFF` | Add-on is healthy — detector is serving, HA is connected, MQTT is connected |
| `ON` | A problem was detected — see the add-on Log tab for details |

The health entity is created automatically by Argus on first start via MQTT discovery. If it
does not appear in Developer Tools, check that Mosquitto is running and that Argus started
successfully.

### Add-on won't start

- Confirm the **Mosquitto broker** add-on (`core_mosquitto`) is installed and in the **Running**
  state. Argus declares `services: [mqtt:need]`; if the Supervisor finds no MQTT service, it
  refuses to start Argus.
- Check the **Log** tab of the Argus add-on for the exact error message.

### No anomaly entities appear in Home Assistant

- Confirm Mosquitto is installed and running.
- Confirm your monitored entities are **numeric sensors** (non-numeric states are skipped).
- On first start, Argus logs the numeric sensors it discovered — open the **Log** tab and
  copy the entity IDs from the discovery output into the `entities` field.
- Verify that the entity IDs you entered match those shown in **Developer Tools → States**
  exactly (case-sensitive).

### Streaming anomalies are delayed or missing

- Check `binary_sensor.argus_addon_health` — if it is `ON`, the detector may not be serving.
- Increase `log_level` to `debug` and restart the add-on to get detailed gRPC and MQTT trace
  output in the Log tab.

### Log tab

The add-on **Log** tab is the primary diagnostic surface. All orchestrator and detector output
is routed there. Set `log_level` to `debug` for verbose traces when diagnosing connection
or detection issues.

## Support

Report issues and request features at:
<https://github.com/krzyl2/argus/issues>
