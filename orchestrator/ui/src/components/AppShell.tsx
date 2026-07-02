import type { ComponentChildren } from 'preact';

interface AppShellProps {
  children: ComponentChildren;
}

// Replaces <header class="argus-header">/<footer class="argus-footer"> in BuildFullPage.
// Nav row added inside the existing flex header (08-UI-SPEC.md "Screens / Routes") —
// no new nav component, .argus-header already supports this as a flex container.
export function AppShell({ children }: AppShellProps) {
  return (
    <>
      <header class="argus-header">
        <span class="argus-heading">Argus</span>
        <nav>
          <a class="argus-label" href="#/sensors">
            Sensors
          </a>{' '}
          <a class="argus-label" href="#/groups">
            Groups
          </a>
        </nav>
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
