import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { DetectorEntry } from './DetectorEntry';
import type { DetectorEntry as DetectorEntryModel } from '../api/types';

function makeDetector(overrides: Partial<DetectorEntryModel> = {}): DetectorEntryModel {
  return {
    name: 'hst',
    params: {},
    ...overrides,
  };
}

const noop = () => {};

describe('DetectorEntry type picker (Select -> AlgorithmCard radiogroup, D-01/D-02)', () => {
  it('renders a role="radiogroup" containing three AlgorithmCards (hst/mad/stl)', () => {
    render(
      <DetectorEntry
        entityIdx={0}
        detIdx={0}
        detector={makeDetector()}
        onTypeChange={noop}
        onParamChange={noop}
        onRemove={noop}
      />
    );

    const group = screen.getByRole('radiogroup');
    expect(group).toBeTruthy();
    const cards = screen.getAllByRole('radio');
    expect(cards).toHaveLength(3);
  });

  it('marks the card matching the current detector name as selected (SC3 — 2px accent border class)', () => {
    render(
      <DetectorEntry
        entityIdx={0}
        detIdx={0}
        detector={makeDetector({ name: 'mad' })}
        onTypeChange={noop}
        onParamChange={noop}
        onRemove={noop}
      />
    );

    const cards = screen.getAllByRole('radio');
    const selectedCard = cards.find((c) => c.className.includes('argus-algorithm-card--selected'));
    expect(selectedCard).toBeTruthy();
    expect(selectedCard?.getAttribute('aria-checked')).toBe('true');
    expect(selectedCard?.textContent).toContain('mad');
  });

  it('clicking a non-selected card calls onTypeChange with that card\'s hst/mad/stl value', () => {
    const onTypeChange = vi.fn();
    render(
      <DetectorEntry
        entityIdx={0}
        detIdx={0}
        detector={makeDetector({ name: 'hst' })}
        onTypeChange={onTypeChange}
        onParamChange={noop}
        onRemove={noop}
      />
    );

    const cards = screen.getAllByRole('radio');
    const stlCard = cards.find((c) => c.textContent?.includes('stl'));
    expect(stlCard).toBeTruthy();
    fireEvent.click(stlCard as HTMLElement);

    expect(onTypeChange).toHaveBeenCalledWith('stl');
  });

  it('does not import/render the old Select element', () => {
    const { container } = render(
      <DetectorEntry
        entityIdx={0}
        detIdx={0}
        detector={makeDetector()}
        onTypeChange={noop}
        onParamChange={noop}
        onRemove={noop}
      />
    );

    expect(container.querySelector('select')).toBeNull();
  });

  it('still renders the Remove button and forwards onRemove', () => {
    const onRemove = vi.fn();
    render(
      <DetectorEntry
        entityIdx={0}
        detIdx={0}
        detector={makeDetector()}
        onTypeChange={noop}
        onParamChange={noop}
        onRemove={onRemove}
      />
    );

    fireEvent.click(screen.getByText('Remove'));
    expect(onRemove).toHaveBeenCalledTimes(1);
  });
});
