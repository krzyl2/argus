import { describe, it, expect } from 'vitest';
import {
  validateField,
  validateHstParams,
  validateMadParams,
  validateStlParams,
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
