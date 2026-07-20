import { answerGuidedQuestion, skipToManual } from '../state/groupEditor';
import { Card } from './Card';
import { Button } from './Button';

// The "What are you monitoring?" question (ALGO-04) — exactly 2 answers per
// 08-UI-SPEC.md Copywriting Contract, verbatim copy. A manual skip link is always
// visible alongside the question (never forces the guided path).
export function GuidedFlowStep() {
  return (
    <Card padding="sm">
      <p class="argus-body">What are you monitoring?</p>
      <div class="argus-guided-flow-step__answers">
        <Button variant="secondary" onClick={() => answerGuidedQuestion('together')}>
          A room/area&apos;s related sensors, together
        </Button>
        <Button variant="secondary" onClick={() => answerGuidedQuestion('diverges')}>
          Which one sensor diverges from its peers
        </Button>
      </div>
      <Button variant="ghost" size="sm" onClick={skipToManual}>
        Skip — choose manually
      </Button>
    </Card>
  );
}
