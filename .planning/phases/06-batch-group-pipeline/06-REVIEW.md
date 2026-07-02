---
phase: 06-batch-group-pipeline
reviewed: 2026-07-02T00:00:00Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs
  - orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs
  - orchestrator/Argus.Orchestrator/Logging/LogEvents.cs
  - orchestrator/Argus.Orchestrator/Batch/IGroupInfluxDataSource.cs
  - orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs
  - orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchDetectorClientAdapter.cs
  - orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs
  - orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs
  - orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/IStatePublisher.cs
  - orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs
  - orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs
  - orchestrator/Argus.Orchestrator/Program.cs
  - orchestrator/Argus.Orchestrator.Tests/GroupInfluxReaderTests.cs
  - orchestrator/Argus.Orchestrator.Tests/GroupBatchSchedulerTests.cs
  - orchestrator/Argus.Orchestrator.Tests/MqttRetractionTests.cs
findings:
  critical: 2
  warning: 5
  info: 4
  total: 11
status: issues_found
---

# Phase 06: Code Review Report

**Reviewed:** 2026-07-02T00:00:00Z
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

Przejrzano ścieżkę batch/group scoringu (GRP-02/GRP-08): `EntitiesConfigLoader` (walidacja grup),
`GroupInfluxReader` (Flux query + freshness), `BatchSchedulerWorker` (polityka staleness JOINT vs
PEER), `MqttPublisherWorker`/`DiscoveryPublisher` (discovery + retraction dla grup) oraz testy.

Rdzeń logiki JOINT/PEER staleness (`BuildGroupMatrix`) jest poprawny i dobrze pokryty testami —
to główne ryzyko tej fazy zostało zaadresowane prawidłowo. Znaleziono natomiast realny wyścig
(race condition) w `MqttPublisherWorker.OnConfigChanged` (nowy kod tej fazy), który przy dwóch
szybko następujących po sobie zmianach configu może doprowadzić do niespójnej retrakcji/duplikacji
publikacji dla grup — dokładnie ten typ błędu, którego domain-attention miał unikać ("no orphaned
HA entities"). Drugi blocker dotyczy nadmiernie permisywnej walidacji Flux-injection guard w
`GroupInfluxReader`, który nie chroni przed wszystkimi znakami mogącymi zaburzyć konstrukcję
zapytania Flux mimo komentarza sugerującego pełną ochronę.

Reszta uwag to warningi dot. potencjalnych edge-case'ów (duplikaty w `Members`, brak walidacji
`stalenessCap <= 0`, milcząca utrata precyzji przy dużej liczbie punktów) oraz drobne info
(nieużywane usingi, martwy kod, magiczne stringi).

## Critical Issues

### CR-01: Race condition w MqttPublisherWorker.OnConfigChanged może zdublować lub pominąć retrakcję grup

**File:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs:70-128`
**Issue:**
Handler `OnConfigChanged` jest uruchamiany jako fire-and-forget (`_ = Task.Run(...)`) i nie ma
żadnej synchronizacji (semafora/locka) między kolejnymi wywołaniami. Komentarz w polu `_lastGroups`
(linie 31-35) twierdzi: *"the worker is single-threaded enough (ExecuteAsync + sequential
fire-and-forget handler body) for this to be a safe diff basis"* — to założenie jest błędne.
`ConfigChanged` jest zwykłym `EventHandler` wywoływanym synchronicznie wewnątrz `Swap()`
(`LiveEntitiesConfig.cs:46-50`), ale samo ciało handlera odpala **nowy** `Task.Run`, który
wykonuje się asynchronicznie względem wywołującego `Swap`. Jeśli operator zapisze konfigurację
dwa razy pod rząd w krótkim odstępie (UI umożliwia to — `POST /api/sensors/save` wywołuje
`liveCfg.Swap` na końcu obsługi żądania), powstaną dwa równoległe zadania `Task.Run`, które:
1. Oba czytają `_lastGroups` (może to być ta sama, jeszcze niezaktualizowana wartość dla obu),
2. Oba obliczają `removed = oldGroup.Members.Except(newGroup.Members)` na podstawie tego samego
   starego stanu,
3. Oba nadpisują `_lastGroups = newGroups` w nieokreślonej kolejności (możliwy lost update — końcowy
   `_lastGroups` może odpowiadać starszej z dwóch zmian, mimo że nowsza konfiguracja jest już
   aktywna w `_liveConfig`).

Efekt: przy szybkiej sekwencji zmian (np. dodanie potem usunięcie członka grupy) retrakcja może
zostać pominięta dla członka, który już nie istnieje w bieżącej konfiguracji → osierocona encja HA
(dokładnie efekt uboczny, którego ten obszar miał unikać), albo odwrotnie — retrakcja przeżywającego
członka przez porównanie do przestarzałego `_lastGroups`.

**Fix:** Serializować wykonania handlera (np. `SemaphoreSlim(1,1)` trzymany jako pole, `await`-owany
na wejściu do ciała `Task.Run`, zwalniany w `finally`), albo zastąpić fire-and-forget kolejką
przetwarzaną sekwencyjnie przez jeden dedykowany task/`Channel<T>`:

```csharp
private readonly SemaphoreSlim _configChangeGate = new(1, 1);

