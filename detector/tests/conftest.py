"""Collection gate for the F13 final-acceptance module.

`test_rmad_real_series.py` enforces the D-J per-sensor alarm thresholds on the
operator's OWN Recorder series. Those series live in
`tests/fixtures/real_24h.json`, and only the live instance can produce them
(`detector/scripts/dump_real_24h.py`), so on any machine but that one the module
has no data to run against.

It is IGNORED at collection time rather than skipped, on purpose.
docs/FIX-PLAN.md section 5 makes "exactly one skip" (the Windows SIGTERM one) an
acceptance criterion for WS1, and the reason is not bookkeeping: a suite that
grows a habit of skipping stops reporting the one skip that matters, and `-q`
prints no skip lines at all, so five parametrised skips are invisible in the
output everyone actually reads. The open item is not swallowed with them --
`pytest_terminal_summary` below names it on EVERY run, quiet mode included.

The gate is self-arming: drop the fixture in place and the module is collected
and enforced, with no edit here or there.
"""

from pathlib import Path

REAL_SERIES_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "real_24h.json"

CAPTURE_COMMAND = (
    "HA_URL=... HA_TOKEN=... python detector/scripts/dump_real_24h.py --hours 168"
)

collect_ignore = [] if REAL_SERIES_FIXTURE.exists() else ["test_rmad_real_series.py"]


def pytest_terminal_summary(terminalreporter):
    """Report the dormant F13 acceptance loudly, in every run and every mode."""
    if REAL_SERIES_FIXTURE.exists():
        return
    terminalreporter.write_line(
        "F13 OPEN: test_rmad_real_series.py not collected - "
        f"{REAL_SERIES_FIXTURE} is missing. WS1 alarm rates are pinned on "
        "synthetic shapes only (docs/FIX-PLAN.md section 5, "
        'F13 "DEGRADOWANE do na fikstursach"). Capture it with: '
        f"{CAPTURE_COMMAND}",
        yellow=True,
    )
