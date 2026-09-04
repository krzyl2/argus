// API contract types — see 07-UI-SPEC.md / 08-UI-SPEC.md "API Contract" sections.

/**
 * Single-sensor detector names. Declared ONCE here: before WS3 this union was re-typed
 * inline in eight places, so adding a name meant finding all eight, and `strictFunctionTypes`
 * turns a missed one into a contravariance error nowhere near the actual edit.
 *
 * `rmad` is the default (D-A). `hst` remains available as the rollback path and is
 * known-broken by design — it scores rarity, not deviation (F4).
 */
export type DetectorName = 'rmad' | 'hst' | 'mad' | 'stl';

export interface SensorEntry {
  entityId: string;
  friendlyName: string | null;
  // Null only for a synthesized row (see knownToHa): HA has never reported a value, and a
  // fabricated one would read as a live measurement.
  currentValue: string | null;
  unitOfMeasurement: string | null;
  isTracked: boolean;
  // WS4/F9: false means "Argus tracks this, HA does not list it" (e.g. an entity removed from HA
  // while still in entities.yaml). Optional because most fixtures predate it and an absent value
  // must read as "known", which is the overwhelmingly common case.
  knownToHa?: boolean;
  // SRCH-02/03 (08-02): HA area name (null if unresolved) + entity_id domain, e.g. "sensor".
  areaName: string | null;
  domain: string;
  // QUICK-warmup-status: warm-up progress, tracked entities only (null otherwise, and
  // null for a tracked entity the pipeline has not yet scored).
  warmedUp?: boolean | null;
  readingCount?: number | null;
  warmUpWindow?: number | null;
  // D-N: the detector list AS STORED, so the editor hydrates from the server instead of
  // seeding defaults. Optional: many test fixtures predate it, and an absent value must
  // read as "unknown", never as "no detectors" (which a save would then write to disk).
  detectors?: DetectorEntry[] | null;
  // D-E / F6-2: the calibrated band in the sensor's own units. Null until the first verdict.
  calibratedExpected?: number | null;
  calibratedLower?: number | null;
  calibratedUpper?: number | null;
  // Measured seconds between readings, used to render a window in SAMPLES as a wall-clock
  // span. Null means the UI shows samples only and says nothing it cannot back up.
  medianIntervalSec?: number | null;
}

export interface SensorsResponse {
  entries: SensorEntry[];
}

export interface DetectorEntry {
  name: DetectorName;
  params: Record<string, string>;
}

export interface SaveEntity {
  entityId: string;
  detectors: DetectorEntry[];
}

// Authoritative save request shape — MUST match 07-02's C# SaveRequest DTO exactly
// (natural nested-array shape per RESEARCH Open Q2, eliminates DetectorFieldParser).
export interface SaveRequest {
  entities: SaveEntity[];
  include: string;
  exclude: string;
}

export type SaveResponse =
  | { ok: true; count: number; hasStreaming: boolean }
  | { ok: false; kind: 'validation'; errorCount: number }
  | { ok: false; kind: 'error'; reason: string };

export interface DetectorDefaults {
  name: DetectorName;
  params: Record<string, string>;
}

/**
 * GET /api/detectors/defaults with no ?name= — the whole table plus the single-sensor
 * sensitivity presets. WR-02 is withdrawn: the client no longer mirrors these numbers,
 * so DetectorDefaults.cs is the only place they exist and a .NET-only rebuild moves the UI.
 */
export interface DetectorDefaultsResponse {
  defaults: Record<string, Record<string, string>>;
  presets: { rmad: DetectorPreset[] | null };
}

// ---------------------------------------------------------------------------
// Phase 8 — Group config + algorithm chooser (08-02 AUTHORITATIVE contracts).
// ---------------------------------------------------------------------------

/** "peer_divergence" | "joint" */
export type GroupMode = 'peer_divergence' | 'joint';

/** "peer_divergence" | "ecod" | "copod" | "pca" | "iforest" */
export type GroupDetectorName = 'peer_divergence' | 'ecod' | 'copod' | 'pca' | 'iforest';

// GET /api/groups response entry — matches Program.cs's anonymous projection of
// GroupConfig exactly (camelCase, @params -> params).
export interface GroupConfig {
  groupId: string;
  friendlyName: string;
  members: string[];
  mode: GroupMode;
  detector: GroupDetectorName;
  params: Record<string, string>;
}

export interface GroupsResponse {
  groups: GroupConfig[];
}

// POST /api/groups/save request body — MUST match GroupSaveRequest.cs exactly
// (full-list-replace: the entire groups: list, not a single-group PATCH).
export interface GroupSaveEntry {
  groupId: string;
  friendlyName: string;
  members: string[];
  mode: GroupMode;
  detector: GroupDetectorName;
  params: Record<string, string>;
}

export interface GroupSaveRequest {
  groups: GroupSaveEntry[];
}

