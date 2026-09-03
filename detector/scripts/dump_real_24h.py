"""
One-off dump of the five F13 reference series from Home Assistant's Recorder
into detector/tests/fixtures/real_24h.json.

This is the DEPENDS of the F13 acceptance criterion (docs/FIX-PLAN.md section
5): every WS1 number is currently measured on synthetic series shaped like the
real ones, and final acceptance is deferred until the real series are on disk.
Once this file has produced the fixture, detector/tests/test_rmad_real_series.py
stops skipping and enforces the D-J per-sensor thresholds.

Run from repo root against the operator's own instance:

    export HA_URL=http://homeassistant.local:8123
    export HA_TOKEN=<long-lived access token>
    python detector/scripts/dump_real_24h.py --hours 168

stdlib only, on purpose: rmad and everything that guards it must import on a
bare python:3.12-slim image with no wheels installed.

NOTE (deviation from the plan text): the plan names the WebSocket command
`history/history_during_period`. stdlib has no WebSocket client, so this script
uses the REST endpoint `/api/history/period`, which reads the same Recorder
tables and returns the same rows. The detector itself is unaffected — this
script never runs inside the add-on.
"""

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
OUT_FILE = REPO_ROOT / "detector" / "tests" / "fixtures" / "real_24h.json"

# The five sensors F13 was measured on (docs/FIX-PLAN.md section 1).
ENTITY_IDS = (
    "sensor.load_5m",
    "sensor.memory_use_percent",
    "sensor.processor_use",
    "sensor.lodowkababcia_power",
    "sensor.zamrazarkapiwnica_power",
)


def _get(base_url: str, token: str, path: str, query: dict | None = None):
    url = base_url.rstrip("/") + path
    if query:
        url += "?" + urllib.parse.urlencode(query)
    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.loads(response.read().decode("utf-8"))


def _unit(base_url: str, token: str, entity_id: str) -> str | None:
    state = _get(base_url, token, f"/api/states/{entity_id}")
    return state.get("attributes", {}).get("unit_of_measurement")


def _values(base_url: str, token: str, entity_id: str, hours: int) -> list[float]:
    start = datetime.now(timezone.utc) - timedelta(hours=hours)
    blocks = _get(
        base_url,
        token,
        "/api/history/period/" + start.isoformat(),
        {
            "filter_entity_id": entity_id,
            "minimal_response": "",
            "no_attributes": "",
        },
    )
    values: list[float] = []
    for block in blocks:
        for row in block:
            try:
                values.append(float(row["state"]))
            except (KeyError, TypeError, ValueError):
                # unknown / unavailable / restart gaps — the detector never sees
                # these either (HaListenerWorker drops non-numeric states).
                continue
    return values


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--hours",
        type=int,
        default=168,
        help="Recorder lookback in hours (F12: this instance keeps 7 days)",
    )
    parser.add_argument("--out", type=Path, default=OUT_FILE)
    args = parser.parse_args()

    base_url = os.environ.get("HA_URL")
    token = os.environ.get("HA_TOKEN")
    if not base_url or not token:
        print("ERROR: set HA_URL and HA_TOKEN", file=sys.stderr)
        return 1

    series = {}
    for entity_id in ENTITY_IDS:
        try:
            values = _values(base_url, token, entity_id, args.hours)
            unit = _unit(base_url, token, entity_id)
        except (urllib.error.URLError, urllib.error.HTTPError) as exc:
            print(f"ERROR: {entity_id}: {exc}", file=sys.stderr)
            return 1
        if len(values) < 720:
            # Fail loud: a short series would silently make every threshold in
            # test_rmad_real_series.py meaningless (the baseline window is 720).
            print(
                f"ERROR: {entity_id}: only {len(values)} numeric rows, "
                f"need at least 720 — raise --hours",
                file=sys.stderr,
            )
            return 1
        series[entity_id] = {"unit_of_measurement": unit, "values": values}
        print(f"{entity_id}: {len(values)} rows, unit {unit!r}")

    payload = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "lookback_hours": args.hours,
        "series": series,
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(payload), encoding="utf-8")
    print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
