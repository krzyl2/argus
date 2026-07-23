---
quick_id: 260723-oik
title: "UI: pokaż członków grupy w edytorze + status grup na liście Detectors"
status: ready
created: 2026-07-23
---

# Quick Task 260723-oik — Plan

## Goal

Dwie poprawki UX we froncie Argusa (`orchestrator/ui`, Preact + `@preact/signals`):

1. **Widoczność członków w edytorze grupy** — w edycji/tworzeniu grupy użytkownik NIE widzi,
   które sensory są członkami (widać dopiero po wyszukaniu). Ma zawsze widzieć listę
   wybranych członków, z możliwością usunięcia każdego.
2. **Status grup na liście Detectors** — wiersze pojedynczych sensorów pokazują status
   (`Rozgrzewka X/Y` / `Działa`), a wiersze grup nic. Grupy mają pokazywać status z
   `GET api/groups/{id}/status`: `Oczekuje` (jeszcze nie oceniona) / `Działa` / `Anomalia`.

Zakres: wyłącznie frontend. Backend (`GET api/groups/{id}/status`) już istnieje i jest używany
przez `AttributionPanel`.

## Constraints / decisions

- **NIE modyfikować `MemberPicker.tsx`** — jest współdzielony z `AddDetectorWizard`. Poprawkę #1
  zrobić w `GroupEditorForm.tsx`.
- Trzymać istniejące konwencje CSS (`.argus-list`, `.argus-list-row`, `.argus-row-meta`,
  `.argus-section-label`) i komponenty (`Card`, `Badge`, `Button`). Bez nowego CSS.
- Tony `Badge`: dostępne `ok | warn | error | tracked | member | neutral | accent`.
- Etykiety statusu po polsku (spójnie z wierszem sensora: „Działa").
- Poll grup: NIE ruszać istniejącego 5 s polla sensorów; dodać osobny ~30 s dla grup.

## Task 1 — Widoczność wybranych członków w edytorze grupy

**files:** `orchestrator/ui/src/components/GroupEditorForm.tsx`
(+ test: `orchestrator/ui/src/components/GroupEditorForm.test.tsx`)

**action:**
- W `GroupEditorForm`, tuż pod `<p class="argus-section-label">Members</p>` a NAD `<MemberPicker>`,
  dodać zawsze-widoczną listę wybranych członków:
  - `const selectedMembers = sensors.filter((s) => draftMembers.value.includes(s.entityId));`
  - Renderować tylko gdy `selectedMembers.length > 0`.
  - Nagłówek: `<p class="argus-section-label">Selected ({selectedMembers.length})</p>`.
  - `Card padding="none"` → `ul.argus-list`; każdy wiersz `li.argus-list-row argus-list-row--tracked`:
    - `span.argus-row-entity-id` = entityId,
    - jeśli `friendlyName && friendlyName !== entityId` → `span.argus-row-friendly-name`,
    - `div.argus-row-meta`: `span.argus-row-value` z `unitOfMeasurement` (gdy jest),
      `Badge tone="member"` „member",
      oraz `Button variant="destructive-ghost" size="xs"` „Remove" wołający `toggleMember(entityId, false)`.
- Użyć istniejącego `toggleMember` (już w komponencie).

**verify:** `npm test` (GroupEditorForm.test.tsx) + wizualnie w kroku deploy.

**done:** W edytorze istniejącej grupy widać listę wszystkich członków bez wpisywania w wyszukiwarkę;
klik „Remove" usuwa członka z `draftMembers`.

## Task 2 — Status grup na liście Detectors

**files:**
- `orchestrator/ui/src/state/groups.ts`
- `orchestrator/ui/src/state/detectors.ts`
- `orchestrator/ui/src/components/DetectorsPage.tsx`
- `orchestrator/ui/src/components/DetectorListRow.tsx`
- testy: `detectors.test.ts`, `DetectorsPage.test.tsx`, `DetectorListRow.test.tsx`

**action:**
- `state/groups.ts`:
  - Import `GroupStatus`, `GroupStatusResponse` z `../api/types`.
  - `export const groupStatuses = signal<Record<string, GroupStatus | null>>({});`
  - `export async function loadGroupStatuses(): Promise<void>` — dla każdej grupy z `groups.value`
    równolegle (`Promise.all`) pobrać `apiGet<GroupStatusResponse>(\`api/groups/${encodeURIComponent(id)}/status\`)`,
    zbudować mapę `id -> res.status`. Błąd pojedynczej grupy tolerować (pomiń — zostaw poprzednią
    wartość / nie wpisuj). Na końcu podmienić `groupStatuses.value` na nowy obiekt (merge ze starym,
    by zachować poprzednie przy częściowych błędach).
- `state/detectors.ts`:
  - `DetectorRow` dodać `status?: GroupStatus | null;` (import typu).
  - W `detectorRows` computed dla wierszy grup ustawić `status: groupStatuses.value[g.groupId]`
    (odczyt `groupStatuses.value` w ciele computed — reaktywność).
- `components/DetectorsPage.tsx`:
  - Na mount: `await loadGroups(); loadGroupStatuses();` (import `loadGroupStatuses`).
    (Uwaga: obecnie `loadGroups()` nie jest awaitowane — opakować w async IIFE / funkcję.)
  - Dodać osobny `setInterval(~30000)` wołający `loadGroups()` + `loadGroupStatuses()`; czyścić w cleanup.
  - Zostawić istniejący 5 s poll `loadSensors('')`.
- `components/DetectorListRow.tsx`:
  - `DetectorListRow` przekazać do `GroupRow` również `row.status`.
  - `GroupRow` przyjmuje `status?: GroupStatus | null`. W `div.argus-row-meta`, PRZED licznikiem członków,
    renderować badge:
    - `status === undefined` → nic (jeszcze nie pobrano),
    - `status === null` → `Badge tone="warn"` „Oczekuje",
    - `status.isAnomaly === true` → `Badge tone="error"` „Anomalia",
    - w przeciwnym razie → `Badge tone="ok"` „Działa".

**verify:** `npm test` (detectors/DetectorsPage/DetectorListRow) — mapowanie null/active/anomaly→badge,
poll grup wywoływany, member count nadal renderowany.

**done:** Wiersze grup na `#/detectors` pokazują badge statusu odpowiadający ostatniej ocenie
(lub „Oczekuje" gdy nigdy nieoceniona), odświeżany co ~30 s.

## Global verification

W `orchestrator/ui`:
- `npm run build` (tsc -b && vite build) — przechodzi (typy OK).
- `npm test` — cały zestaw zielony.

NIE deployować (osobny krok po akceptacji).

## must_haves

- truths:
  - Edytor grupy pokazuje wszystkich aktualnych członków bez wyszukiwania.
  - Wiersz grupy na liście Detectors pokazuje status (Oczekuje/Działa/Anomalia).
  - `MemberPicker.tsx` niezmodyfikowany.
  - `npm run build` i `npm test` przechodzą.
- artifacts:
  - `orchestrator/ui/src/components/GroupEditorForm.tsx` (lista wybranych członków)
  - `orchestrator/ui/src/state/groups.ts` (groupStatuses + loadGroupStatuses)
  - `orchestrator/ui/src/state/detectors.ts` (DetectorRow.status)
  - `orchestrator/ui/src/components/DetectorsPage.tsx` (poll statusów grup)
  - `orchestrator/ui/src/components/DetectorListRow.tsx` (badge statusu grupy)
