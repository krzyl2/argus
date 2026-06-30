#!/usr/bin/with-contenv bashio
# cont-init.d/10-config-gen.sh
# Oneshot: materializes s6 environment variables and /data/entities.yaml before any service starts.
# Runs serially before services.d/ (s6 lexicographic cont-init.d ordering guarantee).
#
# Requirements met:
#   SUPV-01 — ARGUS_HA_URL=ws://supervisor:80 + ARGUS_HA_TOKEN from SUPERVISOR_TOKEN (no manual token)
#   SUPV-02 — MQTT creds from bashio::services; exit 1 if unavailable
#   UICFG-08 — /data/entities.yaml generated from /data/options.json via gen-entities.py
#
# Security:
#   T-1-04 — secrets written via printf to /var/run/s6/container_environment/ (never echo, never logged)

set -e

# Write an optional add-on option to an s6 env file, emitting "" when the key is
# absent/null/empty. bashio::config returns the literal string "null" for an
# absent optional key, so a plain `bashio::config 'x' || echo ""` writes "null"
# (a truthy value downstream). bashio::config.has_value is true only when the key
# exists AND is non-null/non-empty — the correct gate for optional fields.
write_optional_env() {
    local key="${1}" dest="${2}"
    if bashio::config.has_value "${key}"; then
        printf "%s" "$(bashio::config "${key}")" > "${dest}"
    else
        printf "" > "${dest}"
    fi
}

# ── HA Auth (SUPV-01) ────────────────────────────────────────────────────────
# Add-ons reach the HA WebSocket API through the Supervisor proxy at
# ws://supervisor/core/websocket (host supervisor, port 80, path /core/websocket),
# authenticated with SUPERVISOR_TOKEN. NOT ws://supervisor:80 — .NET treats :80 as
# the ws default port, so the old code overrode it to HA core's 8123 (unreachable
# from the add-on) and used the wrong default path /api/websocket.
# homeassistant_api: true in config.yaml ensures SUPERVISOR_TOKEN is injected.
# Do NOT write HA IConfiguration key overrides — Program.cs reads ARGUS_* vars directly.
printf "ws://supervisor/core/websocket" > /var/run/s6/container_environment/ARGUS_HA_URL
printf "%s" "${SUPERVISOR_TOKEN}" > /var/run/s6/container_environment/ARGUS_HA_TOKEN

# ── MQTT Credentials (SUPV-02) ───────────────────────────────────────────────
# config.yaml declares services: [mqtt:need] — fail loud rather than silently
# connecting with empty credentials (Pitfall 2: mqtt:want returns empty strings).
if ! bashio::services.available "mqtt"; then
    bashio::log.fatal "MQTT service is not available. Install the Mosquitto add-on first."
    exit 1
fi
printf "%s" "$(bashio::services mqtt "host")"     > /var/run/s6/container_environment/ARGUS_MQTT_HOST
printf "%s" "$(bashio::services mqtt "port")"     > /var/run/s6/container_environment/ARGUS_MQTT_PORT
printf "%s" "$(bashio::services mqtt "username")" > /var/run/s6/container_environment/ARGUS_MQTT_USER
printf "%s" "$(bashio::services mqtt "password")" > /var/run/s6/container_environment/ARGUS_MQTT_PASSWORD

# ── Detector Mode (local vs remote) ─────────────────────────────────────────
# detector_endpoint is optional (str? in schema; absent from options defaults).
# Empty / absent = bundled local detector. Non-empty = remote gRPC with mTLS.
mkdir -p /run/argus
# Use bashio::config.has_value, NOT `[ -z "$(bashio::config 'detector_endpoint')" ]`:
# bashio returns the literal "null" for an absent optional key, which -z treats as
# non-empty and would wrongly select remote/mTLS mode for the default (no endpoint)
# configuration — the local detector would never start and the orchestrator would
# try to load nonexistent cert files. has_value is true only for a real value.
if bashio::config.has_value 'detector_endpoint'; then
    printf "%s" "$(bashio::config 'detector_endpoint')" > /var/run/s6/container_environment/ARGUS_DETECTOR_ENDPOINT
    printf "/data/certs/ca.crt"     > /var/run/s6/container_environment/ARGUS_TLS_CA
    printf "/data/certs/client.crt" > /var/run/s6/container_environment/ARGUS_TLS_CERT
    printf "/data/certs/client.key" > /var/run/s6/container_environment/ARGUS_TLS_KEY
    printf "remote"                 > /run/argus/mode
    # PROC-04: write the down file so s6 does not start the local detector in remote mode.
    # The detector/run script is never reached; only the orchestrator starts.
    touch /etc/services.d/detector/down
