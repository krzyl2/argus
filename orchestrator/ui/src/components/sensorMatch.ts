import type { SensorEntry } from '../api/types';

/**
 * SRCH-01 match predicate: entity_id OR friendly_name, case-insensitive substring.
 * Strict superset of the Phase 7 entity_id-only behavior — used by MemberPicker's
 * client-side filter over an already-loaded sensor list (the same predicate the
 * server applies in HaSensorRegistry.GetFiltered for #/sensors's live query).
 */
export function matchesSensorQuery(entry: SensorEntry, query: string): boolean {
  if (!query) return true;
  const q = query.toLowerCase();
  if (entry.entityId.toLowerCase().includes(q)) return true;
  if (entry.friendlyName && entry.friendlyName.toLowerCase().includes(q)) return true;
  return false;
}
