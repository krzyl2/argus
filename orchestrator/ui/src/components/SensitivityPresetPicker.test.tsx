import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { SensitivityPresetPicker } from './SensitivityPresetPicker';
import { draftParams, draftPresetLabel } from '../state/groups';
import type { DetectorCatalogEntry } from '../api/types';

// Regression guard (D-07): SensitivityPresetPicker itself is not modified by this plan —
// this test file exists purely to lock in its unchanged Med-default/isCustomized behavior.
function fakeEntry(): DetectorCatalogEntry {
  return {
    name: 'ecod',
    bestFor: 'Detecting a room/area acting abnormally as a whole.',
    presets: [
      { label: 'Low', params: { contamination: '0.05' } },
      { label: 'Med', params: { contamination: '0.1' } },
      { label: 'High', params: { contamination: '0.2' } },
    ],
    paramSchema: [{ key: 'contamination', type: 'number', min: 0, max: 0.5, step: '0.01' }],
  };
}

describe('SensitivityPresetPicker', () => {
  beforeEach(() => {
    draftParams.value = {};
    draftPresetLabel.value = null;
  });

  it('defaults to the Med preset and expands its params into the draft', async () => {
    render(<SensitivityPresetPicker entry={fakeEntry()} />);

    const medRadio = (await screen.findAllByRole('radio')).find(
      (el) => (el as HTMLInputElement).value === 'Med'
    ) as HTMLInputElement;
    expect(medRadio.checked).toBe(true);
    expect(draftParams.value.contamination).toBe('0.1');
  });

  it('shows the "Med, customized" indicator once a param diverges from the active preset', async () => {
    render(<SensitivityPresetPicker entry={fakeEntry()} />);

    await screen.findAllByRole('radio');
    draftParams.value = { ...draftParams.value, contamination: '0.42' };

    expect(await screen.findByText('Med, customized')).toBeTruthy();
  });

  it('selecting a different preset updates the active label and re-expands its params (no customized indicator)', async () => {
    render(<SensitivityPresetPicker entry={fakeEntry()} />);

    const lowRadio = (await screen.findAllByRole('radio')).find(
      (el) => (el as HTMLInputElement).value === 'Low'
    ) as HTMLInputElement;
    fireEvent.click(lowRadio);

    expect(draftParams.value.contamination).toBe('0.05');
    expect(draftPresetLabel.value).toBe('Low');
    expect(screen.queryByText('Low, customized')).toBeNull();
  });
});
