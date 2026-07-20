import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/preact';
import { GuidedFlowStep } from './GuidedFlowStep';
import { chooserMode, selectedDetector, guidedRecommended, catalog } from '../state/groupEditor';

function resetChooserState() {
  chooserMode.value = 'guided-question';
  selectedDetector.value = null;
  guidedRecommended.value = null;
  catalog.value = null;
}

describe('GuidedFlowStep', () => {
  beforeEach(() => {
    resetChooserState();
  });

  it('renders inside a Card with the verbatim question, two answer buttons, and a skip button', () => {
    render(<GuidedFlowStep />);

    expect(screen.getByText('What are you monitoring?').closest('.argus-card')).toBeTruthy();
    expect(screen.getByText("A room/area's related sensors, together")).toBeTruthy();
    expect(screen.getByText('Which one sensor diverges from its peers')).toBeTruthy();
    expect(screen.getByText('Skip — choose manually')).toBeTruthy();
  });

  it("clicking the first answer calls answerGuidedQuestion('together') and moves to guided-pick-shown", () => {
    render(<GuidedFlowStep />);

    fireEvent.click(screen.getByText("A room/area's related sensors, together"));

    expect(selectedDetector.value).toBe('ecod');
    expect(chooserMode.value).toBe('guided-pick-shown');
  });

  it("clicking the second answer calls answerGuidedQuestion('diverges') and moves to guided-pick-shown", () => {
    render(<GuidedFlowStep />);

    fireEvent.click(screen.getByText('Which one sensor diverges from its peers'));

    expect(selectedDetector.value).toBe('peer_divergence');
    expect(chooserMode.value).toBe('guided-pick-shown');
  });

  it('clicking skip calls skipToManual, clearing any recommendation and switching to manual mode', () => {
    render(<GuidedFlowStep />);

    fireEvent.click(screen.getByText('Skip — choose manually'));

    expect(chooserMode.value).toBe('manual');
    expect(guidedRecommended.value).toBeNull();
  });
});
