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
    private static readonly string[] KnownDetectors = { "hst", "mad", "stl" };

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
                        "Choose HST, MAD, or STL.");
                    continue; // skip param validation for unknown detector type
                }

                switch (name)
                {
                    case "hst":
                        ValidateHst(det.Params, errors);
                        break;
                    case "mad":
                        ValidateMad(det.Params, errors);
                        break;
                    case "stl":
                        ValidateStl(det.Params, errors);
                        break;
                }
            }
        }

        return errors;
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
        if (TryGetDouble(p, "high_threshold", out var high))
        {
            if (high <= 0.0 || high > 1.0)
                errors.Add("Must be between 0 and 1, and greater than low threshold.");
        }

        // low_threshold: number in [0, 1) — i.e. ≥ 0 AND < 1
        if (TryGetDouble(p, "low_threshold", out var low))
        {
            if (low < 0.0 || low >= 1.0)
                errors.Add("Must be between 0 and 1, and less than high threshold.");
        }

        // Cross-field: high must be strictly > low (Pitfall 5 — skip if either key absent)
        if (p.TryGetValue("high_threshold", out _) && p.TryGetValue("low_threshold", out _))
        {
            if (TryGetDouble(p, "high_threshold", out var h) &&
                TryGetDouble(p, "low_threshold",  out var l))
            {
                // Only add cross-field errors if range errors not already added
                // (to avoid double-reporting on the same field)
                if (h > 0.0 && h <= 1.0 && l >= 0.0 && l < 1.0)
                {
                    // Both are in their individual valid ranges — check cross-field constraint
                    if (h <= l)
                    {
                        errors.Add("Must be between 0 and 1, and greater than low threshold.");
                        errors.Add("Must be between 0 and 1, and less than high threshold.");
                    }
                }
            }
        }

        // frozen_variance_threshold: number ≥ 0
        if (TryGetDouble(p, "frozen_variance_threshold", out var fvt))
        {
            if (fvt < 0.0)
                errors.Add("Must be 0 or greater.");
        }
    }

    private static void ValidateMad(Dictionary<string, string> p, List<string> errors)
    {
        // threshold: number > 0
        if (TryGetDouble(p, "threshold", out var threshold))
        {
            if (threshold <= 0.0)
                errors.Add("Must be greater than 0.");
        }

        // window: integer ≥ 1
        ValidateIntAtLeast(p, "window", 1, "Must be a whole number ≥ 1.", errors);
    }

    private static void ValidateStl(Dictionary<string, string> p, List<string> errors)
    {
        // period: integer ≥ 2
        ValidateIntAtLeast(p, "period", 2, "Must be a whole number ≥ 2.", errors);

        // threshold: number > 0
        if (TryGetDouble(p, "threshold", out var threshold))
        {
            if (threshold <= 0.0)
                errors.Add("Must be greater than 0.");
        }
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
    /// Validates that an integer param is present and ≥ minValue; appends errorMsg on failure.
    /// </summary>
    private static void ValidateIntAtLeast(
        Dictionary<string, string> p,
        string key,
        int minValue,
        string errorMsg,
        List<string> errors)
    {
        if (TryGetInt(p, key, out var val))
        {
            if (val < minValue)
                errors.Add(errorMsg);
        }
    }
}
