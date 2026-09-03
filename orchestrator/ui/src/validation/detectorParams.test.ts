import { describe, it, expect } from 'vitest';
import {
  validateField,
  validateHstParams,
  validateMadParams,
  validateStlParams,
  validateRmadParams,
  validateDetectorParams,
  hasAnyError,
} from './detectorParams';

// Encodes INTENT: validation must block save exactly where v3 (EntityPickerPage.cs
// _validationScript + InputValidator.cs) blocked it — not just "some" validation.

describe('validateField', () => {
  it('rejects empty value', () => {
    expect(validateField('window', '')).toBe('Must provide a value.');
  });

  it('rejects non-numeric value', () => {
    expect(validateField('window', 'abc')).toBe('Must provide a value.');
  });

  it('window=0 is an error (must be >= 1)', () => {
    expect(validateField('window', '0')).toBe('Must be a whole number ≥ 1.');
  });

  it('window=1 is valid', () => {
    expect(validateField('window', '1')).toBeNull();
  });

  it('period=1 is an error (must be >= 2)', () => {
    expect(validateField('period', '1')).toBe('Must be a whole number ≥ 2.');
  });

  it('period=2 is valid', () => {
    expect(validateField('period', '2')).toBeNull();
  });

  it('threshold=0 is an error (must be > 0)', () => {
    expect(validateField('threshold', '0')).toBe('Must be greater than 0.');
  });

  it('threshold=0.1 is valid', () => {
    expect(validateField('threshold', '0.1')).toBeNull();
  });

  it('frozen_variance_threshold=0 is valid (>= 0 allowed)', () => {
    expect(validateField('frozen_variance_threshold', '0')).toBeNull();
  });

  it('frozen_variance_threshold=-1 is an error', () => {
    expect(validateField('frozen_variance_threshold', '-1')).toBe('Must be 0 or greater.');
  });
});

describe('validateHstParams cross-field high/low', () => {
  const base = {
    window: '250',
    n_trees: '25',
    min_consecutive: '3',
    frozen_window: '10',
    frozen_variance_threshold: '0.001',
  };

  it('high=0.7/low=0.3 is valid', () => {
    const errors = validateHstParams({ ...base, high_threshold: '0.7', low_threshold: '0.3' });
    expect(hasAnyError(errors)).toBe(false);
  });

  it('high=0.3/low=0.7 is a cross-field error on both fields', () => {
    const errors = validateHstParams({ ...base, high_threshold: '0.3', low_threshold: '0.7' });
    expect(errors.high_threshold).toBe(
      'Must be between 0 and 1, and greater than low threshold.'
    );
    expect(errors.low_threshold).toBe('Must be between 0 and 1, and less than high threshold.');
  });

  it('high == low is a cross-field error (strictly greater required)', () => {
    const errors = validateHstParams({ ...base, high_threshold: '0.5', low_threshold: '0.5' });
    expect(hasAnyError(errors)).toBe(true);
  });

  it('empty high_threshold is required-field error, not cross-field', () => {
    const errors = validateHstParams({ ...base, high_threshold: '', low_threshold: '0.3' });
    expect(errors.high_threshold).toBe('Must provide a value.');
  });
});

describe('validateMadParams', () => {
  it('valid defaults pass', () => {
    expect(hasAnyError(validateMadParams({ threshold: '3.5', window: '20' }))).toBe(false);
  });

  it('threshold=0 fails', () => {
    const errors = validateMadParams({ threshold: '0', window: '20' });
    expect(errors.threshold).toBe('Must be greater than 0.');
  });
});

describe('validateStlParams', () => {
  it('valid defaults pass', () => {
    expect(
      hasAnyError(validateStlParams({ period: '24', seasonal: '7', threshold: '3.0' }))
    ).toBe(false);
  });

  it('period=1 fails', () => {
    const errors = validateStlParams({ period: '1', seasonal: '7', threshold: '3.0' });
    expect(errors.period).toBe('Must be a whole number ≥ 2.');
  });
});


