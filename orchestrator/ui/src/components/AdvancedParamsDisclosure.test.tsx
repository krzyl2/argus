import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { AdvancedParamsDisclosure } from './AdvancedParamsDisclosure';
import { draftParams } from '../state/groups';
import type { DetectorCatalogEntry } from '../api/types';

function fakeEntry(): DetectorCatalogEntry {
  return {
    name: 'peer_divergence',
    bestFor: 'Finding the one sensor that disagrees with its peers.',
    presets: [
      { label: 'Low', params: { threshold: '4.5' } },
      { label: 'Med', params: { threshold: '3.5' } },
      { label: 'High', params: { threshold: '2.5' } },
    ],
    paramSchema: [
      { key: 'threshold', type: 'number', min: 0, max: null, step: '0.1' },
      { key: 'window', type: 'number', min: 1, max: null, step: null },
    ],
  };
}

describe('AdvancedParamsDisclosure', () => {
  beforeEach(() => {
    draftParams.value = { threshold: '3.5', window: '20' };
  });

  it('renders each schema field via the shared Input with the correct id and current value', () => {
    render(<AdvancedParamsDisclosure entry={fakeEntry()} />);
    fireEvent.click(screen.getByText('Advanced — view/override parameters'));

    const thresholdInput = screen.getByLabelText('threshold') as HTMLInputElement;
    expect(thresholdInput.id).toBe('group-param-peer_divergence-threshold');
    expect(thresholdInput.value).toBe('3.5');
    expect(thresholdInput.type).toBe('number');

    const windowInput = screen.getByLabelText('window') as HTMLInputElement;
    expect(windowInput.id).toBe('group-param-peer_divergence-window');
    expect(windowInput.value).toBe('20');
  });

  it('editing a field calls updateParam(field.key, value) and lands in draftParams without touching other keys', () => {
    render(<AdvancedParamsDisclosure entry={fakeEntry()} />);
    fireEvent.click(screen.getByText('Advanced — view/override parameters'));

    const thresholdInput = screen.getByLabelText('threshold') as HTMLInputElement;
    fireEvent.input(thresholdInput, { target: { value: '5.0' } });

    expect(draftParams.value.threshold).toBe('5.0');
    expect(draftParams.value.window).toBe('20');
  });
});
