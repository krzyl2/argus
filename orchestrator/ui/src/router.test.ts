import { describe, it, expect } from 'vitest';
import { normalizeHash, parseSensorEntityId } from './router';

describe('normalizeHash (D-01/D-05 default route + legacy redirects)', () => {
  it('redirects bare #/sensors to /detectors', () => {
    expect(normalizeHash('#/sensors')).toBe('/detectors');
  });

  it('redirects bare #/groups to /detectors', () => {
    expect(normalizeHash('#/groups')).toBe('/detectors');
  });

  it('leaves /groups/new unchanged (exact-match redirect only)', () => {
    expect(normalizeHash('#/groups/new')).toBe('/groups/new');
  });

  it('leaves /groups/:id unchanged (exact-match redirect only)', () => {
    expect(normalizeHash('#/groups/abc')).toBe('/groups/abc');
  });

  it('defaults an empty hash to /detectors', () => {
    expect(normalizeHash('')).toBe('/detectors');
  });

  it('passes through the new /detectors route unchanged', () => {
    expect(normalizeHash('#/detectors')).toBe('/detectors');
  });
});

describe('parseSensorEntityId (D-01)', () => {
  it('returns the decoded entity id for a well-formed path', () => {
    expect(parseSensorEntityId('/detectors/sensor/sensor.living_room_temp')).toBe(
      'sensor.living_room_temp'
    );
  });

  it('decodes an encodeURIComponent-encoded entity id', () => {
    const encoded = encodeURIComponent('sensor.living_room_temp');
    expect(parseSensorEntityId(`/detectors/sensor/${encoded}`)).toBe('sensor.living_room_temp');
  });

  it('returns null for a malformed percent-encoding (defensive fallback)', () => {
    expect(parseSensorEntityId('/detectors/sensor/%E0%A4%A')).toBeNull();
  });

  it('returns null for a non-matching path', () => {
    expect(parseSensorEntityId('/detectors')).toBeNull();
  });
});
