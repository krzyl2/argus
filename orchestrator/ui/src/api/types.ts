// API contract types — see 07-UI-SPEC.md / 08-UI-SPEC.md "API Contract" sections.

export interface SensorEntry {
  entityId: string;
  friendlyName: string | null;
  currentValue: string;
  unitOfMeasurement: string | null;
  isTracked: boolean;
  // SRCH-02/03 (08-02): HA area name (null if unresolved) + entity_id domain, e.g. "sensor".
  areaName: string | null;
  domain: string;
}

export interface SensorsResponse {
  entries: SensorEntry[];
}

export interface DetectorEntry {
  name: 'hst' | 'mad' | 'stl';
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
  | { ok: true; count: number; hasHst: boolean }
  | { ok: false; kind: 'validation'; errorCount: number }
  | { ok: false; kind: 'error'; reason: string };

export interface DetectorDefaults {
  name: 'hst' | 'mad' | 'stl';
  params: Record<string, string>;
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
