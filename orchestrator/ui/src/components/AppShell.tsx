import type { ComponentChildren } from 'preact';

interface AppShellProps {
  children: ComponentChildren;
}

// Replaces <header class="argus-header">/<footer class="argus-footer"> in BuildFullPage.
export function AppShell({ children }: AppShellProps) {
  return (
    <>
      <header class="argus-header">
        <span class="argus-heading">Argus</span>
      </header>
      <main class="argus-main" style={{ maxWidth: '880px' }}>
        {children}
      </main>
      <footer class="argus-footer">
        <span class="argus-label">Argus</span>
      </footer>
    </>
  );
}
