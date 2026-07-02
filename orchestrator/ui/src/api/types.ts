// API contract types — see 07-UI-SPEC.md "API Contract" section.

export interface SensorEntry {
  entityId: string;
  friendlyName: string | null;
  currentValue: string;
  unitOfMeasurement: string | null;
  isTracked: boolean;
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
