import { describe, it, expect } from 'vitest';
import { validateGroupMembers, validateUnitConsistency } from './groupParams';
import type { SensorEntry } from '../api/types';

// Encodes INTENT: client validation must block save exactly where the server
// (GroupInputValidator.cs) blocks it — floor=2, peer-mode unit consistency.

function makeSensor(overrides: Partial<SensorEntry>): SensorEntry {
  return {
    entityId: 'sensor.x',
    friendlyName: null,
    currentValue: '1',
    unitOfMeasurement: null,
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

describe('validateGroupMembers', () => {
  it('rejects fewer than 2 members', () => {
    expect(validateGroupMembers(['a'])).toBe('A group needs at least 2 members.');
  });

  it('rejects an empty member list', () => {
    expect(validateGroupMembers([])).toBe('A group needs at least 2 members.');
  });

  // A 2-member group is now a valid paired comparison (joint pairing or peer_divergence
  // pairwise-delta) — GRP-10/GRP-12, not just "one below the old floor of 3".
  it('accepts exactly 2 members', () => {
    expect(validateGroupMembers(['a', 'b'])).toBeNull();
  });

  it('accepts exactly 3 members', () => {
    expect(validateGroupMembers(['a', 'b', 'c'])).toBeNull();
  });

  it('accepts more than 3 members', () => {
    expect(validateGroupMembers(['a', 'b', 'c', 'd'])).toBeNull();
  });
});

describe('validateUnitConsistency', () => {
  it('returns the mismatch message for peer_divergence mode with mixed units', () => {
    const members = [
      makeSensor({ entityId: 'sensor.a', unitOfMeasurement: '°C' }),
      makeSensor({ entityId: 'sensor.b', unitOfMeasurement: '%' }),
    ];
    expect(validateUnitConsistency(members, 'peer_divergence')).toBe(
      'Peer-divergence groups need members with the same unit. Found: °C, %.'
    );
  });

  it('returns null for peer_divergence mode with matching units', () => {
    const members = [
      makeSensor({ entityId: 'sensor.a', unitOfMeasurement: '°C' }),
      makeSensor({ entityId: 'sensor.b', unitOfMeasurement: '°C' }),
    ];
    expect(validateUnitConsistency(members, 'peer_divergence')).toBeNull();
  });

  it('returns null for joint mode regardless of unit mismatch', () => {
    const members = [
      makeSensor({ entityId: 'sensor.a', unitOfMeasurement: '°C' }),
      makeSensor({ entityId: 'sensor.b', unitOfMeasurement: '%' }),
    ];
    expect(validateUnitConsistency(members, 'joint')).toBeNull();
  });

  it('returns null when units are null/unresolved (degrade-safe, matches server skip-check)', () => {
    const members = [
      makeSensor({ entityId: 'sensor.a', unitOfMeasurement: null }),
      makeSensor({ entityId: 'sensor.b', unitOfMeasurement: null }),
    ];
    expect(validateUnitConsistency(members, 'peer_divergence')).toBeNull();
  });
});
