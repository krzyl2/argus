import type { SensorEntry } from '../api/types';

interface CalibratedBandReadoutProps {
  entry?: SensorEntry;
}

function num(value: number, unit: string | null): string {
  // No fixed precision: a 0..1 load average and a 0..984 W power draw need different digits,
  // and rounding 0.54 to "1" would make the band look wrong to the person who knows the sensor.
  const rounded = Math.abs(value) >= 10 ? Math.round(value) : Math.round(value * 100) / 100;
  const text = String(rounded).replace('.', ',');
  return unit ? `${text} ${unit}` : text;
}

/**
 * Renders the alarm threshold in the SENSOR'S OWN UNITS: "Norma: 107 W · alarm poza 92–122 W".
 *
 * This is what makes a dimensionless threshold checkable (F6-2). The stored number is 0.5 on
 * every sensor by design — that is the whole point of the fix — but 0.5 is not something an
 * operator can agree or disagree with. The band is: they know their freezer sits at 101–109 W,
 * so they can see that 92–122 W will not fire on it, and that the fridge's 984 W compressor
 * spike will.
 *
 * It NEVER invents a band. Before the first verdict there is no median and no MAD, so the
 * component says how far calibration has got instead. A sensor that never changes value has no
 * scale at all, and says so — a made-up band there would be a confident statement about a
 * measurement nobody has taken, which is exactly the class of error this whole fix removes.
 */
export function CalibratedBandReadout({ entry }: CalibratedBandReadoutProps) {
  if (!entry) return null;

  const { calibratedExpected, calibratedLower, calibratedUpper } = entry;
  const unit = entry.unitOfMeasurement;

  if (calibratedExpected != null && calibratedLower != null && calibratedUpper != null) {
    // A zero-width band means the estimator found no spread at all: every value in the window
    // is the same, so there is no threshold to speak of.
    if (calibratedLower === calibratedUpper) {
      return (
        <p class="argus-calibrated-band argus-calibrated-band--unknown">
          Próg nieustalony — czujnik nie zmienia wartości.
        </p>
      );
    }
    return (
      <p class="argus-calibrated-band">
        Norma: {num(calibratedExpected, unit)} · alarm poza {num(calibratedLower, unit)}–
        {num(calibratedUpper, unit)}
      </p>
    );
  }

  if (entry.readingCount != null && entry.warmUpWindow != null) {
    return (
      <p class="argus-calibrated-band argus-calibrated-band--calibrating">
        Kalibracja {entry.readingCount}/{entry.warmUpWindow}
      </p>
    );
  }

  return (
    <p class="argus-calibrated-band argus-calibrated-band--unknown">
      Próg nieustalony — brak werdyktu z detektora.
    </p>
  );
}