void OnConfigChanged(object? sender, EventArgs e)
{
    _ = Task.Run(async () =>
    {
        await _configChangeGate.WaitAsync(_stoppingToken);
        try
        {
            // ... existing body ...
        }
        finally
        {
            _configChangeGate.Release();
        }
    });
}
```

---

### CR-02: Flux injection guard nie blokuje wszystkich znaków mogących wyjść poza kontekst stringa

**File:** `orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs:22-23, 77-97`
**Issue:**
Regex `_safeFluxString = new(@"^[^""\\]+$")` blokuje tylko `"` i `\`. Wartości `memberId`,
`InfluxBucket`, `InfluxMeasurement`, `InfluxValueField` są następnie interpolowane bezpośrednio
do stringów Flux (`from(bucket: "{_settings.InfluxBucket}")`, `contains(value: r["entity_id"],
set: [{memberSet}])`, itd.) bez dalszego escapowania. Flux nie jest zwykłym SQL — poza literałami
łańcuchowymi w stringu istnieje ryzyko wyjścia poza pojedynczy literał string przez inne
metaznaki niż `"`/`\`, np.:
- **Nowa linia** (`\n`, `\r`) — Flux jest wrażliwy na strukturę linii/wcięcia w niektórych
  kontekstach parsera (pipe-forward). Regex `[^""\\]+` **dopuszcza** `\n`, ponieważ `.` w tym
  kontekście nie jest używane, a klasa negacji `[^"\\]` domyślnie dopasowuje znak nowej linii.
  `entity_id` pochodzący z `group.Members` (operator-controlled, ale też z YAML wczytywanego z
  pliku, który może być edytowany zewnętrznie przez `ConfigFileWatcherService`) mógłby zawierać
  osadzony znak nowej linii, wstrzykując dodatkowe linie Flux (np. dodatkowy `|> filter(...)`
  albo modyfikację `range()`).
- Znaki takie jak `$`, `{`, `}` nie są istotne dla Flux string interpolation w C# (to
  interpolacja .NET, nie Flux), więc to nie jest problem — ale sam fakt, że komentarz
  (linia 19-21) głosi *"reject values that contain double-quote or backslash which would allow
  Flux string-literal injection"* jako kompletną ochronę, jest mylący: nie pokrywa przypadku
  znaku nowej linii.

**Fix:** Rozszerzyć regex, by odrzucał też `\r` i `\n` (i ewentualnie ograniczyć się do
allowlisty dopuszczalnych znaków `entity_id`/nazw pól typu `^[A-Za-z0-9_.\-]+$`, co jest
bezpieczniejsze niż blacklista):

```csharp
private static readonly Regex _safeFluxString =
    new(@"^[^""\\\r\n]+$", RegexOptions.Compiled);
```

lub — zalecane — allowlist zamiast blacklisty, ponieważ `entity_id` HA ma znaną, wąską gramatykę
(`^[a-z0-9_]+\.[a-z0-9_]+$` dla entity_id; nazwy pól/bucketów są operator-controlled ale też
zwykle alfanumeryczne):

```csharp
private static readonly Regex _safeFluxString =
    new(@"^[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);
```

## Warnings

### WR-01: Brak deduplikacji `group.Members` — duplikat cichnie psuje macierz PEER

