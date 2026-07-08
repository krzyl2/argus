---
phase: 11-new-standalone-screens-dashboard-algorithms-settings
reviewed: 2026-07-08T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - orchestrator/Argus.Orchestrator.Tests/SettingsEndpointTests.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator/Web/SettingsProjection.cs
  - orchestrator/ui/public/css/argus.css
  - orchestrator/ui/src/api/types.ts
  - orchestrator/ui/src/components/AlgorithmsPage.tsx
  - orchestrator/ui/src/components/DashboardPage.tsx
  - orchestrator/ui/src/components/SettingsPage.tsx
  - orchestrator/ui/src/components/Sidebar.test.tsx
  - orchestrator/ui/src/components/Sidebar.tsx
  - orchestrator/ui/src/main.tsx
  - orchestrator/ui/src/router.ts
  - orchestrator/ui/src/state/algorithms.ts
  - orchestrator/ui/src/state/dashboard.ts
  - orchestrator/ui/src/state/settings.ts
  - orchestrator/ui/src/state/theme.ts
findings:
  critical: 0
  warning: 1
  info: 4
  total: 5
status: issues_found
---

# Phase 11: Code Review Report

**Reviewed:** 2026-07-08
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

Zakres fazy 11: trzy nowe ekrany SPA (Dashboard/Algorithms/Settings), współdzielony
stan motywu (`state/theme.ts`), nowy endpoint `GET /api/settings` z redakcyjną projekcją
`SettingsProjection`, oraz aktywacja pozycji nawigacji w `Sidebar`.

Granica bezpieczeństwa jest solidna: `SettingsProjection.Build` jawnie wypisuje 6 pól
pole-po-polu (nigdy nie serializuje całego `ConnectionSettings`), a testy jednostkowe
weryfikują brak wycieku sekretów i brak nazw właściwości o kształcie sekretu. Endpoint
jest chroniony istniejącym `IsAuthorizedRequest`. Nie znaleziono podatności ani ryzyka
utraty danych.

Znaleziono jeden realny defekt funkcjonalny (kontrolka "Log level" nigdy nie pokazuje
faktycznej wartości z powodu niedopasowania wartości opcji) oraz kilka uwag jakościowych
dotyczących ekranu Dashboard i kruchości testów motywu.

## Warnings

### WR-01: Kontrolka "Log level" nigdy nie wyświetla faktycznego poziomu logowania

**File:** `orchestrator/ui/src/components/SettingsPage.tsx:15-19,128-134`
**Issue:**
Wartości opcji `LOG_LEVEL_OPTIONS` to `'debug' | 'info' | 'warning'` (małe litery,
skrócone), natomiast backend produkuje dla `Logging:LogLevel:Default` wyłącznie
`"Debug"`, `"Warning"` lub `"Information"` — potwierdzone w
`argus/rootfs/etc/cont-init.d/10-config-gen.sh:103-108`:

```sh
case "${LOG_LEVEL_RAW}" in
    debug)   DOTNET_LOG="Debug" ;;
    warning) DOTNET_LOG="Warning" ;;
    *)       DOTNET_LOG="Information" ;;
esac
```

Żadna z trzech rzeczywistych wartości nie pasuje do żadnej opcji:
`"Debug"` ≠ `'debug'`, `"Warning"` ≠ `'warning'`, `"Information"` ≠ `'info'`
(niezgodność wielkości liter, a dla Information także samej nazwy). Skutek: sterowany
`<select value="Information">` ma `selectedIndex = -1` i renderuje się pusty na każdym
realnym wdrożeniu — ekran Settings nigdy nie pokazuje faktycznie skonfigurowanego
poziomu logowania (a właśnie to jest jego jedynym zadaniem, bo pole jest read-only).

**Fix:** Dopasować wartości opcji do wartości emitowanych przez backend (lub
normalizować wielkość liter przy porównaniu). Np.:

