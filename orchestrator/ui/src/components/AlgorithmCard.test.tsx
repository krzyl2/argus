import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { AlgorithmCard } from './AlgorithmCard';

describe('AlgorithmCard', () => {
  it('renders a plain-string name and bestFor caption', () => {
    render(
      <AlgorithmCard name="hst" bestFor="Streaming — reacts within ~2 s." selected={false} recommended={false} onSelect={vi.fn()} />
    );

    expect(screen.getByText('hst')).toBeTruthy();
    expect(screen.getByText('Streaming — reacts within ~2 s.')).toBeTruthy();
  });

  it('carries the argus-algorithm-card--selected class and aria-checked=true when selected (SC3 — border-class-driven, not color alone)', () => {
    render(
      <AlgorithmCard name="mad" bestFor="Batch — robust median-based outlier detection." selected recommended={false} onSelect={vi.fn()} />
    );

    const card = screen.getByRole('radio');
    expect(card.className).toContain('argus-algorithm-card--selected');
    expect(card.getAttribute('aria-checked')).toBe('true');
  });

  it('does not carry the selected class when not selected', () => {
    render(
      <AlgorithmCard name="stl" bestFor="Batch — seasonal/trend decomposition." selected={false} recommended={false} onSelect={vi.fn()} />
    );

    const card = screen.getByRole('radio');
    expect(card.className).not.toContain('argus-algorithm-card--selected');
    expect(card.getAttribute('aria-checked')).toBe('false');
  });

  it('calls onSelect with the string name when clicked', () => {
    const onSelect = vi.fn();
    render(
      <AlgorithmCard name="hst" bestFor="Streaming." selected={false} recommended={false} onSelect={onSelect} />
    );

    fireEvent.click(screen.getByRole('radio'));

    expect(onSelect).toHaveBeenCalledWith('hst');
  });

  it('renders the guided-suggestion label when recommended is true', () => {
    render(
      <AlgorithmCard name="hst" bestFor="Streaming." selected={false} recommended onSelect={vi.fn()} />
    );

    expect(
      screen.getByText('Suggested based on your answer — you can pick a different algorithm below.')
    ).toBeTruthy();
  });

  it('does not render the guided-suggestion label when recommended is false', () => {
    render(
      <AlgorithmCard name="hst" bestFor="Streaming." selected={false} recommended={false} onSelect={vi.fn()} />
    );

    expect(
      screen.queryByText('Suggested based on your answer — you can pick a different algorithm below.')
    ).toBeNull();
  });
});
