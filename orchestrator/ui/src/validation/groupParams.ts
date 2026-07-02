// Client-side group field validation — mirrors GroupInputValidator.cs
// (server is the authoritative boundary; this is UX-only fast feedback).
// See 08-UI-SPEC.md "Copywriting Contract". Messages are the parity spec —
// do not reword (English, operator-facing), same convention as detectorParams.ts.

import type { GroupMode, SensorEntry } from '../api/types';

const MIN_MEMBERS = 3;

const MSG_BELOW_FLOOR = 'A group needs at least 3 members.';

/**
 * Validates the member-count floor (EntitiesConfigLoader.ValidateGroups / GroupInputValidator
 * MinMembers=3). Returns null when the floor is met.
 */
export function validateGroupMembers(members: string[]): string | null {
  if (members.length < MIN_MEMBERS) {
    return MSG_BELOW_FLOOR;
  }
  return null;
}

/**
 * Validates peer-divergence unit consistency across selected members (GroupInputValidator's
 * peer-mode unit check). Joint mode always returns null — the unit-consistency rule only
 * applies to peer_divergence groups. Returns null when 0 or 1 distinct units are present.
 */
export function validateUnitConsistency(members: SensorEntry[], mode: GroupMode): string | null {
  if (mode !== 'peer_divergence') return null;

  const units = members
    .map((m) => m.unitOfMeasurement)
    .filter((u): u is string => !!u && u.trim() !== '');
  const distinctUnits = Array.from(new Set(units));

  if (distinctUnits.length > 1) {
    return `Peer-divergence groups need members with the same unit. Found: ${distinctUnits.join(', ')}.`;
  }
  return null;
}
