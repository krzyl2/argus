import { useEffect } from 'preact/hooks';
import type { GroupDetectorName } from '../api/types';
import { draftDetector, draftParams, draftPresetLabel } from '../state/groups';
import {
  catalog,
  chooserMode,
  selectedDetector,
  guidedRecommended,
  loadCatalog,
  pickAlgorithmManually,
  resetChooser,
  loadChooserFromDetector,
} from '../state/groupEditor';
import { GuidedFlowStep } from './GuidedFlowStep';
import { AlgorithmCard } from './AlgorithmCard';
import { SensitivityPresetPicker } from './SensitivityPresetPicker';
import { AdvancedParamsDisclosure } from './AdvancedParamsDisclosure';

interface AlgorithmChooserProps {
  // Present when editing an existing group whose detector was already chosen in a prior
  // session — skips straight to the manual grid with that detector pre-selected, matching
  // GroupEditorForm's loadDraftFromGroup effect.
  existingDetector: string | null;
}

// Top-level chooser: guided-flow entry point OR manual algorithm card grid (ALGO-01..04).
// Loads the catalog once; orchestrates GuidedFlowStep vs the AlgorithmCard grid per the
// 08-UI-SPEC.md AlgorithmChooser states table. When a detector is selected, mounts
// SensitivityPresetPicker + AdvancedParamsDisclosure below the selected card.
//
// state/groupEditor.ts's selectedDetector is the chooser's own state-machine signal (mirrors
// RESEARCH Pattern 4 verbatim, independently testable); this component is the single place
// that mirrors it into state/groups.ts's draftDetector/draftParams, which is what saveGroup()
// actually persists. A guided answer or a manual pick both flow through selectedDetector,
// so one effect keeps the draft in sync regardless of which path set it.
export function AlgorithmChooser({ existingDetector }: AlgorithmChooserProps) {
  useEffect(() => {
    loadCatalog();
  }, []);

  useEffect(() => {
    if (existingDetector) {
      loadChooserFromDetector(existingDetector as Parameters<typeof loadChooserFromDetector>[0]);
    } else {
      resetChooser();
    }
    // Only re-run when switching between editor sessions (existingDetector identity change).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [existingDetector]);

  // Mirror the chooser's selectedDetector into the draft whenever it changes (guided answer
  // or manual pick) — but do not clobber an existing group's already-loaded params when the
  // effect simply re-syncs the same detector it was seeded with.
  useEffect(() => {
    const next = selectedDetector.value;
    if (next && next !== draftDetector.value) {
      draftDetector.value = next;
      draftParams.value = {};
      draftPresetLabel.value = null;
    }
  }, [selectedDetector.value]);

  const cat = catalog.value;
  if (!cat) {
    return <p class="argus-label">Loading algorithms…</p>;
  }

  const selected = draftDetector.value;
  const selectedEntry = selected ? cat.detectors.find((d) => d.name === selected) : undefined;

  return (
    <div class="argus-algorithm-chooser">
      {chooserMode.value === 'guided-question' && <GuidedFlowStep />}

      {chooserMode.value !== 'guided-question' && (
        <div class="argus-algorithm-chooser__grid" role="radiogroup" aria-label="Algorithm">
          {cat.detectors.map((entry) => (
            <AlgorithmCard
              key={entry.name}
              name={entry.name}
              bestFor={entry.bestFor}
              selected={selected === entry.name}
              recommended={guidedRecommended.value === entry.name}
              onSelect={(name) => pickAlgorithmManually(name as GroupDetectorName)}
            />
          ))}
        </div>
      )}

      {selectedEntry && (
        <>
          <SensitivityPresetPicker entry={selectedEntry} />
          <AdvancedParamsDisclosure entry={selectedEntry} />
        </>
      )}
    </div>
  );
}
