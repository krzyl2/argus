import { render } from 'preact';
import './router';
import { route } from './router';
import { AppShell } from './components/AppShell';
import { SensorsPage } from './components/SensorsPage';

function App() {
  // route.value is reactive; this phase ships exactly one real route.
  return (
    <AppShell>
      {route.value === '/sensors' ? <SensorsPage /> : <SensorsPage />}
    </AppShell>
  );
}

const mountEl = document.getElementById('app');
if (mountEl) {
  render(<App />, mountEl);
}