**File:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs:111-117`
**Issue:** Walidacja sprawdza tylko `group.Members.Count < 3`, ale nie odrzuca duplikatów w
liście (np. `["sensor.a", "sensor.a", "sensor.b"]` przechodzi walidację z `Count == 3`).
W `BatchSchedulerWorker.BuildGroupMatrix` (linia 294) `activeMembers.ToDictionary(m => m, ...)`
rzuci `ArgumentException: An item with the same key has already been added` przy pierwszym
duplikacie w `activeMembers`, crashując cały cykl grupy (złapane przez `catch (Exception ex)`
w `RunBatchAsync`, więc nie zabija workera — ale grupa nigdy nie będzie oceniana, cicho, bez
jasnego komunikatu wskazującego na przyczynę — log pokaże tylko ogólny `GroupSchedulerError`
z komunikatem wyjątku o duplikacie klucza, co jest myląca diagnostyka dla operatora).
**Fix:** Dodać sprawdzenie duplikatów w `ValidateGroups`:
```csharp
var distinctMembers = group.Members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
if (distinctMembers.Count != group.Members.Count)
{
    logger.LogWarning(LogEvents.GroupRejected,
        "Group '{GroupId}' has duplicate member ids — skipped", group.GroupId);
    continue;
}
```

### WR-02: `stalenessCap` z configu nie jest walidowany na wartości ujemne/zerowe

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:182-185, 493-496`
**Issue:** `TimeSpan.TryParse(capVal, out var parsedCap)` akceptuje `"00:00:00"` lub nawet
ujemny TimeSpan (`"-1.00:00:00"` parsuje poprawnie jako ujemny `TimeSpan`). Z `stalenessCap =
TimeSpan.Zero` lub ujemnym, `(utcNow - lastSeen) > stalenessCap` będzie **zawsze prawdziwe**
(chyba że `lastSeen` jest w przyszłości), więc każdy member zostanie uznany za stale — dla JOINT
grupa nigdy nie zostanie oceniona (`skipWholeGroup = true` w nieskończoność), dla PEER grupa
spadnie poniżej progu 3 i też nigdy się nie oceni. To cichy, trudny do zdiagnozowania deadlock
funkcjonalny wywołany literówką operatora w YAML (np. `staleness_cap: "0"` zamiast poprawnego
formatu TimeSpan).
**Fix:** Odrzucić `parsedCap <= TimeSpan.Zero` i spaść do `DefaultStalenessCap` z logiem
ostrzegawczym:
```csharp
var stalenessCap = group.Params.TryGetValue("staleness_cap", out var capVal) &&
                    TimeSpan.TryParse(capVal, out var parsedCap) && parsedCap > TimeSpan.Zero
    ? parsedCap
    : DefaultStalenessCap;
```

### WR-03: `EntitiesConfigLoader.ValidateGroups` — komunikat "unit check skipped" myląco łączy dwa różne przypadki

