using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Argus.Orchestrator.Config;

/// <summary>
/// Server-side input validator for the POST /api/sensors/save handler.
///
/// Enforces every rule in 04-UI-SPEC Validation Rules before any write reaches disk.
/// This is the authoritative security boundary — a tampered or malformed POST body must
/// never reach ConfigWriter or the live pipeline.
///
/// T-04-01: entity_id regex rejects malformed ids before write.
/// T-04-02: KnownDetectors allowlist rejects unknown detector names before write.
/// T-04-03: Per-type numeric range checks reject out-of-range params before write.
/// T-04-04: WebUtility.HtmlEncode on all user-submitted strings in error messages.
/// </summary>
public static class InputValidator
{
    // entity_id must match ^[a-z0-9_]+\.[a-z0-9_]+$ (T-04-01)
    private static readonly Regex EntityIdRegex =
        new(@"^[a-z0-9_]+\.[a-z0-9_]+$", RegexOptions.Compiled);

    // Allowlist of valid detector names (T-04-02); comparison is always .ToLowerInvariant()
    private static readonly string[] KnownDetectors = { "rmad", "hst", "mad", "stl" };

    // Parity constants — orchestrator/ui/src/validation/detectorParams.ts carries the SAME
    // four strings verbatim. A client/server drift here shows up as a form that saves a value
    // the server then rejects with no field highlighted.
    internal const string MSG_WINDOW_RANGE = "Must be a whole number between 30 and 10000.";
    internal const string MSG_MIN_SAMPLES = "Must be a whole number ≥ 10.";
    internal const string MSG_MIN_SAMPLES_LE_WINDOW = "Must not be greater than window.";
    internal const string MSG_RMAD_LEGACY_N_TREES =
        "Parameter \"n_trees\" belongs to HST, not RMAD — this block was not migrated.";

