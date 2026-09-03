"""
F13 final acceptance, on the REAL Recorder series.

Everything else in WS1 is measured on synthetic series SHAPED like the five
sensors of the live instance (docs/FIX-PLAN.md section 5: "F13 — DEGRADOWANE do
'na fikstursach'"). That degradation is honest but incomplete: a synthetic
series can only contain the failure modes whoever wrote it thought of, and F13
is the criterion that says the fix actually reduced the alarm rate on the
operator's own data — the one claim WS1 cannot make from generated numbers.

This module is that criterion, wired up and dormant. It stays skipped until
detector/tests/fixtures/real_24h.json exists, and the skip names the exact
command that produces it. The thresholds are D-J (docs/FIX-PLAN.md section 2),
i.e. the FALSIFIABLE half of F3 — alarm rate, silence on the freezer, survival
of the fridge compressor events. D-J deliberately states no precision target
for the three system sensors, because rmad computes the same robust-z that F3
used to define an outlier, so a precision number here would be half
self-confirming.

Producing the fixture requires the operator's live HA and is therefore out of
reach of an automated run; that is an environmental block, and this file is
what turns it into a red test instead of an unwritten one.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from test_rmad_detector import _gate_stats, _stream

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "real_24h.json"

_MISSING = (
    f"{FIXTURE} not captured yet - F13 final acceptance is still open. "
    "Produce it on the live instance with: HA_URL=... HA_TOKEN=... "
    "python detector/scripts/dump_real_24h.py --hours 168"
)

# D-J, per sensor: (max episodes or None, max on-time percent or None,
# min episodes or None). None means "not a criterion for this sensor".
_ACCEPTANCE = {
    "sensor.load_5m": (6, 7.0, None),
    "sensor.memory_use_percent": (0, None, None),
    "sensor.processor_use": (3, 2.0, None),
    "sensor.lodowkababcia_power": (None, None, 2),
    "sensor.zamrazarkapiwnica_power": (0, None, None),
}


def _fixture() -> dict:
    if not FIXTURE.exists():
        pytest.skip(_MISSING)
    return json.loads(FIXTURE.read_text(encoding="utf-8"))


@pytest.mark.parametrize("entity_id", sorted(_ACCEPTANCE))
def test_real_series_meets_the_per_sensor_acceptance_thresholds(entity_id):
    payload = _fixture()
    series = payload["series"].get(entity_id)
    assert series is not None, f"{entity_id} missing from {FIXTURE}"

    values = series["values"]
    # The baseline window is 720 samples; a shorter series would make every
    # threshold below pass for the wrong reason.
    assert len(values) >= 720, f"{entity_id}: only {len(values)} rows"

    # D-I: scale_floor is in SENSOR UNITS, and WS2 derives it from the HA unit —
    # 0.3 for percent series, 0.0 otherwise. Replaying with a different floor
    # than production would measure a detector nobody ships.
    scale_floor = 0.3 if series.get("unit_of_measurement") == "%" else 0.0

    episodes, on_time = _gate_stats(_stream(values, scale_floor=scale_floor))
    max_episodes, max_on_time, min_episodes = _ACCEPTANCE[entity_id]

    if max_episodes is not None:
        assert episodes <= max_episodes, (
            f"{entity_id}: {episodes} episodes, D-J allows {max_episodes}"
        )
    if min_episodes is not None:
        # F3: the fridge compressor is the only alarm on this installation with
        # real precision (83%). Killing false alarms must not kill it.
        assert episodes >= min_episodes, (
            f"{entity_id}: {episodes} episodes, D-J requires at least "
            f"{min_episodes} real compressor cycles"
        )
    if max_on_time is not None:
        # F1 was five flags at 100/100/99/91/25% on-time. On-time is the
        # criterion that makes "stuck ON" impossible to ship again.
        assert on_time <= max_on_time, (
            f"{entity_id}: {on_time:.2f}% on-time, D-J allows {max_on_time}%"
        )
