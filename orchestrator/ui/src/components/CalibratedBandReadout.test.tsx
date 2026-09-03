import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { CalibratedBandReadout } from './CalibratedBandReadout';
import type { SensorEntry } from '../api/types';

function makeSensor(overrides: Partial<SensorEntry> = {}): SensorEntry {
  return {
    entityId: 'sensor.zamrazarkapiwnica_power',
    friendlyName: null,
    currentValue: '107',
    unitOfMeasurement: 'W',
    isTracked: true,
    areaName: null,
    domain: 'sensor',
    ...overrides,
  };
}

describe('CalibratedBandReadout', () => {
  // F6-2. The stored threshold is 0.5 on every sensor by design — that is the fix. But 0.5 is
  // not a claim an operator can check. The band is: they know the freezer sits at 101-109 W,
  // so 92-122 W visibly will not fire on it, and the fridge's 984 W spike visibly will.
  it('renders the band in the sensor own units', () => {
    const { container } = render(
      <CalibratedBandReadout
        entry={makeSensor({
          calibratedExpected: 107,
          calibratedLower: 92,
          calibratedUpper: 122,
        })}
      />
    );

    expect(container.textContent).toMatch(/Norma: 107 W/);
    expect(container.textContent).toMatch(/92 W/);
    expect(container.textContent).toMatch(/122 W/);
  });

  // This is the load-bearing rule. Every other failure mode in this fix came from a number that
  // looked measured and was not; a band drawn before any median/MAD exists would be exactly
  // that, and the operator would tune against it.
  it('NeverAFabricatedBand', () => {
    const calibrating = render(
      <CalibratedBandReadout entry={makeSensor({ readingCount: 12, warmUpWindow: 60 })} />
    );
    expect(calibrating.container.textContent).toMatch(/Kalibracja 12\/60/);
    expect(calibrating.container.textContent).not.toMatch(/Norma/);

    // Partial data must not be completed by guessing the missing edge.
    const partial = render(
      <CalibratedBandReadout
        entry={makeSensor({ calibratedExpected: 107, calibratedLower: 92, calibratedUpper: null })}
      />
    );
    expect(partial.container.textContent).not.toMatch(/Norma/);

    // No verdict at all, and no warm-up progress either: say so rather than show nothing,
    // which would read as "this sensor has no threshold problem".
    const unknown = render(<CalibratedBandReadout entry={makeSensor()} />);
    expect(unknown.container.textContent).toMatch(/Próg nieustalony/);
  });

  // A sensor that never changes value has no scale at all (the degenerate rung of rmad's scale
  // ladder). Reporting "alarm poza 5-5 W" would be a confident statement about a measurement
  // that does not exist.
  it('says the threshold is unset when the sensor does not move', () => {
    const { container } = render(
      <CalibratedBandReadout
        entry={makeSensor({ calibratedExpected: 5, calibratedLower: 5, calibratedUpper: 5 })}
      />
    );

    expect(container.textContent).toMatch(/nie zmienia wartości/);
  });

  it('renders nothing without an entry', () => {
    const { container } = render(<CalibratedBandReadout />);
    expect(container.textContent).toBe('');
  });
});
