import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { SaveResultBanner } from './SaveResultBanner';
import type { SaveResponse } from '../api/types';

describe('SaveResultBanner', () => {
  it('renders success banner with count and entities plural', () => {
    const result: SaveResponse = { ok: true, count: 3, hasHst: false };
    const { container } = render(<SaveResultBanner result={result} />);
    expect(container.querySelector('.argus-banner--success')).not.toBeNull();
    expect(container.textContent).toMatch(/Saved — pipeline active\. 3 entities tracked\./);
  });

  it('renders success banner with singular entity for count=1', () => {
    const result: SaveResponse = { ok: true, count: 1, hasHst: false };
    const { container } = render(<SaveResultBanner result={result} />);
    expect(container.textContent).toMatch(/1 entity tracked\./);
  });

  it('appends HST warm-up note only when hasHst is true', () => {
    const withHst: SaveResponse = { ok: true, count: 1, hasHst: true };
    const { container: c1 } = render(<SaveResultBanner result={withHst} />);
    expect(c1.querySelector('.argus-warmup-note')).not.toBeNull();

    const withoutHst: SaveResponse = { ok: true, count: 1, hasHst: false };
    const { container: c2 } = render(<SaveResultBanner result={withoutHst} />);
    expect(c2.querySelector('.argus-warmup-note')).toBeNull();
  });

  it('renders validation banner for kind=validation', () => {
    const result: SaveResponse = { ok: false, kind: 'validation', errorCount: 2 };
    const { container } = render(<SaveResultBanner result={result} />);
    expect(container.querySelector('.argus-banner--validation')).not.toBeNull();
    expect(container.textContent).toMatch(/Save blocked: 2 field\(s\) have invalid values\./);
  });

  it('renders error banner for kind=error with reason', () => {
    const result: SaveResponse = { ok: false, kind: 'error', reason: 'disk error' };
    const { container } = render(<SaveResultBanner result={result} />);
    expect(container.querySelector('.argus-banner--error')).not.toBeNull();
    expect(container.textContent).toMatch(/Save failed\. disk error\. Check the add-on log for details\./);
  });
});