export type GroupSaveResponse =
  | { ok: true; count: number }
  | { ok: false; kind: 'validation'; errorCount: number }
  | { ok: false; kind: 'error'; reason: string };

// GET /api/detectors/catalog response — matches DetectorCatalog.cs's record shapes.
export interface ParamFieldSchema {
  key: string;
  type: string;
  min: number | null;
  max: number | null;
  step: string | null;
}

export interface DetectorPreset {
  label: string;
  params: Record<string, string>;
}

export interface DetectorCatalogEntry {
  name: GroupDetectorName;
  bestFor: string;
  presets: DetectorPreset[];
  paramSchema: ParamFieldSchema[];
}

export interface GuidedAnswer {
  answer: string;
  detector: GroupDetectorName;
}

export interface DetectorCatalog {
  detectors: DetectorCatalogEntry[];
  guided: GuidedAnswer[];
}

// GET /api/groups/{id}/status response — matches the GroupStatusEntry projection
// in Program.cs exactly (200-with-null status for an unknown/never-scored id).
export interface FeatureContribution {
  memberId: string;
  contribution: number;
}

export interface GroupStatus {
  groupId: string;
  score: number | null;
  isAnomaly: boolean | null;
  detector: GroupDetectorName;
  scoredAtUtc: string;
  contributions: FeatureContribution[];
}

export interface GroupStatusResponse {
  status: GroupStatus | null;
}

// GET /api/settings response (D-06) — non-sensitive orchestrator configuration only (D-07).
export interface SettingsResponse {
  detectorEndpoint: string | null;
  influxUrl: string | null;
  influxBucket: string | null;
  batchIntervalMinutes: number;
  nightlyFitHour: number;
  logLevel: string | null;
}

// GET /api/health response (QUICK-dashboard-real-data) — matches HealthProjection.cs exactly
// (allowlist boundary, D-07 — no secrets ever appear here).
export type HealthStatus = 'ok' | 'warn' | 'error' | 'idle';

export interface HealthComponent {
  key: string;
  label: string;
  status: HealthStatus;
  detail: string;
}

export interface HealthResponse {
  homeAssistant: { connected: boolean; entityCount: number };
  components: HealthComponent[];
}

// ---------------------------------------------------------------------------
// WS6 — POST /api/sensors/{entityId}/simulate (replay panel).
// Matches SimulateEndpoint.cs's projection records exactly. Every field is new, so nothing
// here touches an existing fixture and nothing needs to be optional for `tsc -b` to pass.
// ---------------------------------------------------------------------------

export interface SimulateRequest {
  detector: DetectorName;
  params: Record<string, string>;
  /** Duration literal, ^\d+[smhdw]$. 24h is the window every acceptance number is stated in. */
  lookback: string;
  maxPoints: number;
}

/**
 * One ON run of the replayed gate, indexed into scores/values/timestamps.
 *
 * `endIndex` is exclusive, and equals the point count for an episode that never closed —
 * F2's signature, which has to stay drawable.
 */
export interface ReplayEpisodeSpan {
  startIndex: number;
  endIndex: number;
}

export interface SimulateSummary {
  episodes: number;
  /** Percentage of WALL-CLOCK time in alarm, not percentage of samples. */
  onTimePercent: number;
  spanHours: number;
  alertsPerDay: number;
  scorablePoints: number;
  /** All flag flips, both directions; ON->OFF count is transitions - episodes. */
  transitions: number;
  /**
   * The runs `episodes` counts, from the server's own gate pass. The chart's shaded bands are
   * THESE — the panel deliberately owns no gate of its own. On the adaptive path (the default)
   * the live decision can come from the raw channel's robust z, which the client cannot see at
   * all, so any client-side re-derivation from `scores` disagrees with the count printed beside
   * it: an episode with nothing shaded, or a shaded band over "0 epizodów".
   */
  episodeSpans: ReplayEpisodeSpan[];
  /**
   * First index at which the SCORE channel was calibrated. Before it only the raw channel could
   * fire, so episodes counted there came from one half of the evidence — and how long that
   * stretch lasts depends on the lookback, which is the operator's choice. The panel says so.
   */
  calibratedFromIndex: number;
}

export interface SimulateResponse {
  ok: boolean;
  error: string | null;
  /** Null whenever ok is false — "did not run" must not render as "found nothing". */
  summary: SimulateSummary | null;
  scores: number[];
  values: number[];
  timestamps: string[];
  /** First scorable index. Points before it are a structural 0.0, never an observation. */
  warmedUpFromIndex: number;
  /** Effective warm-up gate (hst: window, rmad: min_samples). */
  window: number;
}

// GET /api/anomalies/recent response (QUICK-dashboard-real-data) — matches the ring-buffer
// projection in Program.cs exactly (newest-first).
export interface RecentAnomaly {
  entityId: string | null;
  groupId: string | null;
  score: number;
  detector: string;
  detectedAtUtc: string;
}

export interface RecentAnomaliesResponse {
  anomalies: RecentAnomaly[];
}
