// Client-side field validation — ports _validationScript / InputValidator.cs rules verbatim.
// See 07-UI-SPEC.md "Client-side field validation rules". Messages are the parity spec —
// do not reword (English, operator-facing).

const MSG_INT_GE_1 = 'Must be a whole number ≥ 1.';
const MSG_INT_GE_2 = 'Must be a whole number ≥ 2.';
const MSG_HIGH = 'Must be between 0 and 1, and greater than low threshold.';
const MSG_LOW = 'Must be between 0 and 1, and less than high threshold.';
const MSG_FROZEN_VARIANCE = 'Must be 0 or greater.';
const MSG_GT_ZERO = 'Must be greater than 0.';
const MSG_REQUIRED = 'Must provide a value.';

// Integer fields, minimum value per key (matches EntityPickerPage.cs _validationScript PR table
// and InputValidator.cs ValidateIntAtLeast calls).
const INT_MIN: Record<string, number> = {
  window: 1,
  n_trees: 1,
  min_consecutive: 1,
  frozen_window: 1,
  period: 2,
  seasonal: 1,
};

function isBlankOrNonNumeric(raw: string): boolean {
  return raw.trim() === '' || Number.isNaN(parseFloat(raw));
}

/**
 * Validates a single field in isolation (no cross-field check). Cross-field
 * high/low comparison is applied separately by validateHstParams, matching
 * InputValidator.cs's two-phase approach (individual range, then cross-field).
 */
export function validateField(key: string, raw: string): string | null {
  if (isBlankOrNonNumeric(raw)) {
    return MSG_REQUIRED;
  }
  const value = parseFloat(raw);

  if (key in INT_MIN) {
    const min = INT_MIN[key];
    if (!Number.isInteger(value) || value < min) {
      return min >= 2 ? MSG_INT_GE_2 : MSG_INT_GE_1;
    }
    return null;
  }

  if (key === 'high_threshold') {
    if (value <= 0 || value > 1) return MSG_HIGH;
    return null;
  }

  if (key === 'low_threshold') {
    if (value < 0 || value >= 1) return MSG_LOW;
    return null;
  }

  if (key === 'frozen_variance_threshold') {
    if (value < 0) return MSG_FROZEN_VARIANCE;
    return null;
  }

  if (key === 'threshold') {
    if (value <= 0) return MSG_GT_ZERO;
    return null;
  }

  return null;
}

/**
 * Validates the full HST params set including the high_threshold > low_threshold
 * cross-field rule (InputValidator.cs ValidateHst / _validationScript "cr" flag).
 * Returns a map of field key -> error message for any field currently in error.
 */
export function validateHstParams(params: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};

  for (const key of [
    'window',
    'n_trees',
    'high_threshold',
    'low_threshold',
    'min_consecutive',
    'frozen_window',
    'frozen_variance_threshold',
  ]) {
    const raw = params[key] ?? '';
    const err = validateField(key, raw);
    if (err) errors[key] = err;
  }

  // Cross-field: only applies when both individually pass their own range check
  // (mirrors InputValidator.cs: "Only add cross-field errors if range errors not
  // already added").
  if (!errors.high_threshold && !errors.low_threshold) {
    const high = parseFloat(params.high_threshold ?? '');
    const low = parseFloat(params.low_threshold ?? '');
    if (!Number.isNaN(high) && !Number.isNaN(low) && high <= low) {
      errors.high_threshold = MSG_HIGH;
      errors.low_threshold = MSG_LOW;
    }
  }

  return errors;
}

export function validateMadParams(params: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const key of ['threshold', 'window']) {
    const err = validateField(key, params[key] ?? '');
    if (err) errors[key] = err;
  }
  return errors;
}

export function validateStlParams(params: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const key of ['period', 'seasonal', 'threshold']) {
    const err = validateField(key, params[key] ?? '');
    if (err) errors[key] = err;
  }
  return errors;
}

export function validateDetectorParams(
  name: 'hst' | 'mad' | 'stl',
  params: Record<string, string>
): Record<string, string> {
  switch (name) {
    case 'hst':
      return validateHstParams(params);
    case 'mad':
      return validateMadParams(params);
    case 'stl':
      return validateStlParams(params);
  }
}

/** True if any field in the given error map is in error — drives Save-button disabled state. */
export function hasAnyError(errors: Record<string, string>): boolean {
  return Object.keys(errors).length > 0;
}