**File:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs:144-149`
**Issue:** Warunek `registry is null || resolvedUnitValues.Count < 2` obejmuje zarówno
przypadek "rejestr jeszcze niezaładowany" (cold boot — właściwe zachowanie: warn-only, nie
odrzucaj), jak i przypadek "rejestr jest gotowy, ale znaleziono tylko 0 lub 1 unikalną jednostkę"
(czyli **prawidłowy scenariusz — jednostki się zgadzają lub są nieznane**, co też powinno
przejść, więc logika jest OK funkcjonalnie), ale log zawsze mówi "sensor registry not yet
populated", nawet gdy `registry` nie jest nullem i po prostu `resolvedUnitValues.Count == 1`
(zgodne jednostki — normalny, zdrowy przypadek). To wprowadza mylące ostrzeżenie/info-log przy
każdym poprawnym starcie z jedną wspólną jednostką, sugerując operatorowi nieistniejący problem
z rejestrem.
**Fix:** Rozdzielić komunikaty:
```csharp
if (registry is null)
{
    logger.Log(LogLevel.Information, LogEvents.GroupConfigLoaded,
        "Group '{GroupId}' unit check skipped — sensor registry not yet populated", group.GroupId);
}
else if (resolvedUnitValues.Count > 1)
{
    logger.LogWarning(...); continue;
}
// else: registry populated and units consistent (0 or 1 distinct) — no log needed
```

### WR-04: `GroupInfluxReader.QueryGroupAsync` — brak walidacji `every`/`aggFn` przed interpolacją do Flux

**File:** `orchestrator/Argus.Orchestrator/Batch/GroupInfluxReader.cs:100-107`
**Issue:** `every` i `aggFn` pochodzą z `group.Params` (operator-controlled przez YAML/UI) i są
interpolowane bezpośrednio do `aggregateWindow(every: {every}, fn: {aggFn}, ...)` bez przejścia
przez `_safeFluxString` guard (guard jest stosowany tylko do `members`, `InfluxBucket`,
`InfluxMeasurement`, `InfluxValueField` — linie 77-87). Chociaż domain note mówi, że są to wartości
operator-controlled (accepted risk), niespójność jest realna: dwa pola z tej samej klasy ryzyka
(user-controlled Flux fragment) są traktowane różnie w tym samym pliku, co świadczy o przeoczeniu
przy T-06-03/T-06-04, a nie o świadomej decyzji.
**Fix:** Dodać te same guardy dla `every`/`aggFn` lub udokumentować świadomie, dlaczego są
wyłączone (np. jeśli walidowane wcześniej w `EntitiesConfigLoader`/UI — obecnie nie są).

### WR-05: `MqttPublisherWorker` nigdy nie retraktuje usuniętych **entities** (poza grupami)

**File:** `orchestrator/Argus.Orchestrator/Workers/MqttPublisherWorker.cs:102-118`
**Issue:** `OnConfigChanged` retraktuje usunięte członków grup (nowe w tej fazie), ale nadal
(pre-existing z Phase 3, niezmienione w tej fazie) nie retraktuje usuniętych zwykłych `entities`
— publikuje tylko discovery + availability dla bieżącego zbioru (`PublishAllAsync`), bez diffu
względem poprzedniego stanu entity. To nie jest regresja tej fazy, ale skoro faza dotyka
dokładnie ten plik i dokładnie ten mechanizm ("ordering... retract removed group members FIRST"),
warto odnotować, że analogiczny mechanizm dla zwykłych encji nadal nie istnieje — niespójność
zachowania między encjami pojedynczymi a grupowymi w tym samym workerze.
**Fix:** Poza zakresem tej fazy do naprawy, ale rekomendowane dodanie analogicznego
`_lastEntities` snapshot + `DiscoveryPublisher.RetractAsync` w osobnym tasku/fazie.

## Info

### IN-01: Nieużywany `using System.Linq;` w EntitiesConfigLoader.cs

**File:** `orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs:1`
**Issue:** `using System.Linq;` jest zbędny w projekcie z `ImplicitUsings` (CS8019/CS8933
ostrzeżenie kompilatora, jak wskazano w domain notes). LINQ jest już dostępny niejawnie.
**Fix:** Usunąć linię 1, jeśli implicit usings faktycznie obejmuje `System.Linq` w tym TFM
(sprawdzić `.csproj`); jeśli explicit using jest zamierzony dla czytelności, zostawić i
zignorować ostrzeżenie.

### IN-02: `BatchSchedulerWorker` duplikuje odczyt `every`/`aggFn`/`stalenessCap` z Params w trzech miejscach

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:176-185, 487-496`
**Issue:** Identyczny blok parsujący `every`/`fn`/`staleness_cap` z `group.Params` jest
powielony w `RunGroupBatchAsync` i `RunGroupFitAsync` (kopiuj-wklej, 10 linii x2). Nie jest to
błąd, ale utrudnia utrzymanie — zmiana domyślnej wartości lub logiki walidacji (patrz WR-02)
wymaga edycji w dwóch miejscach.
**Fix:** Wydzielić do prywatnej metody pomocniczej, np. `(string every, string aggFn, TimeSpan cap)
ResolveGroupQueryParams(GroupConfig group)`.

### IN-03: `BuildGroupScoreRequest` i `BuildFitGroupRequest` są niemal identyczne

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:311-355`
**Issue:** Obie metody budują request o identycznej strukturze (GroupId, Detector, Params,
Series z memberSeries) różniącej się jedynie typem requestu (`GroupScoreRequest` vs
`FitGroupRequest`). Duplikacja logiki zwiększa ryzyko rozjazdu przy przyszłych zmianach
(np. dodanie nowego pola do jednego, zapomnienie o drugim).
**Fix:** Niekrytyczne — jeśli oba typy proto pozostaną strukturalnie zbieżne, rozważyć
wspólną metodę generyczną lub helper wypełniający `RepeatedField<Series>`.

### IN-04: Magiczny próg `PeerMinFreshMembers = 3` zduplikowany z minimalnym rozmiarem grupy w configu

**File:** `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs:46` oraz
`orchestrator/Argus.Orchestrator/Config/EntitiesConfigLoader.cs:111`
**Issue:** Wartość `3` (minimalna liczba świeżych membersów dla PEER oraz minimalna liczba
membersów w ogóle dla configu) jest zakodowana niezależnie w dwóch plikach jako osobne stałe
(`PeerMinFreshMembers` i literał `3` w `Count < 3`). Nie są one formalnie powiązane — zmiana
jednej bez drugiej rozjeżdża założenie "grupa ma min. 3 membersów, więc floor=3 zawsze jest
osiągalny przy pełnej świeżości".
**Fix:** Wydzielić wspólną stałą (np. `GroupConstants.MinMembers = 3`) używaną w obu miejscach.

---

_Reviewed: 2026-07-02T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
