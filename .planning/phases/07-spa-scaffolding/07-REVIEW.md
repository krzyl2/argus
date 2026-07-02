---
phase: 07-spa-scaffolding
reviewed: 2026-07-02T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - orchestrator/ui/src/api/client.ts
  - orchestrator/ui/src/api/types.ts
  - orchestrator/ui/src/router.ts
  - orchestrator/ui/src/state/sensors.ts
  - orchestrator/ui/src/validation/detectorParams.ts
  - orchestrator/ui/src/main.tsx
  - orchestrator/ui/src/components/SensorsPage.tsx
  - orchestrator/ui/src/components/DetectorEntry.tsx
  - orchestrator/ui/src/components/DetectorParamGrid.tsx
  - orchestrator/ui/src/components/SaveBar.tsx
  - orchestrator/ui/src/components/SensorList.tsx
  - orchestrator/ui/src/components/PatternFiltersPanel.tsx
  - orchestrator/ui/vite.config.ts
  - orchestrator/Argus.Orchestrator/Web/SaveRequest.cs
  - orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - argus/Dockerfile
  - .github/workflows/build.yml
  - deploy/build-push.ps1
findings:
  critical: 1
  warning: 4
  info: 3
  total: 8
status: issues_found
---

# Phase 7: Code Review Report

**Reviewed:** 2026-07-02T00:00:00Z
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found

## Summary

Zakres obejmuje SPA scaffolding (Preact + signals), API DTO, walidację parametrów detektorów po stronie klienta, oraz zmiany w `Program.cs`/Dockerfile wspierające migrację na SPA. Ingress base-path guard w `client.ts` działa poprawnie (rzuca wyjątek przy leading-slash, `fetch()` zawsze dostaje ścieżkę względną). Autoryzacja (`IsAuthorizedRequest`) jest wywoływana jako pierwsza instrukcja we wszystkich trzech handlerach `/api/*`, a `MapFallbackToFile` nie serwuje danych konfiguracyjnych. Hot-reload parity (`InputValidator.Validate` → `ConfigWriter.WriteAsync` → `liveCfg.Swap`) jest zachowane w prawidłowej kolejności. Dockerfile poprawnie odrzuca Node/SDK w finalnym stage'u.

Największy problem: serwerowy `InputValidator.cs` **milczy** (nie dodaje błędu) gdy pole numeryczne jest puste, brakujące lub nie-liczbowe — `TryGetInt`/`TryGetDouble` zwracają `false` i walidacja po prostu pomija to pole. Klient (`detectorParams.ts`) natomiast traktuje puste/nie-liczbowe pole jako twardy błąd (`MSG_REQUIRED`). To rozjazd kontraktu parzystości klient/serwer: zmanipulowany lub zbugowany POST z brakującym/pustym polem numerycznym przechodzi walidację serwerową i trafia do `entities.yaml`, mimo że UI nigdy by na to nie pozwoliło.

## Critical Issues

### CR-01: Server-side InputValidator silently accepts missing/blank/non-numeric detector params

**File:** `orchestrator/Argus.Orchestrator/Config/InputValidator.cs:184-215`
**Issue:** `TryGetDouble` i `TryGetInt` zwracają `false` gdy klucz jest nieobecny w słowniku albo `double.TryParse`/`int.TryParse` się nie powiedzie (pusty string, tekst, itp.). Wywołania w `ValidateHst`/`ValidateMad`/`ValidateStl` (np. linie 98-101, 106, 113, 140, 150, 157, 163, 166, 169) opakowują te helpery w `if (TryGetX(...)) { ...check range... }` — gdy zwrócą `false`, blok w ogóle się nie wykonuje i **żaden błąd nie jest dodawany**. Efekt: POST z `{"window": ""}`, `{"window": "abc"}`, albo w ogóle bez klucza `window`, przechodzi walidację bez błędu i request trafia do `EntityConfig`/`ConfigWriter.WriteAsync` z niepoprawną/brakującą wartością zapisaną do `entities.yaml` (Dictionary<string,string> przyjmie cokolwiek). To łamie deklarowany kontrakt bezpieczeństwa w komentarzu klasy: "a tampered or malformed POST body must never reach ConfigWriter or the live pipeline" (linie 11-12).

Klient (`orchestrator/ui/src/validation/detectorParams.ts:24-26,34-36`) explicite traktuje ten sam przypadek jako błąd (`isBlankOrNonNumeric` → `MSG_REQUIRED`), więc istnieje faktyczny rozjazd walidacji klient/serwer — dokładnie ten typ błędu, przed którym ostrzega treść zadania (Validation parity).

