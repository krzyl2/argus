import { describe, it, expect } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
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
  // Fixtures start from the server default table and override ONLY the key under test, so a
  // case cannot pass or fail for a reason unrelated to the rule it exercises.
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

  // An OMITTED key is not an empty field. It means "use the server default" -- the same thing
  // it means to RmadParams.From and to InputValidator, which validates the submitted keys
  // layered over DetectorDefaults. `params: {}` is what a fresh install stores for every
  // entity, and reporting an error on it disabled Save for the WHOLE screen (validationErrors
  // aggregates over every tracked entity) with no field visibly wrong.
  it('reports nothing for a params block that omits every key', () => {
    expect(validateRmadParams({})).toEqual({});
    expect(validateHstParams({})).toEqual({});
    expect(validateMadParams({})).toEqual({});
    expect(validateStlParams({})).toEqual({});
  });

  it('reports nothing for a partial block, on the keys it omits', () => {
    expect(validateRmadParams({ window: '240' })).toEqual({});
  });

  // The half of the rule that must NOT be relaxed: a key that exists and is blank is a field
  // the operator cleared, and saving it would write an unparsable value.
  it('still requires a value on a key that is present and blank', () => {
    expect(validateRmadParams({ z_scale: '' }).z_scale).toBe('Must provide a value.');
    expect(validateRmadParams({ z_scale: '   ' }).z_scale).toBe('Must provide a value.');
    expect(validateRmadParams({ z_scale: 'abc' }).z_scale).toBe('Must provide a value.');
  });

  // A present key is still range-checked, omissions around it notwithstanding.
  it('still range-checks a key that is present in an otherwise empty block', () => {
    expect(validateRmadParams({ window: '5' }).window).toBe(
      'Must be a whole number between 30 and 10000.'
    );
  });
});

// WR-02 / N2. The one server rule that had no client mirror: an rmad block carrying the
// HST-only `n_trees` key. Since D-N the editor hydrates params straight off disk, so a
// hand-edited entities.yaml reaches the form intact — and every key such a block shares with
// rmad is individually in range, so without an explicit rule the browser reported "valid" and
// the server rejected the Save with a message the UI could not attach to any field.
describe('validateRmadParams rejects a non-migrated (HST-shaped) block', () => {
  const LEGACY_MSG = 'Parameter "n_trees" belongs to HST, not RMAD — this block was not migrated.';

  it('flags n_trees even when every other key is a legal rmad value', () => {
    const legacy = {
      window: '250',
      n_trees: '25',
      high_threshold: '0.7',
      low_threshold: '0.3',
      min_consecutive: '3',
      frozen_window: '10',
      frozen_variance_threshold: '0.001',
    };

    expect(validateRmadParams(legacy).n_trees).toBe(LEGACY_MSG);
    expect(hasAnyError(validateRmadParams(legacy))).toBe(true);
  });

  it('flags n_trees through validateDetectorParams, the entry point the screens call', () => {
    expect(validateDetectorParams('rmad', { n_trees: '25' }).n_trees).toBe(LEGACY_MSG);
  });

  // The rule is rmad-only: n_trees is a legitimate HST key and must stay legal there.
  it('leaves n_trees alone on an hst block', () => {
    expect(validateHstParams({ n_trees: '25' })).toEqual({});
    expect(validateDetectorParams('hst', { n_trees: '25' })).toEqual({});
  });
});

// Parity pin. The four rmad message strings live in TWO files by necessity (C# validates the
// POST body, TS validates the form), and a drift between them is invisible until an operator
// hits a Save that the browser had called valid. These read the server's constants off disk
// and assert the client produces the SAME text at runtime — so editing InputValidator.cs
// without editing detectorParams.ts turns red here rather than in production.
describe('C#/TS message parity (InputValidator.cs <-> detectorParams.ts)', () => {
  // Walked up from the working directory rather than resolved off import.meta.url: vitest
  // transforms this module, so import.meta.url is not a file:// URL here.
  function findServerSource(): string {
    const rel = join('orchestrator', 'Argus.Orchestrator', 'Config', 'InputValidator.cs');
    for (let dir = process.cwd(); ; dir = dirname(dir)) {
      const candidate = join(dir, rel);
      if (existsSync(candidate)) return candidate;
      if (dirname(dir) === dir) throw new Error(`could not locate ${rel} above ${process.cwd()}`);
    }
  }

  const csharp = readFileSync(findServerSource(), 'utf8');

  /**
   * Reads the value of `internal const string NAME = "...";` out of the server source,
   * unescaping the C# literal by hand so no regex escaping sits between the two files.
   */
  function serverMessage(name: string): string {
    const at = csharp.indexOf(`internal const string ${name}`);
    if (at < 0) throw new Error(`InputValidator.cs no longer declares ${name}`);
    const open = csharp.indexOf('"', at);
    let out = '';
    for (let i = open + 1; i < csharp.length; i++) {
      const ch = csharp[i];
      if (ch === '\\') {
        out += csharp[++i];
        continue;
      }
      if (ch === '"') return out;
      out += ch;
    }
    throw new Error(`InputValidator.cs has an unterminated literal for ${name}`);
  }

  // A UTF-8 BOM in front of `using` is invisible in an editor and harmless to the compiler,
  // but it is one more byte the string extraction above has to survive, and it makes the file
  // compare unequal to every other source in the folder. It was added by hand; keep it out.
  it('InputValidator.cs carries no UTF-8 BOM', () => {
    expect(csharp.charCodeAt(0)).not.toBe(0xfeff);
  });

  it('MSG_RMAD_LEGACY_N_TREES matches', () => {
    expect(validateRmadParams({ n_trees: '25' }).n_trees).toBe(
      serverMessage('MSG_RMAD_LEGACY_N_TREES')
    );
  });

  it('MSG_WINDOW_RANGE matches', () => {
    expect(validateRmadParams({ window: '5' }).window).toBe(serverMessage('MSG_WINDOW_RANGE'));
  });

  it('MSG_MIN_SAMPLES matches', () => {
    expect(validateRmadParams({ min_samples: '1' }).min_samples).toBe(
      serverMessage('MSG_MIN_SAMPLES')
    );
  });

  it('MSG_MIN_SAMPLES_LE_WINDOW matches', () => {
    expect(validateRmadParams({ window: '100', min_samples: '200' }).min_samples).toBe(
      serverMessage('MSG_MIN_SAMPLES_LE_WINDOW')
    );
  });
});
