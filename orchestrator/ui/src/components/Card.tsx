import type { ComponentChildren } from 'preact';

export interface CardProps {
  padding?: 'none' | 'sm' | 'md';
  interactive?: boolean;
  children: ComponentChildren;
}

// Generic flat surface container over .argus-card. `padding` is reserved for a future
// spacing modifier — .argus-card's default padding (--space-lg) covers 'md' today; 'none'/'sm'
// are accepted in the prop shape per the DS API but have no dedicated modifier class yet in
// argus.css (Plan 10-01 did not add one), so they currently render identically to 'md'.
export function Card({ padding = 'md', interactive = false, children }: CardProps) {
  void padding;
  return (
    <div class={`argus-card${interactive ? ' argus-card--interactive' : ''}`}>
      {children}
    </div>
  );
}
