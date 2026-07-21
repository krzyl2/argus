import { computed } from '@preact/signals';
import type { GroupConfig, SensorEntry } from '../api/types';
import { groups } from './groups';
import { sensors, entityEdits } from './sensors';

// D-03/DET-01: one unified row model merging groups (state/groups.ts) + tracked-only
// single sensors (state/sensors.ts) for the Detectors list screen. Namespaced `key`
// (group: / sensor:) is defense-in-depth against a future group-id scheme change
// (Pitfall 2) — a real collision is impossible today since slugify() strips `.` from
// group ids while HA entity ids always contain one.
export interface DetectorRow {
  key: string;
  kind: 'group' | 'sensor';
  group?: GroupConfig;
  entry?: SensorEntry;
}

// Pure derivation over existing signals — no new fetch logic. Groups-first, then
// sensors (sort order is Claude's discretion per 14-CONTEXT.md; groups-first is the
// simplest stable order). Tracked-ness reads entityEdits first (client edit state
// takes precedence over the server flag), falling back to the server's isTracked —
// matches how the rest of the app reads tracked-ness (e.g. state/sensors.ts's save()).
export const detectorRows = computed<DetectorRow[]>(() => {
  const groupRows: DetectorRow[] = groups.value.map((g) => ({
    key: `group:${g.groupId}`,
    kind: 'group',
    group: g,
  }));
  const sensorRows: DetectorRow[] = sensors.value
    .filter((s) => entityEdits.value[s.entityId]?.isTracked ?? s.isTracked)
    .map((s) => ({
      key: `sensor:${s.entityId}`,
      kind: 'sensor',
      entry: s,
    }));
  return [...groupRows, ...sensorRows];
});
