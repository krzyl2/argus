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

## Using the Ingress UI

> The Ingress UI replaces manual YAML editing for entity selection and detector configuration.
> You can configure Argus entirely through the UI with zero manual YAML editing.

### Opening the UI

1. Open **Settings → Add-ons** in Home Assistant and click **Argus Anomaly Detection**.
2. Click **Open Web UI** on the add-on detail page.

The UI is served through HA Ingress — it is not accessible on a separate port and does not
require you to open any firewall rules. Home Assistant handles authentication automatically.

### Selecting Entities

The main page lists all numeric sensors currently live in Home Assistant. Use the search box
to filter by name or entity ID, then tick the checkbox next to each sensor you want to monitor.

Only numeric sensors produce meaningful anomaly scores. Non-numeric entities are not listed.
You can add or remove sensors at any time; saving applies the change immediately without
restarting the add-on.

### Assigning Detectors

Each selected entity has a detector section below its checkbox row. You can assign one or
more detectors to each entity:

- **HST** (Half-Space Trees) — online streaming detector; updates with every reading.
  Parameters: `window`, `n_trees`, `high_threshold`, `low_threshold`, `min_consecutive`,
  `frozen_window`, `frozen_variance_threshold`.
- **MAD** (Median Absolute Deviation) — batch detector; trained on InfluxDB history.
  Parameters: `threshold`, `window`.
- **STL** (Seasonal-Trend decomposition) — batch detector; trained on InfluxDB history.
  Parameters: `period`, `seasonal`, `threshold`.

Use **Add detector** to attach additional detectors to an entity and **Remove** to detach one.
Parameter fields are validated inline; the **Save** button is disabled until all fields are valid.

### Applying Changes (No Restart Required)

Click **Save** to write the configuration and reload the anomaly pipeline. The add-on does not
restart — only the pipeline is swapped out. MQTT and gRPC connections remain alive during the
reload; the streaming gap is under one second.

**HST warm-up:** After saving a configuration that includes HST detectors, allow approximately
**4 minutes** before anomaly scores reflect real patterns. This figure is derived from River's
HST `window=250` (the number of readings required to build the initial model) at a typical rate
of one reading per second per entity: 250 s ≈ 4 minutes. Anomaly scores will be low or
near-zero until the warm-up completes. MAD and STL are batch detectors trained on InfluxDB
historical data and have no comparable warm-up period.

### Recovering a Corrupted Configuration

If you manually edit `/data/entities.yaml` and introduce a YAML syntax or structural error,
the orchestrator will fail to load the file on the next restart and the add-on will keep
restarting in a crash loop. To recover:

1. Open the add-on **Log** tab — the error message identifies the exact YAML problem.
2. **Option A (fix YAML):** SSH into the host, open `/data/entities.yaml` in a text editor,
   correct the error, then restart the add-on from the HA UI.
3. **Option B (reset to UI re-entry):** Delete `/data/entities.yaml` **and**
   `/data/.ui_config_present`, then restart the add-on. The orchestrator starts with an empty
   pipeline. Open the UI to re-configure from scratch.

**How the lock file works:** After each successful UI save, Argus writes a `/data/.ui_config_present`
marker file. The `gen-entities.py` script that runs at add-on startup checks for this marker;
if present, it skips overwriting `entities.yaml` — so a restart after a UI save never silently
erases your configuration. Deleting the marker (Option B) lets the startup script regenerate a
clean baseline, which the orchestrator accepts as an empty pipeline.

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
