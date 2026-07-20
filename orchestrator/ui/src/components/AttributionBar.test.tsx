import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/preact';
import { AttributionBar } from './AttributionBar';

describe('AttributionBar', () => {
  it('renders the accent fill--top modifier when topRank is true', () => {
    const { container } = render(
      <AttributionBar memberId="sensor.a" contribution={0.6} topContribution={0.6} topRank={true} />
    );

    const fill = container.querySelector('.argus-attribution-bar__fill');
    expect(fill).toBeTruthy();
    expect(fill!.classList.contains('argus-attribution-bar__fill--top')).toBe(true);
  });

  it('does not render the accent fill--top modifier when topRank is false', () => {
    const { container } = render(
      <AttributionBar memberId="sensor.b" contribution={0.3} topContribution={0.6} topRank={false} />
    );

    const fill = container.querySelector('.argus-attribution-bar__fill');
    expect(fill).toBeTruthy();
    expect(fill!.classList.contains('argus-attribution-bar__fill--top')).toBe(false);
  });

  it('computes fill width as a percentage of topContribution', () => {
    const { container } = render(
      <AttributionBar memberId="sensor.a" contribution={0.3} topContribution={0.6} topRank={false} />
    );

    const fill = container.querySelector('.argus-attribution-bar__fill') as HTMLElement;
    expect(fill.style.width).toBe('50%');
  });

  it('clamps fill width to 100% when contribution exceeds topContribution', () => {
    const { container } = render(
      <AttributionBar memberId="sensor.a" contribution={0.9} topContribution={0.6} topRank={true} />
    );

    const fill = container.querySelector('.argus-attribution-bar__fill') as HTMLElement;
    expect(fill.style.width).toBe('100%');
  });

  it('renders 0% width when topContribution is 0', () => {
    const { container } = render(
      <AttributionBar memberId="sensor.a" contribution={0} topContribution={0} topRank={false} />
    );

    const fill = container.querySelector('.argus-attribution-bar__fill') as HTMLElement;
    expect(fill.style.width).toBe('0%');
  });

  it('renders the memberId label and the contribution formatted to 3 decimals', () => {
    const { container } = render(
      <AttributionBar memberId="sensor.living_room" contribution={0.123456} topContribution={0.6} topRank={false} />
    );

    expect(container.querySelector('.argus-attribution-bar__label')!.textContent).toBe('sensor.living_room');
    expect(container.querySelector('.argus-attribution-bar__value')!.textContent).toBe('0.123');
  });
});
