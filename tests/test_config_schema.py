"""
Deterministic validation of argus/config.yaml and translation parity.

Covers:
  - test_config_yaml_valid: structural assertions on config.yaml (slug, arch,
    homeassistant_api, init, services, full schema field set with types)
  - test_schema_translation_parity: all schema fields have name+description in
    EN and PL; no translation defines a field absent from the schema
  - icon and logo PNG signatures (stdlib only, no Pillow)
"""
import pathlib
import yaml
import pytest

REPO_ROOT = pathlib.Path(__file__).parent.parent
CONFIG_PATH = REPO_ROOT / "argus" / "config.yaml"
EN_PATH = REPO_ROOT / "argus" / "translations" / "en.yaml"
PL_PATH = REPO_ROOT / "argus" / "translations" / "pl.yaml"
ICON_PATH = REPO_ROOT / "argus" / "icon.png"
LOGO_PATH = REPO_ROOT / "argus" / "logo.png"

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

EXPECTED_SCHEMA = {
    "entities": [str],          # list type — value is a list in YAML
    "include_patterns": [str],
    "exclude_patterns": [str],
    "influx_url": "url?",
    "influx_token": "password?",
    "influx_org": "str?",
    "influx_bucket": "str?",
    "influx_measurement": "str",
    "influx_value_field": "str",
    "detector_endpoint": "str?",
    "batch_interval_minutes": "int(1,1440)",
    "nightly_fit_hour": "int(0,23)",
    "log_level": "list(debug|info|warning)",
}

SCHEMA_FIELD_NAMES = set(EXPECTED_SCHEMA.keys())


def load_config():
    with open(CONFIG_PATH) as f:
        return yaml.safe_load(f)


def test_config_yaml_valid():
    cfg = load_config()

    assert cfg["slug"] == "argus", f"slug mismatch: {cfg['slug']}"
    assert cfg["arch"] == ["amd64", "aarch64"], f"arch mismatch: {cfg['arch']}"
    assert cfg["homeassistant_api"] is True, "homeassistant_api must be true"
    assert cfg["init"] is False, "init must be false"
    assert "mqtt:need" in cfg["services"], "services must include mqtt:need"

    schema = cfg["schema"]
    missing = SCHEMA_FIELD_NAMES - set(schema.keys())
    assert not missing, f"Schema fields missing from config.yaml: {missing}"

    # Type assertions for non-list fields
    assert schema["influx_token"] == "password?", \
        f"influx_token type must be password?, got: {schema['influx_token']}"
    assert schema["influx_url"] == "url?", \
        f"influx_url type must be url?, got: {schema['influx_url']}"
    assert schema["batch_interval_minutes"] == "int(1,1440)", \
        f"batch_interval_minutes type: {schema['batch_interval_minutes']}"
    assert schema["nightly_fit_hour"] == "int(0,23)", \
        f"nightly_fit_hour type: {schema['nightly_fit_hour']}"
    assert schema["log_level"] == "list(debug|info|warning)", \
        f"log_level type: {schema['log_level']}"
    assert schema["detector_endpoint"] == "str?", \
        f"detector_endpoint type: {schema['detector_endpoint']}"

    # List-type fields must be lists with a single str element
    for list_field in ("entities", "include_patterns", "exclude_patterns"):
        val = schema[list_field]
        assert isinstance(val, list) and len(val) == 1 and val[0] == "str", \
            f"{list_field} schema must be [str], got: {val}"

    # PNG signatures (stdlib only)
    for path in (ICON_PATH, LOGO_PATH):
        with open(path, "rb") as f:
            sig = f.read(8)
        assert sig == PNG_SIGNATURE, \
            f"{path.name} does not have PNG signature; got: {sig!r}"


def test_schema_translation_parity():
    with open(EN_PATH) as f:
        en_cfg = yaml.safe_load(f)["configuration"]
    with open(PL_PATH) as f:
        pl_cfg = yaml.safe_load(f)["configuration"]

    en_keys = set(en_cfg.keys())
    pl_keys = set(pl_cfg.keys())

    # Every schema field must appear in both translation files
    missing_en = SCHEMA_FIELD_NAMES - en_keys
    assert not missing_en, f"EN translations missing fields: {missing_en}"

    missing_pl = SCHEMA_FIELD_NAMES - pl_keys
    assert not missing_pl, f"PL translations missing fields: {missing_pl}"

    # Each field must have both name and description
    for field in SCHEMA_FIELD_NAMES:
        en_entry = en_cfg[field]
        assert "name" in en_entry, f"EN missing 'name' for {field}"
        assert "description" in en_entry, f"EN missing 'description' for {field}"

        pl_entry = pl_cfg[field]
        assert "name" in pl_entry, f"PL missing 'name' for {field}"
        assert "description" in pl_entry, f"PL missing 'description' for {field}"

    # No translation should define a field absent from the schema (drift guard)
    extra_en = en_keys - SCHEMA_FIELD_NAMES
    assert not extra_en, f"EN translations define fields not in schema: {extra_en}"

    extra_pl = pl_keys - SCHEMA_FIELD_NAMES
    assert not extra_pl, f"PL translations define fields not in schema: {extra_pl}"