else
    printf "http://127.0.0.1:50051" > /var/run/s6/container_environment/ARGUS_DETECTOR_ENDPOINT
    # Bind the detector on all interfaces (not loopback-only) so the Supervisor
    # watchdog (tcp://[HOST]:50051 in config.yaml, PROC-05) can probe it on the
    # add-on's container IP. The orchestrator still dials 127.0.0.1:50051 over
    # loopback; a 127.0.0.1-only bind made the watchdog fail and the Supervisor
    # restart the add-on. Exposure is limited to the HA internal add-on network.
    printf "0.0.0.0"                > /var/run/s6/container_environment/ARGUS_GRPC_BIND
    printf "local"                  > /run/argus/mode
fi
mkdir -p /data/models
printf "/data/models" > /var/run/s6/container_environment/ARGUS_MODEL_ROOT

# ── InfluxDB (UICFG-02) ─────────────────────────────────────────────────────
# Optional fields (url?, token?, org?, bucket?) are absent from options defaults.
# Empty influx_url disables the batch path in the orchestrator (InfluxDbReader no-ops).
write_optional_env 'influx_url'    /var/run/s6/container_environment/ARGUS_INFLUX_URL
write_optional_env 'influx_token'  /var/run/s6/container_environment/ARGUS_INFLUX_TOKEN
write_optional_env 'influx_org'    /var/run/s6/container_environment/ARGUS_INFLUX_ORG
write_optional_env 'influx_bucket' /var/run/s6/container_environment/ARGUS_INFLUX_BUCKET
printf "%s" "$(bashio::config 'influx_measurement')"           > /var/run/s6/container_environment/ARGUS_INFLUX_MEASUREMENT
printf "%s" "$(bashio::config 'influx_value_field')"           > /var/run/s6/container_environment/ARGUS_INFLUX_VALUE_FIELD

# ── Batch Schedule (UICFG-04) ────────────────────────────────────────────────
printf "%s" "$(bashio::config 'batch_interval_minutes')" > /var/run/s6/container_environment/ARGUS_BATCH_INTERVAL_MIN
printf "%s" "$(bashio::config 'nightly_fit_hour')"       > /var/run/s6/container_environment/ARGUS_NIGHTLY_FIT_HOUR

# ── Log Level (wired to both processes) ─────────────────────────────────────
LOG_LEVEL_RAW=$(bashio::config 'log_level')
# Detector reads ARGUS_LOG_LEVEL uppercased (DetectorConfig: os.environ.get("ARGUS_LOG_LEVEL", "INFO"))
LOG_LEVEL_UPPER=$(echo "${LOG_LEVEL_RAW}" | tr '[:lower:]' '[:upper:]')
printf "%s" "${LOG_LEVEL_UPPER}" > /var/run/s6/container_environment/ARGUS_LOG_LEVEL
# Orchestrator reads Logging__LogLevel__Default via .NET double-underscore env convention
case "${LOG_LEVEL_RAW}" in
    debug)   DOTNET_LOG="Debug" ;;
    warning) DOTNET_LOG="Warning" ;;
    *)       DOTNET_LOG="Information" ;;
esac
printf "%s" "${DOTNET_LOG}" > /var/run/s6/container_environment/Logging__LogLevel__Default

# ── entities.yaml Generation (UICFG-08) ────────────────────────────────────
printf "/data/entities.yaml" > /var/run/s6/container_environment/ARGUS_ENTITIES_PATH
python3 /usr/local/bin/gen-entities.py /data/options.json > /data/entities.yaml

bashio::log.info "Config-gen complete."
