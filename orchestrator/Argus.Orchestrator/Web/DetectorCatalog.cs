namespace Argus.Orchestrator.Web;

/// <summary>
/// Schema for one tunable param in the Advanced form (ALGO-02).
/// </summary>
public record ParamFieldSchema(string Key, string Type, double? Min, double? Max, string? Step);

/// <summary>One named sensitivity preset ("Low" | "Med" | "High") and its concrete param values.</summary>
public record DetectorPreset(string Label, Dictionary<string, string> Params);

/// <summary>
/// One entry in the group-detector catalog, backing GET /api/detectors/catalog (ALGO-01..03).
/// Presets are always exactly 3 (Low/Med/High, RESEARCH Pattern 3).
/// </summary>
public record DetectorCatalogEntry(
    string Name,
    string BestFor,
    List<DetectorPreset> Presets,
    List<ParamFieldSchema> ParamSchema);

/// <summary>One guided-question answer mapped to its recommended detector (ALGO-04).</summary>
public record GuidedAnswer(string Answer, string Detector);

/// <summary>
/// Static, purely descriptive catalog of the 5 group detectors (peer_divergence, ecod, copod,
/// pca, iforest): Low/Med/High presets, "best for..." copy, and the Advanced-form param schema.
///
/// This is a NEW, PARALLEL table to DetectorDefaults.cs (per-entity hst/mad/stl) — not a
/// replacement. Never calls gRPC/Python; catalog content must render even when the detector
/// process is down (RESEARCH.md Anti-Pattern).
///
/// Preset numeric values [ASSUMED — PyOD-default-centered, not tuned/backtested, confirm at UAT]
/// use the exact param key names honored by Plan 08-01's Python change: `threshold`
/// (peer_divergence), `contamination` (ecod/copod/pca/iforest), `n_estimators` (iforest).
///
/// Honesty note (RESEARCH Pitfall 2): for ecod/copod/pca/iforest, `contamination` only shifts
/// the internal `threshold_` used by `is_anomaly` — it never changes the continuous
/// `decision_function()` score that MQTT publishes as the score sensor. The BestFor/preset
/// copy below states this explicitly; do not imply "High sensitivity = higher score."
/// </summary>
public static class DetectorCatalog
{
    // Phase 9 (ALGO-06): the BestFor copy below is a DRAFT pending operator redaction
    // (ROADMAP scope item 4) — it corrects two prior inaccuracies found by empirical PyOD
    // testing: (1) ECOD/PCA produced ~90% false positives on correlated-pair relationship-break
    // scenarios that COPOD/IForest handled correctly, and (2) peer_divergence's old "know WHICH
    // member is diverging" phrasing does not hold for a 2-member group (Plan 09-01), which
    // reports a single pair-relationship verdict with no per-member attribution.
    public static List<DetectorCatalogEntry> All() =>
    [
        new DetectorCatalogEntry(
            Name: "peer_divergence",
            BestFor: "Best for a group of similar sensors (e.g. tire pressures, per-room temperatures). " +
                     "For 3+ members, flags and identifies WHICH member is diverging from the others. " +
                     "For exactly 2 members, there is no 'others' to compare against — it instead " +
                     "flags when the pair's own relationship breaks (e.g. two front tires that " +
                     "normally track each other), reporting one verdict for the pair with no " +
                     "per-member attribution. Sensitivity directly changes how far a member (or the " +
                     "pair) must drift before it is flagged.",
            Presets:
            [
                new DetectorPreset("Low", new Dictionary<string, string> { ["threshold"] = "4.5" }),
                new DetectorPreset("Med", new Dictionary<string, string> { ["threshold"] = "3.5" }),
                new DetectorPreset("High", new Dictionary<string, string> { ["threshold"] = "2.5" }),
            ],
            ParamSchema:
            [
                new ParamFieldSchema("threshold", "number", 1.0, 10.0, "0.1"),
            ]),

        new DetectorCatalogEntry(
            Name: "ecod",
            BestFor: "Best for sensors that are NOT expected to move together — flags when the whole " +
                     "value vector looks jointly abnormal and shows which member contributed most. " +
                     "Caution: on sensors that normally move together (e.g. two correlated pressures), " +
                     "ECOD tends to flag normal correlated movement as anomalous — prefer COPOD or " +
                     "IForest for that case. Sensitivity shifts how often the anomaly flag fires for a " +
                     "given score distribution; it does not change the anomaly score itself.",
            Presets:
            [
                new DetectorPreset("Low", new Dictionary<string, string> { ["contamination"] = "0.05" }),
                new DetectorPreset("Med", new Dictionary<string, string> { ["contamination"] = "0.1" }),
                new DetectorPreset("High", new Dictionary<string, string> { ["contamination"] = "0.2" }),
            ],
            ParamSchema:
            [
                new ParamFieldSchema("contamination", "number", 0.01, 0.5, "0.01"),
            ]),

        new DetectorCatalogEntry(
            Name: "copod",
            BestFor: "Best for a group of correlated sensors that should move together (e.g. two tire " +
                     "pressures, or humidity + temperature in one room) — handles the normal correlated " +
                     "relationship well and flags a genuine break in it, with per-member attribution. " +
                     "Recommended default for 'these sensors move together' groups. Sensitivity shifts " +
                     "how often the anomaly flag fires for a given score distribution; it does not " +
                     "change the anomaly score itself.",
            Presets:
            [
                new DetectorPreset("Low", new Dictionary<string, string> { ["contamination"] = "0.05" }),
                new DetectorPreset("Med", new Dictionary<string, string> { ["contamination"] = "0.1" }),
                new DetectorPreset("High", new Dictionary<string, string> { ["contamination"] = "0.2" }),
            ],
            ParamSchema:
            [
                new ParamFieldSchema("contamination", "number", 0.01, 0.5, "0.01"),
            ]),

        new DetectorCatalogEntry(
            Name: "pca",
            BestFor: "Best for a group of correlated sensors where anomalies show up as a break in their " +
                     "normal linear relationship (e.g. several sensors that usually track each other). " +
                     "Caution: like ECOD, PCA tends to flag normal correlated movement as anomalous on " +
                     "tightly-correlated pairs — prefer COPOD or IForest for that case. No per-member " +
                     "attribution is available for this detector. Sensitivity shifts how often the " +
                     "anomaly flag fires for a given score distribution; it does not change the anomaly " +
                     "score itself.",
            Presets:
            [
                new DetectorPreset("Low", new Dictionary<string, string> { ["contamination"] = "0.05" }),
                new DetectorPreset("Med", new Dictionary<string, string> { ["contamination"] = "0.1" }),
                new DetectorPreset("High", new Dictionary<string, string> { ["contamination"] = "0.2" }),
            ],
            ParamSchema:
            [
                new ParamFieldSchema("contamination", "number", 0.01, 0.5, "0.01"),
            ]),

        new DetectorCatalogEntry(
            Name: "iforest",
            BestFor: "Best for a larger group of sensors with complex, non-linear relationships — also " +
                     "handles correlated-pair relationship breaks well (similar to COPOD). No " +
                     "per-member attribution is available for this detector. `contamination` shifts how " +
                     "often the anomaly flag fires for a given score distribution (not the score itself); " +
                     "`n_estimators` (tree count) affects score stability/quality.",
            Presets:
            [
                new DetectorPreset("Low", new Dictionary<string, string> { ["contamination"] = "0.05", ["n_estimators"] = "100" }),
                new DetectorPreset("Med", new Dictionary<string, string> { ["contamination"] = "0.1", ["n_estimators"] = "100" }),
                new DetectorPreset("High", new Dictionary<string, string> { ["contamination"] = "0.2", ["n_estimators"] = "150" }),
            ],
            ParamSchema:
            [
                new ParamFieldSchema("contamination", "number", 0.01, 0.5, "0.01"),
                new ParamFieldSchema("n_estimators", "number", 10, 500, "10"),
            ]),
    ];

    /// <summary>
    /// Guided "what are you monitoring?" answer -> recommended detector mapping (ALGO-04).
    /// UI copy/config only — never fetched from Python.
    /// ALGO-05: "together" recommends copod (not ecod) — empirical PyOD testing found ECOD/PCA
    /// produce ~90% false positives on correlated-pair relationship-break scenarios that COPOD
    /// handles correctly (2/10 false-positive rate).
    /// </summary>
    public static List<GuidedAnswer> Guided() =>
    [
        new GuidedAnswer("together", "copod"),
        new GuidedAnswer("diverges", "peer_divergence"),
    ];
}