describe('validateRmadParams', () => {
  // Fixtures start from the server default table and override ONLY the key under test. Every
  // rmad key is required, so a partial fixture would produce errors unrelated to the rule being
  // exercised and each case would pass for the wrong reason.
  const RMAD_DEFAULTS: Record<string, string> = {
    window: '720',
    min_samples: '60',
    z_scale: '5.0',
    scale_floor: '0.0',
    high_threshold: '0.5',
    low_threshold: '0.375',
    min_consecutive: '3',
    frozen_window: '10',
    frozen_variance_threshold: '0.0',
  };

  const withRmad = (overrides: Record<string, string> = {}) => ({ ...RMAD_DEFAULTS, ...overrides });

  it('accepts the shipped default table', () => {
    // If the defaults did not validate, every Save after the migration would be blocked on a
    // config the add-on wrote itself.
    expect(validateRmadParams(withRmad())).toEqual({});
  });

  // The message strings are copied verbatim from InputValidator.cs. A drift between the two
  // sides is not cosmetic: the browser would let a value through that the server rejects, and
  // the operator would get a failed save with no field highlighted.
  it('rejects a window outside 30..10000 with the server message', () => {
    expect(validateRmadParams(withRmad({ window: '29', min_samples: '10' })).window).toBe(
      'Must be a whole number between 30 and 10000.'
    );
    expect(validateRmadParams(withRmad({ window: '10001' })).window).toBe(
      'Must be a whole number between 30 and 10000.'
    );
    expect(validateRmadParams(withRmad({ window: '30', min_samples: '10' })).window).toBeUndefined();
    expect(validateRmadParams(withRmad({ window: '10000' })).window).toBeUndefined();
  });

  // validateField must be called with the detector name. The two-argument form falls through to
  // the generic INT_MIN table, where `window` only has to be >= 1 — so a window of 5 would pass
  // in the browser and be rejected by the server. Both entry points must agree.
  it('applies the window range through validateField too', () => {
    expect(validateField('window', '29', 'rmad')).toBe('Must be a whole number between 30 and 10000.');
    // Same key, different detector: hst keeps its own >= 1 rule, untouched.
    expect(validateField('window', '29', 'hst')).toBeNull();
    expect(validateField('window', '29')).toBeNull();
  });

  it('requires min_samples >= 10', () => {
    expect(validateRmadParams(withRmad({ min_samples: '9' })).min_samples).toBe(
      'Must be a whole number ≥ 10.'
    );
  });

  // A min_samples above the window it is counted against can never be reached, so the entity
  // would sit in calibration forever and never alarm — a misconfiguration that looks healthy.
  it('rejects min_samples greater than window', () => {
    const errors = validateRmadParams(withRmad({ min_samples: '720', window: '60' }));
    expect(errors.min_samples).toBe('Must not be greater than window.');
  });

  it('rejects a non-positive z_scale and a negative scale_floor', () => {
    expect(validateRmadParams(withRmad({ z_scale: '0' })).z_scale).toBe('Must be greater than 0.');
    expect(validateRmadParams(withRmad({ scale_floor: '-1' })).scale_floor).toBe('Must be 0 or greater.');
  });

  // Inverted thresholds are the one misconfiguration that looks valid and never alarms: the
  // gate can never release, or never fire.
  it('rejects high <= low on both fields', () => {
    const errors = validateRmadParams(withRmad({ high_threshold: '0.3', low_threshold: '0.4' }));
    expect(errors.high_threshold).toBeTruthy();
    expect(errors.low_threshold).toBeTruthy();
  });

  // D-H: frozen is disabled by variance, never by the window. A window of 0 makes the .NET
  // FrozenSensorDetector dequeue an empty queue on the first reading.
  it('rejects frozen_window 0 but accepts frozen_variance_threshold 0', () => {
    expect(validateRmadParams(withRmad({ frozen_window: '0' })).frozen_window).toBeTruthy();
    expect(
      validateRmadParams(withRmad({ frozen_variance_threshold: '0.0' })).frozen_variance_threshold
    ).toBeUndefined();
  });

  it('routes through validateDetectorParams', () => {
    expect(validateDetectorParams('rmad', withRmad())).toEqual({});
    expect(validateDetectorParams('rmad', withRmad({ window: '1' })).window).toBeTruthy();
  });
});
