---
status: complete
phase: 08-group-config-ui-algorithm-chooser
source: [08-01-SUMMARY.md, 08-02-SUMMARY.md, 08-03-SUMMARY.md, 08-04-SUMMARY.md]
started: 2026-07-06T13:24:42Z
updated: 2026-07-06T13:24:42Z
---

## Current Test

[testing complete]

## Tests

### 1. Cold Start Smoke Test
expected: Kill any running orchestrator, clear ephemeral state, start from scratch. Boots without errors, connects to HA WebSocket, batch scheduler starts, SPA loads via HA "Open Web UI".
result: pass

### 2. Groups Navigation + List Screen
expected: The SPA shows a Sensors/Groups nav row. Clicking "Groups" (#/groups) shows the group list. If groups exist in config they appear as rows; if none, an empty state. No errors.
result: pass

### 3. Sensor Search by Friendly Name or Entity ID (SRCH-01)
expected: On the sensor list (or member picker), typing part of a Polish friendly_name OR part of an entity_id filters the list to matching sensors. Placeholder reads "Filter by name or entity ID…".
result: pass

### 4. Area / Domain Browse Grouping (SRCH-02)
expected: Sensors can be browsed grouped by HA area — one collapsible section per area, header "{Area} ({count})", alphabetical, with a domain/"Ungrouped" fallback section last. Area names come from the live HA area registry.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 5. Create Group + Member Picker with Validation
expected: "Create group" opens the editor. Name/mode fields present. Selecting members is multi-select. Picking fewer than 3 members surfaces the floor-3 validation error; peer-divergence mode with mixed units surfaces the unit-consistency error. Save is wired.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 6. Algorithm Chooser — Guided Flow (ALGO-04)
expected: The editor asks "What are you monitoring?" with answers. Answering pre-selects AND visibly labels the recommended detector ("Suggested based on your answer…"). The full algorithm grid stays clickable; one click on any other card overrides with no confirm dialog and clears the guided label. A "Skip — choose manually" link is always visible.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 7. Sensitivity Presets Low/Med/High (ALGO-01)
expected: Selecting Low/Med/High immediately expands that preset's params into the group draft (no round-trip). Raw param values stay hidden behind the preset by default.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 8. Advanced Params Override + "Customized" Indicator (ALGO-02)
expected: An "Advanced" disclosure reveals individual param fields. Overriding at least one field shows a "{Preset}, customized" indicator next to the preset radio (visible even when the disclosure is collapsed).
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 9. Algorithm Card "Best For" Copy (ALGO-03)
expected: Each algorithm card shows its catalog-sourced "best for…" description text. Copy is honest — never claims contamination changes the anomaly score.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 10. Save Group → Hot Reload, No Restart
expected: Saving a new/edited group persists to config (groups: replaced, entities:/_patterns: preserved) and hot-reloads live with NO HA/add-on restart. A group-specific save result banner confirms success with member count.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika (backend hot-reload do reweryfikacji po przebudowie UI)

### 11. Delete Group — Two-Click Confirm
expected: On a group row, "Delete group" arms into "Confirm delete" (staged two-click, ~3s auto-revert, no browser confirm dialog). Confirming removes the group and refreshes the list.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

### 12. Attribution Panel — Ranked / No-Attribution / No-Verdict (GRP-09)
expected: On an existing group's edit screen, the attribution panel polls status (~60s). For ecod/copod with a recent joint verdict: ranked contribution bars, top-ranked accented, in server-sorted order. For pca/iforest: honest "This algorithm does not provide per-feature attribution." For no verdict yet: "No anomaly score yet…".
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika (backend attribution/sort do reweryfikacji po przebudowie UI)

### 13. Area Suggestion Banner (SRCH-03)
expected: On #/groups, an area with ≥3 ungrouped sensors shows "{N} sensors share area "{area}" — group them?". "Review" pre-fills the /groups/new member picker (operator still edits mode/algorithm and explicitly saves — never auto-groups). "Not now" dismisses for the session.
result: skipped
reason: UI będzie mocno przebudowywany — pominięte per decyzja użytkownika

## Summary

total: 13
passed: 3
issues: 0
pending: 0
skipped: 10

## Gaps

[none yet]
