import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { SaveResultBanner } from './SaveResultBanner';
import type { SaveResponse } from '../api/types';

describe('SaveResultBanner', () => {
  it('renders success banner with count and entities plural', () => {
    const result: SaveResponse = { ok: true, count: 3, hasStreaming: false };
    const { container } = render(<SaveResultBanner result={result} />);
    expect(container.querySelector('.argus-banner--success')).not.toBeNull();
    expect(container.textContent).toMatch(/Saved — pipeline active\. 3 entities tracked\./);
  });

  it('renders success banner with singular entity for count=1', () => {
    const result: SaveResponse = { ok: true, count: 1, hasStreaming: false };
    const { container } = render(<SaveResultBanner result={result} />);
    expect(container.textContent).toMatch(/1 entity tracked\./);
  });

  it('appends the warm-up note only when a streaming detector is present', () => {
    const withStreaming: SaveResponse = { ok: true, count: 1, hasStreaming: true };
    const { container: c1 } = render(<SaveResultBanner result={withStreaming} />);
    expect(c1.querySelector('.argus-warmup-note')).not.toBeNull();

    const withoutStreaming: SaveResponse = { ok: true, count: 1, hasStreaming: false };
    const { container: c2 } = render(<SaveResultBanner result={withoutStreaming} />);
    expect(c2.querySelector('.argus-warmup-note')).toBeNull();
  });

  // The three numbers in the old copy ("HST", "window=250", "~4 minutes") are all false after
  // the migration, and the last one is off by up to two orders of magnitude on a slow sensor
  // (391 s per reading times 60 min_samples is ~6,5 h, not 4 minutes). An operator who reads
  // "4 minutes" and sees nothing an hour later concludes the add-on is broken.
  it('does not promise a warm-up time the detector cannot keep', () => {
    const result: SaveResponse = { ok: true, count: 1, hasStreaming: true };
    const { container } = render(<SaveResultBanner result={result} />);
    const note = container.querySelector('.argus-warmup-note')!.textContent!;
    expect(note).not.toMatch(/window=250/);
    expect(note).not.toMatch(/4 minutes/);
    expect(note).toMatch(/min_samples/);
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
