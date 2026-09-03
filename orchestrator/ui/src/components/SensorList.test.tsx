import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { SensorList } from './SensorList';
import type { SensorEntry } from '../api/types';
import type { EntityEditState } from '../state/sensors';

function makeEntry(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.default',
    friendlyName: null,
    currentValue: '21.5',
    unitOfMeasurement: '°C',
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

const noop = () => {};

// Fixture spans two named areas plus one entry with no area (falls into the
// domain/"Ungrouped" fallback bucket) — SEN-01 area/domain browse ordering.
// All four are tracked, each with one detector, so the trackedEntityIdx counter
// advances for every entry (D-08 invariant under test).
const ENTRIES: SensorEntry[] = [
  makeEntry({ entityId: 'sensor.sypialnia_temp', areaName: 'Sypialnia' }),
  makeEntry({ entityId: 'sensor.salon_temp', areaName: 'Salon' }),
  makeEntry({ entityId: 'sensor.salon_wilgotnosc', areaName: 'Salon' }),
  makeEntry({ entityId: 'sensor.no_area', areaName: null, domain: 'sensor' }),
];

function editsFor(entries: SensorEntry[]): Record<string, EntityEditState> {
  const edits: Record<string, EntityEditState> = {};
  for (const e of entries) {
    edits[e.entityId] = { isTracked: true, detectors: [{ name: 'hst', params: {} }] };
  }
  return edits;
}

function renderList(overrides: { groupByArea?: boolean; selectedEntityId?: string | null } = {}) {
  return render(
    <SensorList
      entries={ENTRIES}
      query=""
      edits={editsFor(ENTRIES)}
      selectedEntityId={overrides.selectedEntityId ?? null}
      onSelectRow={noop}
      onToggleTracked={noop}
      onDetectorTypeChange={noop}
      onDetectorParamChange={noop}
      onDetectorRemove={noop}
      onDetectorAdd={noop}
      groupByArea={overrides.groupByArea}
    />
  );
}

// Reads the entityIdx a rendered row's expanded detector editor exposes via
// DetectorEntry's `aria-label="Detector type for entity <idx>"` marker (the
// only DOM-visible surface of the trackedEntityIdx counter today).
function entityIdxFor(container: Element, entityId: string): number {
  const row = Array.from(container.querySelectorAll('.argus-list-row')).find(
    (r) => r.querySelector('.argus-row-entity-id')?.textContent === entityId
  );
  const control = row?.querySelector('[aria-label^="Detector type for entity "]');
  const label = control?.getAttribute('aria-label') ?? '';
  const match = label.match(/entity (\d+)/);
  if (!match) throw new Error(`entityIdx marker not found for ${entityId} (label="${label}")`);
  return Number(match[1]);
}

describe('SensorList groupByArea section ordering (SEN-01)', () => {
  it('renders section headers alphabetically by area with the domain/Ungrouped fallback last', () => {
    const { container } = renderList({ groupByArea: true });
    const summaries = Array.from(container.querySelectorAll('.argus-disclosure-toggle')).map(
      (el) => el.textContent
    );
    expect(summaries.length).toBe(3);
    expect(summaries[0]).toMatch(/^Salon/);
    expect(summaries[1]).toMatch(/^Sypialnia/);
    expect(summaries[2]).toMatch(/^sensor/); // domain fallback section, sorts last
  });

  it('wraps each section list in a Card', () => {
    const { container } = renderList({ groupByArea: true });
    const cards = container.querySelectorAll('.argus-sensor-list-grouped .argus-card');
    expect(cards.length).toBe(3);
  });
});

describe('SensorList trackedEntityIdx shared-counter uniqueness (D-08)', () => {
  it('assigns globally unique, monotonically-increasing entityIdx across groupByArea sections', () => {
    // Render once per entry with that entry selected (D-04: only the selected row
    // expands its editor) and collect the entityIdx each one reports. Section render
    // order is Salon (salon_temp, salon_wilgotnosc), Sypialnia (sypialnia_temp),
    // then the domain fallback (no_area) — a per-section-reset bug would make
    // sypialnia_temp report 0 again instead of continuing the shared count.
    const idxByEntity: Record<string, number> = {};
    for (const entry of ENTRIES) {
      const { container } = renderList({ groupByArea: true, selectedEntityId: entry.entityId });
      idxByEntity[entry.entityId] = entityIdxFor(container, entry.entityId);
    }

    const values = Object.values(idxByEntity);
    expect(new Set(values).size).toBe(values.length); // all unique — no collisions across sections
    // Section traversal order: Salon section first (salon_temp, salon_wilgotnosc),
    // then Sypialnia (sypialnia_temp) — its index must continue, not reset to 0.
    expect(idxByEntity['sensor.salon_temp']).toBe(0);
    expect(idxByEntity['sensor.salon_wilgotnosc']).toBe(1);
    expect(idxByEntity['sensor.sypialnia_temp']).toBe(2);
    expect(idxByEntity['sensor.no_area']).toBe(3);
  });

  it('flat mode (groupByArea off) still assigns a correctly incrementing index per entry', () => {
    const idxByEntity: Record<string, number> = {};
    for (const entry of ENTRIES) {
      const { container } = renderList({ groupByArea: false, selectedEntityId: entry.entityId });
      idxByEntity[entry.entityId] = entityIdxFor(container, entry.entityId);
    }
    // Flat mode iterates `entries` in original array order.
    expect(idxByEntity['sensor.sypialnia_temp']).toBe(0);
    expect(idxByEntity['sensor.salon_temp']).toBe(1);
    expect(idxByEntity['sensor.salon_wilgotnosc']).toBe(2);
    expect(idxByEntity['sensor.no_area']).toBe(3);
  });

  it('flat mode (groupByArea off) renders one row per entry, wrapped in a Card', () => {
    const { container } = renderList({ groupByArea: false });
    const rows = container.querySelectorAll('.argus-list-row');
    expect(rows.length).toBe(4);
    expect(container.querySelectorAll('.argus-sensor-list-grouped').length).toBe(0);
    expect(container.querySelector('.argus-card ul.argus-list')).not.toBeNull();
  });
});

describe('SensorList unknown-to-HA tracked entity (WS4/F9)', () => {
  const GHOST = makeEntry({
    entityId: 'sensor.zamrazarkapiwnica_power',
    currentValue: null,
    unitOfMeasurement: 'W',
    knownToHa: false,
  });

  function renderGhost() {
    const entries = [GHOST];
    return render(
      <SensorList
        entries={entries}
        query=""
        edits={editsFor(entries)}
        selectedEntityId={null}
        onSelectRow={noop}
        onToggleTracked={noop}
        onDetectorTypeChange={noop}
        onDetectorParamChange={noop}
        onDetectorRemove={noop}
        onDetectorAdd={noop}
      />
    );
  }

  it('renders unknown-to-HA tracked entity with the Polish chip', () => {
    // WHY (F9): this entity was being scored at 0.996 while invisible in the UI. Showing it
    // without saying HA no longer lists it would be worse than hiding it — the operator would
    // read a stale score as a live one. D8: operator-facing strings are Polish.
    const { container, getByText } = renderGhost();

    expect(container.querySelector('.argus-row-entity-id')?.textContent).toBe(
      'sensor.zamrazarkapiwnica_power'
    );
    expect(getByText('Nieznana w HA')).toBeTruthy();
    // No fabricated reading: a missing value renders as a dash, never as "0 W".
    expect(container.querySelector('.argus-row-value')?.textContent).toBe('—');
  });

  it('keeps the row interactive so the entity can still be unticked', () => {
    // WHY: visibility alone does not fix F9 — the point is that the operator can act on it.
    const { getByLabelText } = renderGhost();
    const checkbox = getByLabelText('sensor.zamrazarkapiwnica_power') as HTMLInputElement;
    expect(checkbox.disabled).toBe(false);
    expect(checkbox.checked).toBe(true);
  });

  it('shows no chip for an ordinary entity (knownToHa absent means known)', () => {
    // WHY: ~14 fixtures predate the field. An absent value must read as "known", or every
    // existing row would grow a scary badge.
    const { queryByText } = renderList();
    expect(queryByText('Nieznana w HA')).toBeNull();
  });
});
