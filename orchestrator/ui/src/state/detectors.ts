import { computed } from '@preact/signals';
import type { GroupConfig, GroupStatus, SensorEntry } from '../api/types';
import { groups, groupStatuses } from './groups';
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
  // Group rows only: last-known status. `undefined` = not yet fetched (no badge),
  // `null` = fetched but never scored ("Oczekuje"), otherwise the scored GroupStatus.
  status?: GroupStatus | null;
}

// Pure derivation over existing signals — no new fetch logic. Groups-first, then
// sensors (sort order is Claude's discretion per 14-CONTEXT.md; groups-first is the
// simplest stable order). Tracked-ness reads entityEdits first (client edit state
// takes precedence over the server flag), falling back to the server's isTracked —
// matches how the rest of the app reads tracked-ness (e.g. state/sensors.ts's save()).
export const detectorRows = computed<DetectorRow[]>(() => {
  // Read groupStatuses.value inside the computed body so status refreshes re-derive
  // the rows (missing key => undefined => no status badge yet).
  const statuses = groupStatuses.value;
  const groupRows: DetectorRow[] = groups.value.map((g) => ({
    key: `group:${g.groupId}`,
    kind: 'group',
    group: g,
    status: statuses[g.groupId],
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
