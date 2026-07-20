import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/preact';
import { AlgorithmChooser } from './AlgorithmChooser';
import * as client from '../api/client';
import { draftDetector, draftParams, draftPresetLabel, draftMode } from '../state/groups';
import { chooserMode, selectedDetector, guidedRecommended, catalog } from '../state/groupEditor';
import type { DetectorCatalog } from '../api/types';

function fakeCatalog(): DetectorCatalog {
  return {
    detectors: [
      {
        name: 'ecod',
        bestFor: 'Detecting a room/area acting abnormally as a whole.',
        presets: [
          { label: 'Low', params: { contamination: '0.05' } },
          { label: 'Med', params: { contamination: '0.1' } },
          { label: 'High', params: { contamination: '0.2' } },
        ],
        paramSchema: [{ key: 'contamination', type: 'number', min: 0, max: 0.5, step: '0.01' }],
      },
      {
        name: 'peer_divergence',
        bestFor: 'Finding the one sensor that disagrees with its peers.',
        presets: [
          { label: 'Low', params: { threshold: '4.5' } },
          { label: 'Med', params: { threshold: '3.5' } },
          { label: 'High', params: { threshold: '2.5' } },
        ],
        paramSchema: [{ key: 'threshold', type: 'number', min: 0, max: null, step: '0.1' }],
      },
    ],
    guided: [
      { answer: 'together', detector: 'ecod' },
      { answer: 'diverges', detector: 'peer_divergence' },
    ],
  };
}

function resetAll() {
  chooserMode.value = 'guided-question';
  selectedDetector.value = null;
  guidedRecommended.value = null;
  catalog.value = null;
  draftDetector.value = null;
  draftParams.value = {};
  draftPresetLabel.value = null;
  draftMode.value = 'peer_divergence';
}

describe('AlgorithmChooser', () => {
  beforeEach(() => {
    resetAll();
    vi.spyOn(client, 'apiGet').mockResolvedValue(fakeCatalog());
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('opens in guided mode showing the question and a manual skip link', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    expect(screen.getByText('Skip — choose manually')).toBeTruthy();
  });

  it('answering the guided question shows the recommended card pre-selected and labeled, with the full grid still visible', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    fireEvent.click(screen.getByText("A room/area's related sensors, together"));

    await waitFor(() => {
      expect(screen.getByText('Suggested based on your answer — you can pick a different algorithm below.')).toBeTruthy();
    });
    // Full grid remains visible/clickable — both cards render.
    expect(screen.getByText('ecod')).toBeTruthy();
    expect(screen.getByText('peer_divergence')).toBeTruthy();
    expect(draftDetector.value).toBe('ecod');
  });

  it('one click on a different card overrides the guided pick with zero friction (no confirm)', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    fireEvent.click(screen.getByText("A room/area's related sensors, together"));
    await waitFor(() => expect(draftDetector.value).toBe('ecod'));

    fireEvent.click(screen.getByText('peer_divergence'));

    await waitFor(() => expect(draftDetector.value).toBe('peer_divergence'));
    expect(screen.queryByText('Suggested based on your answer — you can pick a different algorithm below.')).toBeNull();
  });

  it('renders each card\'s catalog-sourced bestFor description (ALGO-03)', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    fireEvent.click(screen.getByText('Skip — choose manually'));

    await screen.findByText('Detecting a room/area acting abnormally as a whole.');
    expect(screen.getByText('Finding the one sensor that disagrees with its peers.')).toBeTruthy();
  });

  it('selecting a preset expands its catalog params into the draft (no hardcoded client numbers)', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    fireEvent.click(screen.getByText('Skip — choose manually'));
    fireEvent.click(await screen.findByText('ecod'));

    await waitFor(() => expect(draftParams.value.contamination).toBe('0.1')); // Med default

    const lowRadio = (await screen.findAllByRole('radio')).find(
      (el) => (el as HTMLInputElement).value === 'Low'
    ) as HTMLInputElement;
    fireEvent.click(lowRadio);

    await waitFor(() => expect(draftParams.value.contamination).toBe('0.05'));
  });

  it('editing an Advanced field overrides that key while the preset radio stays selected, showing a customized indicator', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    fireEvent.click(screen.getByText('Skip — choose manually'));
    fireEvent.click(await screen.findByText('ecod'));
    await waitFor(() => expect(draftParams.value.contamination).toBe('0.1'));

    fireEvent.click(screen.getByText('Advanced — view/override parameters'));
    const input = await screen.findByLabelText('contamination');
    fireEvent.input(input, { target: { value: '0.42' } });

    await waitFor(() => expect(draftParams.value.contamination).toBe('0.42'));
    expect(screen.getByText('Med, customized')).toBeTruthy();

    const medRadio = (screen.getAllByRole('radio') as HTMLInputElement[]).find((el) => el.value === 'Med')!;
    expect(medRadio.checked).toBe(true);
  });

  it('pre-selects an existing detector directly into the manual grid, skipping the guided question', async () => {
    render(<AlgorithmChooser existingDetector="pca" />);

    await waitFor(() => expect(chooserMode.value).toBe('manual'));
    expect(screen.queryByText('What are you monitoring?')).toBeNull();
  });

  it('renders the "Algorithm" section-label heading', async () => {
    render(<AlgorithmChooser existingDetector={null} />);

    await screen.findByText('What are you monitoring?');
    expect(screen.getByText('Algorithm')).toBeTruthy();
  });

  it('shows the full unfiltered catalog and keeps the guided step available in BOTH peer_divergence and joint modes (D-03 regression guard)', async () => {
    draftMode.value = 'peer_divergence';
    const peerRender = render(<AlgorithmChooser existingDetector={null} />);

    await peerRender.findByText('What are you monitoring?'); // guided step reachable in peer mode
    fireEvent.click(peerRender.getByText('Skip — choose manually'));
    await peerRender.findByText('ecod');
    const peerCardCount = peerRender.getAllByRole('radio').length;
    expect(peerCardCount).toBe(2); // full catalog, no mode filter
    peerRender.unmount();

    resetAll();
    draftMode.value = 'joint';
    const jointRender = render(<AlgorithmChooser existingDetector={null} />);

    await jointRender.findByText('What are you monitoring?'); // guided step still reachable in joint mode
    fireEvent.click(jointRender.getByText('Skip — choose manually'));
    await jointRender.findByText('ecod');
    const jointCardCount = jointRender.getAllByRole('radio').length;
    expect(jointCardCount).toBe(peerCardCount); // identical card count across both modes
  });
});
