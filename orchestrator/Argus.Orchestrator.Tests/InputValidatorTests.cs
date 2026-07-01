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
    public void Validate_StlSeasonalNonNumeric_ReturnsNoError()
    {
        // Non-numeric seasonal: TryGetInt returns false → absent-key semantics, silently skipped
        var errors = InputValidator.Validate([], OneStlDetector(new() { ["seasonal"] = "abc" }));
        Assert.Empty(errors);
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
}
