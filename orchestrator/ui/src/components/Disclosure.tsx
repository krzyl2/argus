import type { ComponentChildren } from 'preact';

export interface DisclosureProps {
  summary: ComponentChildren;
  open?: boolean;
  children: ComponentChildren;
}

// Native <details>/<summary> disclosure — the ▶ marker + rotation are handled entirely by the
// .argus-disclosure-toggle CSS (::before + details[open] selector), so no JS-controlled
// expand/collapse state is needed here, matching DetectorDisclosure/AdvancedParamsDisclosure's
// existing native-<details> usage.
export function Disclosure({ summary, open = false, children }: DisclosureProps) {
  return (
    <details open={open}>
      <summary class="argus-disclosure-toggle">{summary}</summary>
      {children}
    </details>
  );
}