    /// <summary>
    /// Validates entity IDs and detector parameters parsed from an untrusted POST body.
    ///
    /// Validates the raw parsedDetectors output — NOT the defaulted list (after empty→HST
    /// defaulting). Must be called BEFORE any entity-list build or ConfigWriter.WriteAsync.
    /// </summary>
    /// <param name="resolvedIds">Resolved entity IDs from the form submission.</param>
    /// <param name="parsedDetectors">Parsed detector configs keyed by entity index.</param>
    /// <returns>
    /// Empty list on success; one or more error strings (with HTML-encoded user values)
    /// on failure.
    /// </returns>
    public static List<string> Validate(
        IEnumerable<string> resolvedIds,
        Dictionary<int, List<DetectorConfig>> parsedDetectors)
    {
        var errors = new List<string>();

        // Validate entity IDs
        foreach (var id in resolvedIds)
        {
            if (!EntityIdRegex.IsMatch(id))
            {
                errors.Add(
                    $"Invalid entity ID '{WebUtility.HtmlEncode(id)}'. " +
                    "Use format domain.object_id (e.g. sensor.living_room_temp).");
            }
        }

        // Validate detector names and params
        foreach (var (_, detectors) in parsedDetectors)
        {
            foreach (var det in detectors)
            {
                var name = det.Name?.ToLowerInvariant() ?? "";

                if (!KnownDetectors.Contains(name))
                {
                    // T-04-04: HTML-encode the submitted detector name before interpolation
                    errors.Add(
                        $"Unknown detector type \"{WebUtility.HtmlEncode(det.Name)}\". " +
                        "Choose RMAD, HST, MAD, or STL.");
                    continue; // skip param validation for unknown detector type
                }

                // Validate the EFFECTIVE params — the submitted keys layered over the default
                // table — not the raw submitted map. See WithDefaults.
                var effective = WithDefaults(name, det.Params);

                switch (name)
                {
                    case "rmad":
                        ValidateRmad(effective, errors);
                        break;
                    case "hst":
                        ValidateHst(effective, errors);
                        break;
                    case "mad":
                        ValidateMad(effective, errors);
                        break;
                    case "stl":
                        ValidateStl(effective, errors);
                        break;
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Layers the submitted params over the detector's default table, so validation sees the
    /// configuration that will ACTUALLY be in force rather than the literal keys of the body.
    ///
    /// WHY: an absent key already means "use the default" everywhere else in the system —
    /// <c>RmadParams.From</c>/<c>HstParams.From</c> read every field with a fallback, and
    /// <c>params: {}</c> is what gen-entities.py writes for every entity on a fresh install and
    /// what the save path itself writes for an entity it defaulted. Treating an absent key as a
    /// hard error made the validator disagree with the loader about the same file: a brand-new
    /// install could not be saved at all, and the only way to make it savable was to materialize
    /// the whole default table onto disk, which permanently pins those entities to today's
    /// numbers — a later change to DetectorDefaults would never reach them.
    ///
    /// What this does NOT relax: a key that IS present must be numeric and in range. A blank or
    /// non-numeric value is still a hard error (CR-01, client parity with detectorParams.ts's
    /// MSG_REQUIRED), because that is a field the operator was looking at and cleared, not a
    /// field they never touched. Cross-field rules (high &gt; low, min_samples ≤ window) are
    /// evaluated on the merged map too, so a partial block cannot smuggle in an unreachable
    /// combination by omitting one half of the pair.
    ///
    /// The default table is the same one GET /api/detectors/defaults serves, and
    /// DetectorDefaultsTests pins it against the <c>*Params.From</c> fallbacks — so "valid by
    /// default" cannot drift into "the default fails its own validator".
    /// </summary>
    private static Dictionary<string, string> WithDefaults(string name, Dictionary<string, string> submitted)
    {
        // Get() hands back a fresh dictionary per call, so this never mutates the shared table.
        var effective = Web.DetectorDefaults.Get(name);
        if (effective is null) return submitted;

        foreach (var (key, value) in submitted)
            effective[key] = value;

        return effective;
    }

    // -------------------------------------------------------------------------
    // Per-type validators
    // -------------------------------------------------------------------------

    private static void ValidateHst(Dictionary<string, string> p, List<string> errors)
    {
        // integer ≥ 1 params
        ValidateIntAtLeast(p, "window",          1, "Must be a whole number ≥ 1.", errors);
        ValidateIntAtLeast(p, "n_trees",         1, "Must be a whole number ≥ 1.", errors);
        ValidateIntAtLeast(p, "min_consecutive", 1, "Must be a whole number ≥ 1.", errors);
        ValidateIntAtLeast(p, "frozen_window",   1, "Must be a whole number ≥ 1.", errors);

        // high_threshold: number in (0, 1] — but cross-field check requires > low_threshold
        // The cross-field check (below) also covers the "greater than low_threshold" rule.
        // Independent range check: must be in (0, 1] — i.e. > 0 AND ≤ 1
        // Blank/non-numeric is a hard error (client parity — detectorParams.ts MSG_REQUIRED);
        // an omitted key never reaches here, WithDefaults already replaced it with the default.
        var hasHigh = TryGetDouble(p, "high_threshold", out var high);
        if (!hasHigh || high <= 0.0 || high > 1.0)
            errors.Add("Must be between 0 and 1, and greater than low threshold.");

        // low_threshold: number in [0, 1) — i.e. ≥ 0 AND < 1
        var hasLow = TryGetDouble(p, "low_threshold", out var low);
        if (!hasLow || low < 0.0 || low >= 1.0)
            errors.Add("Must be between 0 and 1, and less than high threshold.");

        // Cross-field: high must be strictly > low. Only evaluated when both values
        // individually parsed and passed their own range check (mirrors detectorParams.ts:
        // "only applies when both individually pass their own range check").
        if (hasHigh && hasLow &&
            high > 0.0 && high <= 1.0 && low >= 0.0 && low < 1.0 &&
            high <= low)
        {
            errors.Add("Must be between 0 and 1, and greater than low threshold.");
            errors.Add("Must be between 0 and 1, and less than high threshold.");
        }

        // frozen_variance_threshold: number ≥ 0
        if (!TryGetDouble(p, "frozen_variance_threshold", out var fvt) || fvt < 0.0)
            errors.Add("Must be 0 or greater.");

        ValidateAlertKeys(p, errors);
    }

    /// <summary>
    /// Validates the rmad params set (D-A/D-B).
    ///
    /// Called with the params ALREADY layered over the default table (see WithDefaults), so
    /// every key is present here by construction: an absent key was replaced by its default,
    /// and a present one is checked exactly as submitted. Blank/non-numeric therefore still
    /// fails — only "the operator never touched this field" passes.
    ///
    /// frozen_window keeps the >= 1 rule of the hst path unchanged: frozen is disabled through
    /// frozen_variance_threshold (D-H), never through the window, because
    /// FrozenSensorDetector.AddReading dequeues from an empty queue when the window is 0.
    /// </summary>
    private static void ValidateRmad(Dictionary<string, string> p, List<string> errors)
    {
        // An rmad block carrying an HST-ONLY key is a legacy block wearing the new name — a
        // params set the migration never rewrote. It has to be rejected explicitly, because
        // every key it does share with rmad (window 250, high 0.7, low 0.3, frozen 10/0.001)
        // is individually in range: accepted, it would mean "alarm above robust z 11.7", i.e.
        // an entity that silently never alarms. Until this fix the legacy fingerprint was
        // rejected only as a side effect of min_samples/z_scale/scale_floor being absent, and
        // absence is no longer an error (see WithDefaults). The editor cannot MINT such a block
        // — it replaces the whole params map when the detector name changes — but since D-N the
        // read-back path hydrates the form straight off disk, so a hand-edited entities.yaml
        // reaches the browser intact. validateRmadParams in detectorParams.ts therefore carries
        // the same rule and the same message, or the operator gets "valid" in the form and a
        // rejected Save with no field to point at.
        if (p.ContainsKey("n_trees"))
            errors.Add(MSG_RMAD_LEGACY_N_TREES);

        // window: 30..10000. The lower bound is not cosmetic — a median/MAD baseline under
        // ~30 samples has a scale estimate too noisy to divide by, so the score stops meaning
        // "deviation" and starts meaning "the last few readings disagreed".
        var windowOk = ValidateIntInRange(p, "window", 30, 10000, MSG_WINDOW_RANGE, errors, out var window);

        var minSamplesOk = ValidateIntAtLeast(p, "min_samples", 10, MSG_MIN_SAMPLES, errors, out var minSamples);

        // Cross-field: a min_samples above the window it is counted against can never be
        // reached, so the entity would report "calibrating" forever and never alarm.
        // Reported only when BOTH fields are individually valid, so one bad key produces one
        // message instead of two.
        if (windowOk && minSamplesOk && minSamples > window)
            errors.Add(MSG_MIN_SAMPLES_LE_WINDOW);

        if (!TryGetDouble(p, "z_scale", out var zScale) || zScale <= 0.0)
            errors.Add("Must be greater than 0.");

        if (!TryGetDouble(p, "scale_floor", out var scaleFloor) || scaleFloor < 0.0)
            errors.Add("Must be 0 or greater.");

        ValidateIntAtLeast(p, "min_consecutive", 1, "Must be a whole number ≥ 1.", errors);
        ValidateIntAtLeast(p, "frozen_window",   1, "Must be a whole number ≥ 1.", errors);

        // high/low: identical rules and identical messages to ValidateHst — the keys keep
        // their types and ranges, only the score they are compared against changed (D-B).
        var hasHigh = TryGetDouble(p, "high_threshold", out var high);
        if (!hasHigh || high <= 0.0 || high > 1.0)
            errors.Add("Must be between 0 and 1, and greater than low threshold.");

        var hasLow = TryGetDouble(p, "low_threshold", out var low);
        if (!hasLow || low < 0.0 || low >= 1.0)
            errors.Add("Must be between 0 and 1, and less than high threshold.");

        if (hasHigh && hasLow &&
            high > 0.0 && high <= 1.0 && low >= 0.0 && low < 1.0 &&
            high <= low)
        {
            errors.Add("Must be between 0 and 1, and greater than low threshold.");
            errors.Add("Must be between 0 and 1, and less than high threshold.");
        }

        if (!TryGetDouble(p, "frozen_variance_threshold", out var fvt) || fvt < 0.0)
            errors.Add("Must be 0 or greater.");

        ValidateAlertKeys(p, errors);
    }

    /// <summary>
    /// Validates the WS2 alert-layer keys, which share the HST params map.
    ///
    /// Every key is checked ONLY when present. WHY: the SPA never sends these keys, so treating
    /// a missing key as an error (the rule the HST keys above follow) would make every Save from
    /// every screen fail validation. Absent means "use the default", which is always in range.
    /// </summary>
    private static void ValidateAlertKeys(Dictionary<string, string> p, List<string> errors)
    {
        if (p.TryGetValue("alert_mode", out var mode) &&
            mode?.Trim().ToLowerInvariant() is not ("adaptive" or "legacy"))
            errors.Add("Must be \"adaptive\" or \"legacy\".");

        if (p.TryGetValue("evidence_mode", out var evidence) &&
            evidence?.Trim().ToLowerInvariant() is not ("any" or "both" or "score_only" or "raw_only"))
            errors.Add("Must be \"any\", \"both\", \"score_only\" or \"raw_only\".");

        // rank_window ≥ 50: below 50 samples a mid-rank cannot reach 0.99 at all, so a smaller
        // window silently disables the score channel instead of tightening it.
        ValidateOptionalIntAtLeast(p, "rank_window", 50, "Must be a whole number ≥ 50.", errors);
        ValidateOptionalIntAtLeast(p, "raw_window", 10, "Must be a whole number ≥ 10.", errors);
        ValidateOptionalIntAtLeast(p, "alert_min_samples", 50, "Must be a whole number ≥ 50.", errors);
        ValidateOptionalIntAtLeast(p, "min_duration_sec", 0, "Must be a whole number ≥ 0.", errors);
        ValidateOptionalIntAtLeast(p, "refractory_sec", 0, "Must be a whole number ≥ 0.", errors);
        ValidateOptionalIntAtLeast(p, "storm_hold_sec", 0, "Must be a whole number ≥ 0.", errors);
        ValidateOptionalIntAtLeast(p, "max_events_per_hour", 1, "Must be a whole number ≥ 1.", errors);
        // max_event_duration_sec ≥ 60: the watchdog is the last line against F1; a sub-minute
        // value would chop every real event instead of catching a stuck one.
        ValidateOptionalIntAtLeast(p, "max_event_duration_sec", 60, "Must be a whole number ≥ 60.", errors);

        bool hasQFire = TryGetOptionalDouble(p, "q_fire", out var qFire, out var qFireOk);
        if (hasQFire && (!qFireOk || qFire <= 0.0 || qFire >= 1.0))
            errors.Add("Must be between 0 and 1, and greater than clear quantile.");

        bool hasQClear = TryGetOptionalDouble(p, "q_clear", out var qClear, out var qClearOk);
        if (hasQClear && (!qClearOk || qClear < 0.0 || qClear >= 1.0))
            errors.Add("Must be between 0 and 1, and less than fire quantile.");

        bool hasZFire = TryGetOptionalDouble(p, "z_fire", out var zFire, out var zFireOk);
        if (hasZFire && (!zFireOk || zFire <= 0.0))
            errors.Add("Must be greater than 0, and greater than clear z.");

        bool hasZClear = TryGetOptionalDouble(p, "z_clear", out var zClear, out var zClearOk);
        if (hasZClear && (!zClearOk || zClear < 0.0))
            errors.Add("Must be 0 or greater, and less than fire z.");

        // Cross-field checks, same shape as the high/low threshold pair above: only evaluated
        // when both values individually parsed and passed their own range check. Inverted
        // thresholds are the one misconfiguration that looks valid and never alarms.
        if (hasQFire && hasQClear && qFireOk && qClearOk &&
            qFire > 0.0 && qFire < 1.0 && qClear >= 0.0 && qClear < 1.0 && qFire <= qClear)
        {
            errors.Add("Must be between 0 and 1, and greater than clear quantile.");
            errors.Add("Must be between 0 and 1, and less than fire quantile.");
        }

        if (hasZFire && hasZClear && zFireOk && zClearOk &&
            zFire > 0.0 && zClear >= 0.0 && zFire <= zClear)
        {
            errors.Add("Must be greater than 0, and greater than clear z.");
            errors.Add("Must be 0 or greater, and less than fire z.");
        }

        // alert_min_samples ≤ rank_window: a target above the window it is measured against
        // can never be met, so the entity would stay "calibrating" forever.
        if (TryGetInt(p, "alert_min_samples", out var ams) &&
            TryGetInt(p, "rank_window", out var rw) && ams > rw)
            errors.Add("Must be a whole number ≥ 50 and no greater than rank window.");
    }

    /// <summary>
    /// Range-checks an integer param only when the key is present; a present-but-unparsable or
    /// out-of-range value is an error, an absent key is not.
    /// </summary>
    private static void ValidateOptionalIntAtLeast(
        Dictionary<string, string> p,
        string key,
        int minValue,
        string errorMsg,
        List<string> errors)
    {
        if (!p.ContainsKey(key))
            return;
        if (!TryGetInt(p, key, out var val) || val < minValue)
            errors.Add(errorMsg);
    }

    /// <summary>
    /// Reads an optional double. Returns whether the key was present; <paramref name="parsed"/>
    /// reports whether the present value was numeric (InvariantCulture).
    /// </summary>
    private static bool TryGetOptionalDouble(
        Dictionary<string, string> p, string key, out double val, out bool parsed)
    {
        val = 0;
        parsed = false;
        if (!p.ContainsKey(key))
            return false;
        parsed = TryGetDouble(p, key, out val);
        return true;
    }

    private static void ValidateMad(Dictionary<string, string> p, List<string> errors)
    {
        // threshold: number > 0
        if (!TryGetDouble(p, "threshold", out var threshold) || threshold <= 0.0)
            errors.Add("Must be greater than 0.");

        // window: integer ≥ 1
        ValidateIntAtLeast(p, "window", 1, "Must be a whole number ≥ 1.", errors);
    }

    private static void ValidateStl(Dictionary<string, string> p, List<string> errors)
    {
        // period: integer ≥ 2
        ValidateIntAtLeast(p, "period", 2, "Must be a whole number ≥ 2.", errors);

        // seasonal: integer ≥ 1 (T-04-03: SC1 — seasonal must be validated before write)
        ValidateIntAtLeast(p, "seasonal", 1, "Must be a whole number ≥ 1.", errors);

        // threshold: number > 0
        if (!TryGetDouble(p, "threshold", out var threshold) || threshold <= 0.0)
            errors.Add("Must be greater than 0.");
    }

    // -------------------------------------------------------------------------
    // Parse helpers — project-standard pattern (from EntitiesConfig.cs HstParams.From)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Tries to get a double value from the params dictionary using the project-standard
    /// InvariantCulture pattern (locale-independent).
    /// </summary>
    private static bool TryGetDouble(Dictionary<string, string> p, string key, out double val)
    {
        val = 0;
        return p.TryGetValue(key, out var v) &&
               double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out val);
    }

    /// <summary>
    /// Tries to get an int value from the params dictionary.
    /// </summary>
    private static bool TryGetInt(Dictionary<string, string> p, string key, out int val)
    {
        val = 0;
        return p.TryGetValue(key, out var v) && int.TryParse(v, out val);
    }

    /// <summary>
    /// Validates that an integer param is present, numeric, and ≥ minValue; appends errorMsg
    /// on failure. Blank/non-numeric values are a hard error (client parity —
    /// detectorParams.ts MSG_REQUIRED), not a silent skip. Callers pass the default-layered
    /// map (WithDefaults), so "absent" is not a case that reaches this helper.
    /// </summary>
    private static void ValidateIntAtLeast(
        Dictionary<string, string> p,
        string key,
        int minValue,
        string errorMsg,
        List<string> errors)
        => ValidateIntAtLeast(p, key, minValue, errorMsg, errors, out _);

    /// <summary>
    /// <see cref="ValidateIntAtLeast(Dictionary{string,string},string,int,string,List{string})"/>
    /// that also hands back the parsed value and whether it passed, so a caller can gate a
    /// cross-field rule on it without re-parsing and without re-stating the bound.
    /// </summary>
    private static bool ValidateIntAtLeast(
        Dictionary<string, string> p,
        string key,
        int minValue,
        string errorMsg,
        List<string> errors,
        out int val)
        => ValidateIntInRange(p, key, minValue, int.MaxValue, errorMsg, errors, out val);

    /// <summary>
    /// Validates that an integer param is present, numeric, and within [minValue, maxValue];
    /// appends errorMsg on failure and returns whether it passed. Both bounds live in ONE
    /// place per field: a range spelled out inline at the call site is how the client and the
    /// server drift apart (detectorParams.ts MSG_WINDOW_RANGE is the mirror of this).
    /// </summary>
    private static bool ValidateIntInRange(
        Dictionary<string, string> p,
        string key,
        int minValue,
        int maxValue,
        string errorMsg,
        List<string> errors,
        out int val)
    {
        var ok = TryGetInt(p, key, out val) && val >= minValue && val <= maxValue;
        if (!ok)
            errors.Add(errorMsg);
        return ok;
    }
}
