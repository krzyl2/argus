import { render } from 'preact';
import './router';
import { route, routeSensorEntityId } from './router';
import { AppShell } from './components/AppShell';
import { GroupsPage } from './components/GroupsPage';
import { DashboardPage } from './components/DashboardPage';
import { AlgorithmsPage } from './components/AlgorithmsPage';
import { SettingsPage } from './components/SettingsPage';
import { DetectorsPage } from './components/DetectorsPage';
import { AddDetectorWizard } from './components/AddDetectorWizard';
import { SingleDetectorEditorForm } from './components/SingleDetectorEditorForm';

function App() {
  // route.value is reactive.
  const isGroupsRoute = route.value === '/groups' || route.value.startsWith('/groups/');
  let page;
  if (route.value === '/dashboard') {
    page = <DashboardPage />;
  } else if (route.value === '/algorithms') {
    page = <AlgorithmsPage />;
  } else if (route.value === '/settings') {
    page = <SettingsPage />;
  } else if (route.value === '/detectors/add') {
    page = <AddDetectorWizard />;
  } else if (route.value.startsWith('/detectors/sensor/')) {
    page = <SingleDetectorEditorForm entityId={routeSensorEntityId.value ?? ''} />;
  } else if (route.value === '/detectors') {
    page = <DetectorsPage />;
  } else if (isGroupsRoute) {
    page = <GroupsPage />;
  } else {
    // D-05: fallback replaces SensorsPage — /detectors is the new default route.
    page = <DetectorsPage />;
  }
  return <AppShell>{page}</AppShell>;
}

// Theme bootstrap (localStorage / prefers-color-scheme -> data-theme) now
// lives in state/theme.ts (D-09) — it runs as a side effect of the
// AppShell -> Sidebar -> state/theme import chain above, before render().

const mountEl = document.getElementById('app');
if (mountEl) {
  render(<App />, mountEl);
}