```tsx
const LOG_LEVEL_OPTIONS = [
  { value: 'Debug', label: 'debug' },
  { value: 'Information', label: 'info' },
  { value: 'Warning', label: 'warning' },
];
```

lub renderować wartość z backendu jako dynamiczną opcję zamiast stałej listy:

```tsx
options={s?.logLevel ? [{ value: s.logLevel, label: s.logLevel }] : LOG_LEVEL_UNSET_OPTIONS}
```

## Info

### IN-01: KPI "Active group detectors" powiela `groupCount`

**File:** `orchestrator/ui/src/components/DashboardPage.tsx:81-85`
**Issue:** Kafelek "Active group detectors" renderuje `groupCount.value` — dokładnie ten
sam sygnał co kafelek "Groups" wyżej. Choć liczbowo bywa to prawdziwe (każda grupa ma
jeden `detector`), prezentowanie tej samej liczby pod dwiema różnymi etykietami jako
osobnych metryk jest mylące i — inaczej niż kafelek HA — nie jest oznaczone jako
placeholder/mock.
**Fix:** Albo wyprowadzić faktyczną liczbę aktywnych detektorów grupowych z danych, albo
oznaczyć kafelek jako pochodny/placeholder (hint), albo usunąć duplikat do czasu
dostępności prawdziwej metryki.

### IN-02: Kafelek "Home Assistant" ma zaszytą na sztywno wartość "Connected"

**File:** `orchestrator/ui/src/components/DashboardPage.tsx:86`
**Issue:** `value="Connected"` jest stałą — kafelek zawsze pokazuje "Connected" niezależnie
od faktycznego stanu połączenia. `hint="mocked — no endpoint yet"` ujawnia, że to mock, ale
sama wartość jest sfabrykowanym statusem (ryzyko, że operator uzna go za realny, gdy
faktycznie HA jest niedostępne).
**Fix:** Do czasu dodania `/api/health` rozważyć neutralną wartość (np. `'—'`) zamiast
kategorycznego "Connected", spójnie z konwencją "nigdy nie pokazuj sfabrykowanej wartości
jako realnej" zastosowaną w `state/dashboard.ts`.

### IN-03: `state/theme.ts` — singleton modułu + efekt uboczny na poziomie modułu utrudnia izolację testów

**File:** `orchestrator/ui/src/state/theme.ts:26-29`
**Issue:** `resolveInitialTheme()` oraz `document.documentElement.setAttribute(...)` wykonują
się raz, przy imporcie modułu, a sygnał `theme` jest singletonem modułu. `afterEach` w
`Sidebar.test.tsx` czyści `data-theme` i `localStorage`, ale nie resetuje sygnału `theme`.
Testy w tym pliku przechodzą tylko dzięki kolejności (pierwszy test nie zmienia motywu).
Dodanie testu, który wywoła `setTheme` przed testem toggle, wprowadzi zależność od
kolejności i losowe czerwone testy.
**Fix:** Udostępnić w warstwie testowej sposób resetu sygnału (np. eksport funkcji resetu
lub `vi.resetModules()` między testami), albo w `afterEach` jawnie przywrócić stan sygnału.

### IN-04: Zestawy danych mock w Dashboard (udokumentowane TODO)

**File:** `orchestrator/ui/src/components/DashboardPage.tsx:26-44`
**Issue:** `MOCK_ANOMALIES` i `MOCK_HEALTH` to statyczne dane demonstracyjne. Jest to
świadoma decyzja projektowa (D-02/D-03), poprawnie oznaczona banerami "Mocked" w UI oraz
komentarzami odwołującymi się do braku endpointów `/api/health` i historii anomalii.
Odnotowane wyłącznie jako dług techniczny do usunięcia po dodaniu odpowiednich endpointów —
nie jest to defekt tej fazy.
**Fix:** Śledzić jako zadanie następcze; usunąć mocki po powstaniu endpointów źródłowych.

---

_Reviewed: 2026-07-08_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
