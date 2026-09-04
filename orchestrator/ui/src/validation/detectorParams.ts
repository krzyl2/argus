// Client-side field validation — ports _validationScript / InputValidator.cs rules verbatim.
// See 07-UI-SPEC.md "Client-side field validation rules". Messages are the parity spec —
// do not reword (English, operator-facing).

import type { DetectorName } from '../api/types';

const MSG_INT_GE_1 = 'Must be a whole number ≥ 1.';
const MSG_INT_GE_2 = 'Must be a whole number ≥ 2.';
const MSG_HIGH = 'Must be between 0 and 1, and greater than low threshold.';
const MSG_LOW = 'Must be between 0 and 1, and less than high threshold.';
const MSG_FROZEN_VARIANCE = 'Must be 0 or greater.';
const MSG_GT_ZERO = 'Must be greater than 0.';
const MSG_REQUIRED = 'Must provide a value.';

// rmad (D-A/D-B). Verbatim copies of InputValidator.MSG_WINDOW_RANGE / MSG_MIN_SAMPLES /
// MSG_MIN_SAMPLES_LE_WINDOW. A drift between the two sides shows up as a form that lets the
// operator save a value the server then rejects with no field highlighted.
const MSG_WINDOW_RANGE = 'Must be a whole number between 30 and 10000.';
const MSG_MIN_SAMPLES = 'Must be a whole number ≥ 10.';
const MSG_MIN_SAMPLES_LE_WINDOW = 'Must not be greater than window.';

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
 * An OMITTED key is not an empty field: it means "use the server default", the same thing it
 * means to RmadParams.From and to InputValidator (which validates the submitted keys layered
 * over DetectorDefaults). `params: {}` is what a fresh install stores for every entity
 * (gen-entities.py) and what the save path writes for an entity it defaulted.
 *
 * Reporting MSG_REQUIRED on such a key made one stored entity disable Save for the WHOLE
 * screen — validationErrors aggregates across every tracked entity — with no field visibly
 * wrong, because none of them was wrong, they were merely absent. That is still reachable
 * whenever GET /api/detectors/defaults fails, since defaultsFor() then returns {} and nothing
 * can fill the gaps in; the rule has to live here, not in whoever hydrates the editor.
 *
 * A key that IS present and blank stays an error: that is a field the operator cleared.
 */
function isOmitted(params: Record<string, string>, key: string): boolean {
  return !(key in params);
}

/**
 * Validates a single field in isolation (no cross-field check). Cross-field
 * high/low comparison is applied separately by validateHstParams, matching
 * InputValidator.cs's two-phase approach (individual range, then cross-field).
 */
export function validateField(key: string, raw: string, detector?: DetectorName): string | null {
  if (isBlankOrNonNumeric(raw)) {
    return MSG_REQUIRED;
  }
  const value = parseFloat(raw);

  // rmad-only ranges. They are keyed on the detector rather than on the key name because
  // `window` exists on hst and mad too, with entirely different bounds — a global range here
  // would silently start rejecting valid hst configs.
  if (detector === 'rmad') {
    if (key === 'window') {
      if (!Number.isInteger(value) || value < 30 || value > 10000) return MSG_WINDOW_RANGE;
      return null;
    }
    if (key === 'min_samples') {
      if (!Number.isInteger(value) || value < 10) return MSG_MIN_SAMPLES;
      return null;
    }
    if (key === 'z_scale') {
      if (value <= 0) return MSG_GT_ZERO;
      return null;
    }
    if (key === 'scale_floor') {
      if (value < 0) return MSG_FROZEN_VARIANCE;
      return null;
    }
  }

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
    if (isOmitted(params, key)) continue;
    const err = validateField(key, params[key]);
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

/**
 * Validates the full rmad params set (D-A/D-B), mirroring InputValidator.ValidateRmad.
 *
 * Note the three-argument validateField calls: the two-argument form would fall through to the
 * generic INT_MIN table, where `window` only has to be >= 1 — so a window of 5 would pass in
 * the browser and be rejected by the server, which is the exact class of drift this file exists
 * to prevent.
 */
export function validateRmadParams(params: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};

  for (const key of [
    'window',
    'min_samples',
    'z_scale',
    'scale_floor',
    'high_threshold',
    'low_threshold',
    'min_consecutive',
    'frozen_window',
    'frozen_variance_threshold',
  ]) {
    if (isOmitted(params, key)) continue;
    const err = validateField(key, params[key], 'rmad');
    if (err) errors[key] = err;
  }

  // Cross-field, same two-phase shape as HST: only when both sides individually passed.
  if (!errors.high_threshold && !errors.low_threshold) {
    const high = parseFloat(params.high_threshold ?? '');
    const low = parseFloat(params.low_threshold ?? '');
    if (!Number.isNaN(high) && !Number.isNaN(low) && high <= low) {
      errors.high_threshold = MSG_HIGH;
      errors.low_threshold = MSG_LOW;
    }
  }

  // A min_samples above the window it is counted against can never be reached, so the entity
  // would sit in "calibrating" forever and never alarm — a misconfiguration that looks fine.
  if (!errors.min_samples && !errors.window) {
    const minSamples = parseFloat(params.min_samples ?? '');
    const window = parseFloat(params.window ?? '');
    if (!Number.isNaN(minSamples) && !Number.isNaN(window) && minSamples > window) {
      errors.min_samples = MSG_MIN_SAMPLES_LE_WINDOW;
    }
  }

  return errors;
}

export function validateMadParams(params: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const key of ['threshold', 'window']) {
    if (isOmitted(params, key)) continue;
    const err = validateField(key, params[key]);
    if (err) errors[key] = err;
  }
  return errors;
}

export function validateStlParams(params: Record<string, string>): Record<string, string> {
  const errors: Record<string, string> = {};
  for (const key of ['period', 'seasonal', 'threshold']) {
    if (isOmitted(params, key)) continue;
    const err = validateField(key, params[key]);
    if (err) errors[key] = err;
  }
  return errors;
}

export function validateDetectorParams(
  name: DetectorName,
  params: Record<string, string>
): Record<string, string> {
  switch (name) {
    case 'rmad':
      return validateRmadParams(params);
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
