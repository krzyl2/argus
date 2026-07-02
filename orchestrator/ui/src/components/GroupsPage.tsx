import { useEffect } from 'preact/hooks';
import { route, routeGroupId } from '../router';
import { groups, loadGroups } from '../state/groups';
import { query as sensorQuery, sensors, loadSensors } from '../state/sensors';
import { GroupList } from './GroupList';
import { GroupEditorForm } from './GroupEditorForm';

// Top-level page for the /groups* family — routes internally between GroupList
// (#/groups) and GroupEditorForm (#/groups/new, #/groups/:id) based on the
// router's parsed route/id (role-match analog of SensorsPage's orchestration).
export function GroupsPage() {
  useEffect(() => {
    loadGroups();
    // Member picker needs the full sensor list — reuse the existing sensors
    // signal/loader rather than introducing a second sensor-fetch path.
    loadSensors(sensorQuery.value);
  }, []);

  const isEditor = route.value === '/groups/new' || route.value.startsWith('/groups/');
  const groupId = route.value === '/groups/new' ? null : routeGroupId.value;

  if (isEditor) {
    return <GroupEditorForm groupId={groupId} sensors={sensors.value} />;
  }

  return (
    <div>
      <div>
        <p class="argus-heading">Groups</p>
        <p class="argus-body">
          Detect anomalies across related sensors — divergence within a group, or jointly-abnormal
          combinations.
        </p>
      </div>
      <p>
        <a class="argus-btn argus-btn--primary" href="#/groups/new">
          Create group
        </a>
      </p>
      <GroupList groups={groups.value} />
    </div>
  );
}
