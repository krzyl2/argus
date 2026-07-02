import { answerGuidedQuestion, skipToManual } from '../state/groupEditor';

// The "What are you monitoring?" question (ALGO-04) — exactly 2 answers per
// 08-UI-SPEC.md Copywriting Contract, verbatim copy. A manual skip link is always
// visible alongside the question (never forces the guided path).
export function GuidedFlowStep() {
  return (
    <div class="argus-guided-flow-step">
      <p class="argus-body">What are you monitoring?</p>
      <div class="argus-guided-flow-step__answers">
        <button type="button" class="argus-btn" onClick={() => answerGuidedQuestion('together')}>
          A room/area&apos;s related sensors, together
        </button>
        <button type="button" class="argus-btn" onClick={() => answerGuidedQuestion('diverges')}>
          Which one sensor diverges from its peers
        </button>
      </div>
      <button type="button" class="argus-btn" onClick={skipToManual}>
        Skip — choose manually
      </button>
    </div>
  );
}