Downstream ryzyko: detektor Pythonowy (gRPC) odbiera parametr jako pusty/niepoprawny string i albo rzuci wyjątek przy parsowaniu, albo (gorzej) przy niejawnej konwersji przyjmie wartość domyślną runtime'u, cicho psując detekcję dla tej encji — bez żadnego komunikatu do użytkownika, bo `save()` zwróci `ok: true`.

**Fix:**
```csharp
private static bool TryGetDouble(Dictionary<string, string> p, string key, out double val)
{
    val = 0;
    if (!p.TryGetValue(key, out var v) ||
        !double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out val))
    {
        return false;
    }
    return true;
}

// Callers must treat "TryGetDouble returned false" as an error, not a skip:
if (!TryGetDouble(p, "threshold", out var threshold) || threshold <= 0.0)
    errors.Add("Must be greater than 0.");
```
Zastosuj ten wzorzec do wszystkich wywołań `TryGetDouble`/`TryGetInt`/`ValidateIntAtLeast` w `ValidateHst`, `ValidateMad`, `ValidateStl` — brak klucza lub niepoprawny format musi zawsze generować błąd walidacji, tak jak robi to `validateField()` w `detectorParams.ts` (`isBlankOrNonNumeric` → `MSG_REQUIRED`).

## Warnings

### WR-01: `apiPost` never checks `res.ok` — non-JSON error responses throw a confusing parse error

**File:** `orchestrator/ui/src/api/client.ts:15-27`
**Issue:** `apiPost` zawsze robi `return res.json()` bez sprawdzenia `res.ok`, w przeciwieństwie do `apiGet` (linia 11), które explicite rzuca `GET ... failed: ${status}`. Serwer dla `POST /api/sensors/save` zwraca `Results.StatusCode(403)` (Program.cs:291) gdy `IsAuthorizedRequest` zwróci `false` — to pusta odpowiedź bez body. `res.json()` na pustym body rzuci `SyntaxError: Unexpected end of JSON input`, który zostanie złapany w `save()` (state/sensors.ts:163-167) i pokazany użytkownikowi jako `err.message` zamiast czytelnego komunikatu o autoryzacji/statusie HTTP. Komentarz w client.ts (linia 24-25) zakłada, że "callers inspect the ok/kind discriminant in the JSON body", ale to założenie nie trzyma się dla odpowiedzi bez JSON body (403).
**Fix:**
```ts
export async function apiPost<T>(path: string, body: unknown): Promise<T> {
  if (path.startsWith('/')) {
    throw new Error(`apiPost: path must be relative (no leading slash), got "${path}"`);
  }
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok && res.headers.get('content-type')?.includes('application/json') !== true) {
    throw new Error(`POST ${path} failed: ${res.status}`);
  }
  return res.json() as Promise<T>;
}
```

### WR-02: `DetectorDefaults.Get` and client `DETECTOR_DEFAULTS` are duplicated with no shared source of truth

