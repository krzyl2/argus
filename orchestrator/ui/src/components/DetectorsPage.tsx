import { useEffect } from 'preact/hooks';
import { loadGroups, loadGroupStatuses } from '../state/groups';
import { loadSensors } from '../state/sensors';
import { detectorRows } from '../state/detectors';
import { DetectorList } from './DetectorList';

// D-03/DET-01: the /detectors list screen — loads both sources (groups + the full
// sensor set) on mount and renders the unified DetectorList from the detectorRows
// computed signal. Pure list + header + CTA; editors live on their own routes
// (#/groups/:id, #/detectors/sensor/:id) per D-03/D-05, so there is no internal
// editor branch here (unlike GroupsPage).
export function DetectorsPage() {
  useEffect(() => {
    // Load groups first, then their statuses (loadGroupStatuses reads the just-loaded
    // groups list). loadGroups isn't awaited at the top level, so wrap in an async IIFE.
    (async () => {
      await loadGroups();
      loadGroupStatuses();
    })();
    // D-07: load the full sensor set (empty query), never a partial one — this both
    // feeds the unified merge and satisfies the full-list-replace save guard for
    // any downstream editor that tracks/saves after visiting this list. Mirrors
    // GroupsPage's existing mount-load precedent.
    loadSensors('');

    // QUICK-warmup-status: light 5s polling so warm-up reading counts advance live
    // with no manual refresh. Sensors only (full-set, empty query) — loadGroups is
    // out of scope for this indicator.
    const sensorPollId = setInterval(() => {
      loadSensors('');
    }, 5000);

    // Group status poll: separate, slower cadence (~30s) — refresh the groups list and
    // their statuses so group rows reflect the latest verdict without a manual reload.
    const groupPollId = setInterval(() => {
      (async () => {
        await loadGroups();
        loadGroupStatuses();
      })();
    }, 30000);

    return () => {
      clearInterval(sensorPollId);
      clearInterval(groupPollId);
    };
  }, []);

  return (
    <div>
      <header class="argus-page-header">
        <h1 class="argus-page-header__title">Detectors</h1>
        <p class="argus-page-header__subtitle">
          One list of everything Argus watches — groups and individually tracked sensors.
        </p>
      </header>
      <p>
        <a class="argus-btn argus-btn--primary" href="#/detectors/add">
          Add detector
        </a>
      </p>
      <DetectorList rows={detectorRows.value} />
    </div>
  );
}
