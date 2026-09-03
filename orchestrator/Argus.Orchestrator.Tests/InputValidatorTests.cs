using Argus.Orchestrator.Config;
using Xunit;

namespace Argus.Orchestrator.Tests;

/// <summary>
/// Unit tests for InputValidator — covers every rule in the 04-UI-SPEC Validation Rules.
/// All tests follow the MethodName_Scenario_ExpectedOutcome naming convention.
/// </summary>
public class InputValidatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Dictionary<int, List<DetectorConfig>> OneHstDetector(
        Dictionary<string, string>? overrides = null)
    {
        var p = new Dictionary<string, string>
        {
            ["window"]                     = "250",
            ["n_trees"]                    = "25",
            ["high_threshold"]             = "0.7",
            ["low_threshold"]              = "0.3",
            ["min_consecutive"]            = "3",
            ["frozen_window"]              = "10",
            ["frozen_variance_threshold"]  = "0.001",
        };
        if (overrides is not null)
            foreach (var (k, v) in overrides) p[k] = v;

        return new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = p }],
        };
    }

    private static Dictionary<int, List<DetectorConfig>> OneMadDetector(
        Dictionary<string, string>? overrides = null)
    {
        var p = new Dictionary<string, string>
        {
            ["threshold"] = "3.5",
            ["window"]    = "20",
        };
        if (overrides is not null)
            foreach (var (k, v) in overrides) p[k] = v;

        return new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "mad", Params = p }],
        };
    }

    private static Dictionary<int, List<DetectorConfig>> OneStlDetector(
        Dictionary<string, string>? overrides = null)
    {
        var p = new Dictionary<string, string>
        {
            ["period"]    = "24",
            ["seasonal"]  = "7",
            ["threshold"] = "3.0",
        };
        if (overrides is not null)
            foreach (var (k, v) in overrides) p[k] = v;

        return new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "stl", Params = p }],
        };
    }

    // -------------------------------------------------------------------------
    // entity_id validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_ValidEntityIdAndHstParams_ReturnsNoErrors()
    {
        var ids = new[] { "sensor.salon_temperatura" };
        var errors = InputValidator.Validate(ids, OneHstDetector());
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("sensor.UPPER")]   // uppercase fails regex
    [InlineData("sensor")]         // missing dot-component
    [InlineData("sensor.bad id")]  // space character
    [InlineData("Sensor.x")]       // uppercase domain
    public void Validate_InvalidEntityId_ReturnsError(string badId)
    {
        var errors = InputValidator.Validate([badId], []);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_ValidEntityId_ReturnsNoErrors()
    {
        var errors = InputValidator.Validate(["sensor.salon_temperatura"], []);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_EntityIdError_ContainsHtmlEncodedId()
    {
        // entity_id with <script> to test HTML encoding
        var badId = "<script>alert(1)</script>.sensor";
        var errors = InputValidator.Validate([badId], []);
        Assert.NotEmpty(errors);
        // must NOT contain raw angle brackets in error output
        Assert.DoesNotContain("<script>", errors[0]);
        Assert.Contains("&lt;script&gt;", errors[0]);
    }

    // -------------------------------------------------------------------------
    // HST parameter validation
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("window", "0")]
    [InlineData("n_trees", "0")]
    [InlineData("min_consecutive", "0")]
    [InlineData("frozen_window", "0")]
    public void Validate_HstIntegerParamAtZero_ReturnsError(string paramKey, string value)
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { [paramKey] = value }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_HstHighThresholdOutOfRange_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { ["high_threshold"] = "1.5" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_HstLowThresholdNegative_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { ["low_threshold"] = "-0.1" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_HstHighThresholdLessThanLowThreshold_ReturnsError()
    {
        // high=0.3, low=0.7 → cross-field violation
        var errors = InputValidator.Validate([], OneHstDetector(new()
        {
            ["high_threshold"] = "0.3",
            ["low_threshold"]  = "0.7",
        }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_HstHighThresholdEqualToLowThreshold_ReturnsError()
    {
        // high must be strictly > low
        var errors = InputValidator.Validate([], OneHstDetector(new()
        {
            ["high_threshold"] = "0.5",
            ["low_threshold"]  = "0.5",
        }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_HstOnlyHighThresholdPresent_DoesNotThrow()
    {
        // Pitfall 5: cross-field check skipped when either key is absent
        var p = new Dictionary<string, string>
        {
            ["window"]                    = "250",
            ["n_trees"]                   = "25",
            ["high_threshold"]            = "0.7",
            // low_threshold intentionally absent
            ["min_consecutive"]           = "3",
            ["frozen_window"]             = "10",
            ["frozen_variance_threshold"] = "0.001",
        };
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = p }],
        };

        var exception = Record.Exception(() => InputValidator.Validate([], detectors));
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_HstOnlyLowThresholdPresent_DoesNotThrow()
    {
        // Pitfall 5: cross-field check skipped when either key is absent
        var p = new Dictionary<string, string>
        {
            ["window"]                    = "250",
            ["n_trees"]                   = "25",
            // high_threshold intentionally absent
            ["low_threshold"]             = "0.3",
            ["min_consecutive"]           = "3",
            ["frozen_window"]             = "10",
            ["frozen_variance_threshold"] = "0.001",
        };
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = p }],
        };

        var exception = Record.Exception(() => InputValidator.Validate([], detectors));
        Assert.Null(exception);
    }

    [Fact]
    public void Validate_HstFrozenVarianceThresholdNegative_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { ["frozen_variance_threshold"] = "-0.001" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_HstFrozenVarianceThresholdZero_ReturnsNoError()
    {
        // zero is valid (≥ 0)
        var errors = InputValidator.Validate([], OneHstDetector(new() { ["frozen_variance_threshold"] = "0" }));
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // HST threshold range boundaries (in [0,1])
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_HstHighThresholdAtOne_ReturnsNoError()
    {
        // high_threshold=1.0 is in range, and with low=0.3 the cross-field is satisfied
        var errors = InputValidator.Validate([], OneHstDetector(new()
        {
            ["high_threshold"] = "1.0",
            ["low_threshold"]  = "0.3",
        }));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_HstHighThresholdAboveOne_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { ["high_threshold"] = "1.01" }));
        Assert.NotEmpty(errors);
    }

    // -------------------------------------------------------------------------
    // MAD parameter validation
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("threshold", "0")]
    [InlineData("threshold", "-1")]
    public void Validate_MadThresholdNotPositive_ReturnsError(string paramKey, string value)
    {
        var errors = InputValidator.Validate([], OneMadDetector(new() { [paramKey] = value }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MadWindowAtZero_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneMadDetector(new() { ["window"] = "0" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_MadValidParams_ReturnsNoErrors()
    {
        var errors = InputValidator.Validate([], OneMadDetector());
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // STL parameter validation
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_StlPeriodAtOne_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["period"] = "1" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_StlPeriodAtTwo_ReturnsNoError()
    {
        // period ≥ 2 is valid
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["period"] = "2" }));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_StlThresholdAtZero_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["threshold"] = "0" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_StlValidParams_ReturnsNoErrors()
    {
        var errors = InputValidator.Validate([], OneStlDetector());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_StlSeasonalAtZero_ReturnsError()
    {
        // seasonal must be ≥ 1 (T-04-03 / SC1)
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["seasonal"] = "0" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_StlSeasonalNegative_ReturnsError()
    {
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["seasonal"] = "-999" }));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_StlSeasonalAtOne_ReturnsNoError()
    {
        // seasonal = 1 is the minimum valid value
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["seasonal"] = "1" }));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_StlSeasonalNonNumeric_ReturnsError()
    {
        // Non-numeric seasonal must be a hard error (client parity — detectorParams.ts
        // MSG_REQUIRED via isBlankOrNonNumeric), not a silent skip.
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["seasonal"] = "abc" }));
        Assert.NotEmpty(errors);
    }

    // -------------------------------------------------------------------------
    // Unknown detector name
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("xgboost")]
    [InlineData("")]
    public void Validate_UnknownDetectorName_ReturnsError(string detName)
    {
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = detName, Params = [] }],
        };
        var errors = InputValidator.Validate([], detectors);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_UnknownDetectorError_ContainsHtmlEncodedName()
    {
        var detName = "<xgboost>";
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = detName, Params = [] }],
        };
        var errors = InputValidator.Validate([], detectors);
        Assert.NotEmpty(errors);
        Assert.DoesNotContain("<xgboost>", errors[0]);
        Assert.Contains("&lt;xgboost&gt;", errors[0]);
    }

    [Fact]
    public void Validate_UnknownDetector_SkipsParamValidation()
    {
        // Unknown detector should report exactly one error (the name), not additional param errors
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig
            {
                Name = "xgboost",
                Params = new() { ["window"] = "-999" }, // invalid if validated, but should be skipped
            }],
        };
        var errors = InputValidator.Validate([], detectors);
        Assert.Single(errors); // only the "unknown detector" error
    }

    // -------------------------------------------------------------------------
    // Case-insensitive detector names
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("HST")]
    [InlineData("Mad")]
    [InlineData("STL")]
    public void Validate_DetectorNameMixedCase_Accepted(string name)
    {
        Dictionary<int, List<DetectorConfig>> detectors = name.ToLowerInvariant() switch
        {
            "hst" => OneHstDetector(null),
            "mad" => OneMadDetector(null),
            "stl" => OneStlDetector(null),
            _ => throw new InvalidOperationException("unreachable"),
        };
        // Override the Name with the mixed-case version
        detectors[0][0] = new DetectorConfig
        {
            Name = name,
            Params = detectors[0][0].Params,
        };

        var errors = InputValidator.Validate([], detectors);
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // Locale independence (InvariantCulture)
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_HstThresholdWithDotDecimalSeparator_ParsesCorrectly()
    {
        // "0.7" must parse correctly regardless of machine culture
        var errors = InputValidator.Validate(["sensor.test"], OneHstDetector(new()
        {
            ["high_threshold"] = "0.7",
            ["low_threshold"]  = "0.3",
        }));
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // Empty inputs (no errors expected for empty collections)
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_EmptyIdsAndNoDetectors_ReturnsNoErrors()
    {
        var errors = InputValidator.Validate([], []);
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // CR-01 regression: missing/blank/non-numeric detector params must be a hard
    // error (client parity — detectorParams.ts isBlankOrNonNumeric -> MSG_REQUIRED),
    // never a silent skip that lets malformed values reach ConfigWriter.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("window")]
    [InlineData("n_trees")]
    [InlineData("high_threshold")]
    [InlineData("low_threshold")]
    [InlineData("min_consecutive")]
    [InlineData("frozen_window")]
    [InlineData("frozen_variance_threshold")]
    public void Validate_HstParamEmptyString_ReturnsError(string paramKey)
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { [paramKey] = "" }));
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("n_trees")]
    [InlineData("high_threshold")]
    [InlineData("low_threshold")]
    [InlineData("min_consecutive")]
    [InlineData("frozen_window")]
    [InlineData("frozen_variance_threshold")]
    public void Validate_HstParamNonNumeric_ReturnsError(string paramKey)
    {
        var errors = InputValidator.Validate([], OneHstDetector(new() { [paramKey] = "abc" }));
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("window")]
    [InlineData("n_trees")]
    [InlineData("high_threshold")]
    [InlineData("low_threshold")]
    [InlineData("min_consecutive")]
    [InlineData("frozen_window")]
    [InlineData("frozen_variance_threshold")]
    public void Validate_HstParamMissingKey_ReturnsError(string paramKey)
    {
        var p = new Dictionary<string, string>
        {
            ["window"]                    = "250",
            ["n_trees"]                   = "25",
            ["high_threshold"]            = "0.7",
            ["low_threshold"]             = "0.3",
            ["min_consecutive"]           = "3",
            ["frozen_window"]             = "10",
            ["frozen_variance_threshold"] = "0.001",
        };
        p.Remove(paramKey);
        var detectors = new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "hst", Params = p }],
        };

        var errors = InputValidator.Validate([], detectors);
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("threshold")]
    [InlineData("window")]
    public void Validate_MadParamEmptyOrNonNumeric_ReturnsError(string paramKey)
    {
        var emptyErrors = InputValidator.Validate([], OneMadDetector(new() { [paramKey] = "" }));
        Assert.NotEmpty(emptyErrors);

        var nonNumericErrors = InputValidator.Validate([], OneMadDetector(new() { [paramKey] = "xyz" }));
        Assert.NotEmpty(nonNumericErrors);
    }

    [Theory]
    [InlineData("period")]
    [InlineData("seasonal")]
    [InlineData("threshold")]
    public void Validate_StlParamEmptyOrNonNumeric_ReturnsError(string paramKey)
    {
        var emptyErrors = InputValidator.Validate([], OneStlDetector(new() { [paramKey] = "" }));
        Assert.NotEmpty(emptyErrors);

        var nonNumericErrors = InputValidator.Validate([], OneStlDetector(new() { [paramKey] = "xyz" }));
        Assert.NotEmpty(nonNumericErrors);
    }

    [Fact]
    public void Validate_HstAllParamsValid_ReturnsNoErrors()
    {
        // Valid, in-range params must still pass (regression guard: fix must not
        // over-reject legitimate values).
        var errors = InputValidator.Validate([], OneHstDetector());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MadAllParamsValid_ReturnsNoErrors()
    {
        var errors = InputValidator.Validate([], OneMadDetector());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_StlAllParamsValid_ReturnsNoErrors()
    {
        var errors = InputValidator.Validate([], OneStlDetector());
        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // WS2 alert-layer keys (share the HST params map)
    // -------------------------------------------------------------------------

    [Fact]
    public void Validate_HstParamsWithNoAlertKeys_ReturnsNoErrors()
    {
        // The alert keys must be validated ONLY when present. The SPA never sends them
        // (sensors.ts posts the HST keys and nothing else), so treating a missing key the way
        // the HST keys are treated — missing is a hard error — would make every Save from every
        // screen, including the pattern textareas in Settings, fail validation.
        var errors = InputValidator.Validate([], OneHstDetector());
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AlertKeysPresentButInverted_ReturnsErrors()
    {
        // Inverted thresholds are the one misconfiguration that looks entirely plausible and
        // silently never alarms: with q_fire below q_clear a score can be "high enough to fire"
        // and "low enough to clear" at the same time, and the gate settles into never firing.
        // Same for z_fire below z_clear. Both are caught before the file is written.
        var errors = InputValidator.Validate([], OneHstDetector(new Dictionary<string, string>
        {
            ["q_fire"]  = "0.50",
            ["q_clear"] = "0.90",
            ["z_fire"]  = "2.0",
            ["z_clear"] = "6.0",
        }));

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("greater than clear quantile"));
        Assert.Contains(errors, e => e.Contains("less than fire quantile"));
        Assert.Contains(errors, e => e.Contains("greater than clear z"));
        Assert.Contains(errors, e => e.Contains("less than fire z"));
    }

    [Fact]
    public void Validate_AlertMinSamplesAboveRankWindow_ReturnsError()
    {
        // A calibration target larger than the window it is measured against can never be met,
        // so the entity reports "calibrating" for the life of the process and never alarms —
        // failure by silence, which is exactly what this workstream exists to remove.
        var errors = InputValidator.Validate([], OneHstDetector(new Dictionary<string, string>
        {
            ["rank_window"]       = "200",
            ["alert_min_samples"] = "500",
        }));

        Assert.Contains(errors, e => e.Contains("no greater than rank window"));
    }

    [Fact]
    public void Validate_RankWindowBelowFifty_ReturnsError()
    {
        // Arithmetic floor, not taste: with mid-ranks the largest attainable rank is
        // 1 - 0.5/Count, so q_fire = 0.99 is unreachable below 50 samples. A smaller window
        // would look like a tighter gate while actually disabling the score channel outright.
        var errors = InputValidator.Validate([], OneHstDetector(new Dictionary<string, string>
        {
            ["rank_window"] = "20",
        }));

        Assert.Contains(errors, e => e.Contains("≥ 50"));
    }

    [Fact]
    public void Validate_UnknownAlertModeOrEvidenceMode_ReturnsErrors()
    {
        var errors = InputValidator.Validate([], OneHstDetector(new Dictionary<string, string>
        {
            ["alert_mode"]    = "aggressive",
            ["evidence_mode"] = "either",
        }));

        Assert.Contains(errors, e => e.Contains("adaptive"));
        Assert.Contains(errors, e => e.Contains("score_only"));
    }

    [Fact]
    public void Validate_ValidAlertKeys_ReturnsNoErrors()
    {
        // Regression guard mirroring the HST case: a fully specified, in-range alert block must
        // pass, or a hand-edited entities.yaml could never be saved back from the UI.
        var errors = InputValidator.Validate([], OneHstDetector(new Dictionary<string, string>
        {
            ["alert_mode"]             = "adaptive",
            ["evidence_mode"]          = "any",
            ["rank_window"]            = "720",
            ["q_fire"]                 = "0.99",
            ["q_clear"]                = "0.80",
            ["raw_window"]             = "720",
            ["z_fire"]                 = "5.0",
            ["z_clear"]                = "3.0",
            ["alert_min_samples"]      = "240",
            ["min_duration_sec"]       = "120",
            ["refractory_sec"]         = "600",
            ["max_events_per_hour"]    = "4",
            ["max_event_duration_sec"] = "21600",
            ["storm_hold_sec"]         = "3600",
        }));

        Assert.Empty(errors);
    }

    // -------------------------------------------------------------------------
    // rmad (D-A/D-B) — the default streaming detector
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fixtures start from the authoritative default table and override ONLY the key under
    /// test. WHY it matters that the base is complete: every rmad key is required, so a fixture
    /// that omitted keys would produce errors unrelated to the rule being exercised and each
    /// test would pass for the wrong reason.
    /// </summary>
    private static Dictionary<int, List<DetectorConfig>> OneRmadDetector(
        Dictionary<string, string>? overrides = null)
    {
        var p = new Dictionary<string, string>(Argus.Orchestrator.Web.DetectorDefaults.Get("rmad")!);
        if (overrides is not null)
            foreach (var (k, v) in overrides) p[k] = v;

        return new Dictionary<int, List<DetectorConfig>>
        {
            [0] = [new DetectorConfig { Name = "rmad", Params = p }],
        };
    }

    [Fact]
    public void Validate_RmadDetectorName_IsAccepted()
    {
        // rmad is the default detector after migration; if the allowlist rejected it, the very
        // first Save from any screen would fail and the operator could not touch their config.
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector());
        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRmad_MinSamplesAboveWindow_ReturnsSingleCrossFieldError()
    {
        // A min_samples larger than the window it is counted against can never be reached, so
        // the entity would report "calibrating" forever and never alarm at all.
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector(
            new Dictionary<string, string> { ["min_samples"] = "720", ["window"] = "60" }));

        Assert.Single(errors);
        Assert.Equal(InputValidator.MSG_MIN_SAMPLES_LE_WINDOW, errors[0]);
    }

    [Theory]
    [InlineData("29")]
    [InlineData("10001")]
    public void ValidateRmad_WindowOutOfRange_ReturnsWindowRangeMessage(string window)
    {
        // Below ~30 samples a median/MAD scale estimate is too noisy to divide by — the score
        // stops meaning "deviation from normal" and starts meaning "recent readings disagreed".
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector(
            new Dictionary<string, string> { ["window"] = window, ["min_samples"] = "10" }));

        Assert.Contains(InputValidator.MSG_WINDOW_RANGE, errors);
    }

    [Theory]
    [InlineData("30", "10")]
    [InlineData("10000", "60")]
    public void ValidateRmad_WindowAtBounds_ReturnsNoErrors(string window, string minSamples)
    {
        // min_samples travels with the window here: at the 30 bound the default 60 would trip
        // the (separate) cross-field rule and mask whether the bound itself is inclusive.
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector(
            new Dictionary<string, string> { ["window"] = window, ["min_samples"] = minSamples }));

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRmad_MinSamplesBelowTen_ReturnsMinSamplesMessage()
    {
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector(
            new Dictionary<string, string> { ["min_samples"] = "9" }));

        Assert.Contains(InputValidator.MSG_MIN_SAMPLES, errors);
    }

    [Fact]
    public void ValidateRmad_MissingKey_IsAnError_NotADefault()
    {
        // Defaulting at the validation boundary would let an upstream key loss reach disk
        // looking deliberate — the operator would never learn a value was dropped.
        var p = new Dictionary<string, string>(Argus.Orchestrator.Web.DetectorDefaults.Get("rmad")!);
        p.Remove("z_scale");

        var errors = InputValidator.Validate(["sensor.load_5m"],
            new Dictionary<int, List<DetectorConfig>>
            {
                [0] = [new DetectorConfig { Name = "rmad", Params = p }],
            });

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidateRmad_ZeroFrozenWindow_IsRejected()
    {
        // D-H: frozen is disabled through frozen_variance_threshold, NEVER through the window.
        // FrozenSensorDetector.AddReading dequeues an empty queue when the window is 0, and
        // ScoreStreamPipeline calls it on every reading — the first reading would throw.
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector(
            new Dictionary<string, string> { ["frozen_window"] = "0" }));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidateRmad_ZeroFrozenVarianceThreshold_IsAccepted()
    {
        // The mirror of the rule above: 0.0 is the SUPPORTED way to disable frozen, so it must
        // validate. Sample variance is never negative, so "variance < 0.0" is permanently false.
        var errors = InputValidator.Validate(["sensor.load_5m"], OneRmadDetector(
            new Dictionary<string, string> { ["frozen_variance_threshold"] = "0.0" }));

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateRmad_LegacyHstParamSet_IsRejected()
    {
        // The exact legacy fingerprint (window 250 / n_trees 25 / 0.7 / 0.3 / frozen 10/0.001)
        // must NOT quietly validate as rmad: those thresholds mean "alarm above the 70th
        // percentile of an HST rarity mass" and would mean "alarm above robust z 11.7" here.
        // Rejecting it is what forces the migration to rewrite the whole block.
        var legacy = new Dictionary<string, string>
        {
            ["window"]                    = "250",
            ["n_trees"]                   = "25",
            ["high_threshold"]            = "0.7",
            ["low_threshold"]             = "0.3",
            ["min_consecutive"]           = "3",
            ["frozen_window"]             = "10",
            ["frozen_variance_threshold"] = "0.001",
        };

        var errors = InputValidator.Validate(["sensor.load_5m"],
            new Dictionary<int, List<DetectorConfig>>
            {
                [0] = [new DetectorConfig { Name = "rmad", Params = legacy }],
            });

        // min_samples, z_scale and scale_floor are all absent from the legacy set.
        Assert.Contains(InputValidator.MSG_MIN_SAMPLES, errors);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_UnknownDetector_MessageListsRmadFirst()
    {
        var errors = InputValidator.Validate([],
            new Dictionary<int, List<DetectorConfig>>
            {
                [0] = [new DetectorConfig { Name = "nope", Params = [] }],
            });

        Assert.Single(errors);
        Assert.Contains("Choose RMAD, HST, MAD, or STL.", errors[0]);
    }
}