**File:** `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs:16-43`, `orchestrator/ui/src/state/sensors.ts:9-28`
**Issue:** Wartości domyślne HST/MAD/STL są zduplikowane literalnie w dwóch miejscach (C# i TS) z komentarzami "must match X exactly" w obu. `GET /api/detectors/defaults` istnieje właśnie po to, by serwować tę tabelę, ale `makeDetectorEntry` w `state/sensors.ts` jej nie używa — woli hardkodowaną kopię klienta (komentarz: "no server round-trip needed"). To nie jest bug dziś (wartości się zgadzają), ale jest to duplikowany magic-number config, który cicho rozjedzie się przy przyszłej zmianie jednej strony bez drugiej — endpoint `/api/detectors/defaults` staje się martwym kodem jeśli nic go nie woła z UI (proszę zweryfikować czy faktycznie jest używany gdziekolwiek indziej w komponentach; w przejrzanych plikach nie widać wywołania `apiGet('api/detectors/defaults')`).
**Fix:** Jedno źródło prawdy — albo klient zawsze pobiera defaults z `/api/detectors/defaults` przy starcie i cache'uje w sygnale, albo endpoint zostaje usunięty jeśli jest zbędny. Przy pozostawieniu duplikacji dodać test kontraktowy porównujący obie tabele (np. test E2E lub snapshot), by rozjazd był wykrywany automatycznie.

### WR-03: `SensorSearchInput` debounce can deliver a stale value after unmount/rapid changes

**File:** `orchestrator/ui/src/components/SensorSearchInput.tsx:11-32`
**Issue:** `useRef` przechowuje timer, ale nie ma `useEffect` cleanup, który by go czyścił przy odmontowaniu komponentu. W obecnym SPA z jednym routem to nieszkodliwe (komponent nigdy się nie odmontowuje), ale jest to standardowy pitfall Preact/React hooks — jeśli w przyszłości SPA doda drugi route lub warunkowe renderowanie tego inputu, `setTimeout` odpali `onChange` po odmontowaniu, potencjalnie mutując sygnał `query`/wywołując `loadSensors` dla nieaktywnego widoku.
**Fix:**
```ts
import { useEffect, useRef } from 'preact/hooks';
...
useEffect(() => () => {
  if (timerRef.current) clearTimeout(timerRef.current);
}, []);
```

### WR-04: `loadSensors` race condition — out-of-order responses can overwrite newer results

**File:** `orchestrator/ui/src/state/sensors.ts:59-74`
**Issue:** Każde wywołanie `loadSensors(q)` (wyzwalane debounced-em z `SensorSearchInput`, 200ms) robi niezależny `fetch`. Jeśli dwa zapytania są w locie (np. użytkownik szybko zmienia filtr po debounce, albo request nr 1 jest wolny sieciowo), nie ma żadnej ochrony przed tym, by starsza odpowiedź nadpisała `sensors.value` już po tym, jak nowsza odpowiedź już przyszła — klasyczny "out of order response" bug. `loading.value` też migocze niepoprawnie w takim przypadku (drugi `finally` może zresetować `loading` zanim pierwszy call się zakończy, lub odwrotnie).
**Fix:** Dodać monotoniczny licznik/token żądania i odrzucać odpowiedzi nieaktualne:
```ts
let requestSeq = 0;
export async function loadSensors(q: string): Promise<void> {
  const seq = ++requestSeq;
  loading.value = true;
  try {
    const res = await apiGet<{ entries: SensorEntry[] }>(`api/sensors?q=${encodeURIComponent(q)}`);
    if (seq !== requestSeq) return; // stale response, ignore
    sensors.value = res.entries;
    ...
  } finally {
    if (seq === requestSeq) loading.value = false;
  }
}
```

## Info

### IN-01: Dead conditional in `main.tsx` — both ternary branches are identical

**File:** `orchestrator/ui/src/main.tsx:11`
**Issue:** `{route.value === '/sensors' ? <SensorsPage /> : <SensorsPage />}` — obie gałęzie zwracają dokładnie ten sam komponent, więc warunek nie ma żadnego efektu. To celowe zgodnie z komentarzem ("this phase ships exactly one real route"), ale jest to martwy warunek, który myli czytelnika i sugeruje istnienie routingu, którego nie ma.
**Fix:** Uprościć do `<SensorsPage />` bez warunku, albo dodać TODO wskazujące, że warunek zostanie użyty w przyszłej fazie z drugim routem.

### IN-02: `GET /api/detectors/defaults` response wraps `params` in a JSON reserved-looking key via `@params`

**File:** `orchestrator/Argus.Orchestrator/Program.cs:281`
**Issue:** `Results.Json(new { name, @params = defaults })` — `@params` to C# escaping dla `params` jako identyfikator (nie słowo kluczowe w tym kontekście, ale konwencjonalnie used to avoid confusion). Serializuje się poprawnie jako `"params"` w JSON dzięki domyślnej camelCase policy, więc to nie jest bug, ale warto zauważyć: `DetectorDefaults` (typ TS w `api/types.ts:38-41`) definiuje `params: Record<string, string>` — zgodne. Brak realnego problemu, ale nazwa `@params` bez komentarza może zdezorientować przyszłego czytelnika niezaznajomionego z C# identifier escaping.
**Fix:** Rozważyć dodanie krótkiego komentarza `// @ escapes the C# keyword-like identifier; serializes as "params"`.

### IN-03: `DetectorEntry.tsx` aria-label uses numeric `entityIdx` instead of a human-readable entity id

**File:** `orchestrator/ui/src/components/DetectorEntry.tsx:30`
**Issue:** `aria-label={\`Detector type for entity ${entityIdx}\`}` — `entityIdx` to indeks liczbowy z sortowania alfabetycznego (0, 1, 2...), nie identyfikator encji HA. Użytkownik czytnika ekranu usłyszy "Detector type for entity 3", co nie mówi nic o tym, której faktycznie encji dotyczy kontrolka (np. `sensor.living_room_temp`). `entityId` jest już propagowane do `DetectorDisclosure`/`AddDetectorButton` (które poprawnie używają `entityId` w aria-label), ale `DetectorEntry`/`DetectorParamGrid` dostają tylko `entityIdx`.
**Fix:** Przekazać `entityId: string` przez łańcuch propsów `DetectorDisclosure` → `DetectorEntry` i użyć go w `aria-label`, analogicznie do `AddDetectorButton`.

---

_Reviewed: 2026-07-02T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
