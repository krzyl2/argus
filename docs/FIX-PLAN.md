# Argus — plan naprawy fałszywych alarmów + symulator historii

> Plan naprawy fałszywych alarmów w Argusie + symulator detektora na danych historycznych.
> Podstawa: pomiary na żywej instancji HA operatora z 2026-09-03 (oznaczenia F1–F15 w tekście).
> Wszystkie odwołania `plik:linia` zweryfikowane w kodzie na commicie `d569e84`.
> Sekcja 8 to niezależny audyt kompletności tego dokumentu — luki oznaczone STILL OPEN nie są jeszcze zamknięte.

## 1. Diagnoza

HST nie jest detektorem anomalii tylko detektorem rzadkości — `river.anomaly.HalfSpaceTrees.score_one` zwraca `1 - mass/max_mass`, więc na skwantowanym szeregu rzadki-ale-całkowicie-normalny poziom 101 W dostaje 0.997, a modalny 107 W tylko 0.560 (F4). Normalizacja jest zepsuta strukturalnie: `detector/argus_detector/hst_detector.py:83-84` woła `self._normalizer.learn_one` PRZED `transform_one`, a `river.preprocessing.MinMaxScaler` trzyma nieograniczone bieżące min/max, więc po jednym skoku 13.01 przy p50=0.54 całe normalne pasmo zapada się do ~0.3% zakresu [0,1] (0.54 → 0.0032) i nigdy nie wraca (F5). Skutkiem jest rozkład wyniku per-czujnik i bez żadnej kalibracji — memory żyje w 0.830–1.00, processor w 0.562–1.00, load w 0.480–1.00 — więc jeden globalny próg `high_threshold=0.7` nie może być poprawny na wszystkich naraz (F6), i nic w systemie tego rozkładu nie obserwuje: `is_warmed_up` przełącza się przy `n_seen >= window` (250) i na tym koniec (F7). Próg zwolnienia jest arytmetycznie nieosiągalny — `HysteresisGate.cs:45` zwalnia przy `score < low_threshold` (0.3), a zmierzone minima 24h to 0.480 / 0.830 / 0.562 / 0.492 / 0.497, czyli raz zapalona flaga nie może zgasnąć nigdy (F2). W polu wygląda to tak: pięć `binary_sensor` ON nieprzerwanie >24h, po 6 zmian stanu na flagę i wszystkie o kształcie restartu add-onu (`on → unavailable → unknown → on`), on-time 100% / 100% / 99% / 91% / 25% (F1). Precyzja jest bliska zeru — `memory_use_percent` 1.2% przy 100.0% próbek ≥ 0.7, `load_5m` 4.3% przy 80.0%, `processor_use` 2.9%, `zamrazarkapiwnica_power` 0.0% na szeregu 101–109 W o sd 1.87 W, który w 24h nie zawiera ANI JEDNEGO punktu odstającego — a jedyny czujnik z realnym sygnałem to `lodowkababcia_power` (83%, prawdziwe cykle sprężarki 0 W / 984 W) i to jego nie wolno stracić (F3). Warstwa MQTT dokłada szum niezależnie od detektora: flaga jest republikowana przy każdym ticku scoringu (~4x/15 s), zamiast wyłącznie na przejściu stanu (F8). Priming jest martwy — Phase 15 dowiozła `Warmup` RPC i `IInfluxDataSource`/`InfluxDbReader`, ale `influxUrl=null`, więc żadna encja nigdy nie jest primowana i każdy model rozgrzewa się wyłącznie z ruchu live (F11), mimo że HA Recorder trzyma na tej instancji 7 dni surowej historii osiągalnej komendą `history/history_during_period` po istniejącym WebSockecie (F12). Rejestr encji jest jednorazowym snapshotem z `get_states` (`NetDaemonHaEventSource.cs:143`), a `state_changed` nigdy go nie dotyka, więc picker pokazuje 157 z 403 numerycznych encji (F10) — a `sensor.zamrazarkapiwnica_power` jest jednocześnie aktywnie scorowany i publikowany do HA, i nieobecny w `GET /api/sensors` oraz na ekranie Detektorów, czyli nie da się go ani nastroić, ani odśledzić (F9). Że to jest naprawialne, wiadomo z pomiaru offline na tym samym zapisanym strumieniu: bramka rolling-MAD na SUROWEJ wartości (fire z>5, release z<3, 3 kolejne) daje load 4 epizody/5.5%, cpu 2/0.9%, memory 0/0%, zamrazarka 0/0%, lodowka 2/3.7% — dokładnie te realne zdarzenia i tylko one (F13).

## 2. Decyzje

Identyfikatory `D-02`, `D-06`, `D-07`, `D-08`, `D-11`, `D-14`, `D-15`, `D-16` to istniejące ograniczenia projektu (odpowiednio: całe ML w Pythonie; brak locka na czas I/O w checkpointach; 60 s cooldown po reconnect; polskie friendly-names; brak persystencji stanu bramki po stronie .NET; `object_id == unique_id` w discovery; degrade-nie-throw na złej konfiguracji; knoby `ARGUS_*` poza schematem add-onu). Nowe decyzje tej naprawy dostają `D-A`…`D-N` i są referencowane w dalszych sekcjach.

| ID | Decyzja | Uzasadnienie (1 linia) | Odrzucona alternatywa |
|----|---------|------------------------|-----------------------|
| **D-A** | Domyślnym detektorem pojedynczego czujnika skalarnego jest **`rmad`** — nowy `detector/argus_detector/rmad_detector.py`, stdlib-only (`bisect`, `deque`, `math`), krocząca mediana/MAD w oknie `window=720`, `min_samples=60`, wynik `score = z/(z+z_scale)`. | F4 dowodzi, że HST liczy rzadkość, a nie odchylenie; żaden próg ani kalibracja nałożone na statystykę rzadkości tego nie odwrócą — `rmad` liczy dokładnie tę statystykę, którą F3 przyjmuje jako ground truth i którą F13 zmierzył jako jedyną działającą. | „Calibrated HST" (bounded scaler + ECDF + protected calibration): jego własny prototyp raportuje, że inwersja F4 PRZEŻYWA naprawę (101 W → 0.974 vs 107 W → 0.538), a gałąź kwantylowa sama zostawia memory na 37 epizodach / 35.5% on-time — nośnikiem decyzji i tak jest MAD, więc MAD ma być detektorem, a nie protezą doklejoną do scorera, który się z nim nie zgadza. |
| **D-B** | Progi są **bezwymiarowe**: `z_scale = 5.0` (stała modułowa, NIE parametr), `high_threshold = 0.5` (⇔ z > 5), `low_threshold = 0.375` (⇔ z < 3), `min_consecutive = 3`. Odwrotność: `z = z_scale·t/(1−t)`. | Jedna tabela domyślnych wartości jest arytmetycznie poprawna na każdym czujniku niezależnie od jednostki i zakresu (to jest właściwa likwidacja F6), a ta trójka odtwarza bit-w-bit wariant zmierzony w F13 przez istniejącą bramkę bez zmiany jej kodu; `z_scale` i `high_threshold` to ten sam stopień swobody, więc wystawienie obu byłoby pułapką strojeniową. | Przemianowanie znaczenia `high_threshold`/`low_threshold` na kwantyle (te same klucze, te same typy, te same zakresy w `InputValidator.cs:107-125`, nowe znaczenie, zero możliwości wykrycia błędu) — najgroźniejsza dostępna zmiana konfiguracji i całkowicie zbędna, gdy sam wynik jest bezwymiarowy. |
| **D-C** | Bramka zdarzeń **zostaje w .NET**, w `Detection/HysteresisGate.cs`, bez zmiany kształtu klasy — te same liczniki, ta sama semantyka `min_consecutive`. Zmieniają się wyłącznie wartości domyślne (D-B). Wszystkie 10 testów `HysteresisGateTests.cs` (118 linii) musi zostać zielonych bez edycji. | Skoro wynik jest już skalibrowany per-encja i bezwymiarowy, stały próg jest poprawny; utrzymanie bramki bez zmian to zero ryzyka na najgorętszej ścieżce. | Warstwa zdarzeń oparta na randze wyniku + drugi kanał robust-z na surowej wartości (~500 linii nowego .NET, sort 720 elementów na werdykt, nowy lock między pętlą zapisu a odczytu). Jej własny plan przyznaje, że kanał surowy trzeba będzie wycofać, gdy wynik stanie się odchyleniowy — czyli po D-A duplikowałaby MAD po obu stronach gRPC. |
| **D-D** | Higiena publikacji flagi ships razem z D-C: `EntityRuntimeState.LastPublishedFlag` staje się `bool?` i jest wreszcie CZYTANY (publikacja tylko na przejściu, koniec F8), `StatePublisher.cs:62` przechodzi na `retain: true`, log per-publish z `StatePublisher.cs:61` schodzi na `Debug`, przy starcie każda encja publikuje jawny **OFF**, dochodzi watchdog `max_event_duration_sec = 21600` (6 h) z WARN `AlertStormRaised`, a `BatchSchedulerWorker.cs:451` przestaje publikować flagę (zostaje sam score). | Change-only na temacie nieretained zostawiłoby HA w `unknown` po restarcie, więc `retain:true` jest wymagane, a nie kosmetyczne; watchdog gwarantuje, że nawet przy błędnym scorerze żadna flaga nie zostanie ON dłużej niż 6 h (jedyne twarde zabezpieczenie F1 niezależne od detektora); batch to drugi, bezhisterezowy pisarz na ten sam temat, uśpiony wyłącznie dlatego, że `influxUrl` jest null. | Zostawienie F8 do wyregulowania poziomem logowania — nie usuwa 4 publikacji MQTT na 15 s, tylko je ukrywa. |
| **D-E** | Sensor score publikuje **zduszony robust-z**: 0.5 = z 5, 0.8 = z 20. Ten sam temat `argus/{slug}/score/state`, `retain:false`, publikacja przy każdym ticku (niezmieniony inwariant „flaga implikuje score"). Nieciągłość historii i znaczenia musi trafić do **`argus/CHANGELOG.md`** (plik do utworzenia w WS3; release notes = ten plik, jedyny nośnik — `argus/DOCS.md` not wydaniowych nie niesie). | Wynik przestaje być nieinterpretowalną masą HST i staje się porównywalny między czujnikami; kubełki 0.8/0.5 w `DashboardPage.tsx:24-26` zaczynają cokolwiek znaczyć. | Publikowanie surowego `z` — czytelniejsze w HA, ale wysadza zakresy `(0,1]`/`[0,1)` w `InputValidator.cs:107-125`, mirror kliencki i kubełki dashboardu; pięć plików za czytelniejszą liczbę. |
| **D-F** | `hst` **zostaje verbatim** jako opt-in i ścieżka rollbacku: `hst_detector.py` dostaje wyłącznie docstring z blokiem KNOWN DEFECTS (F4/F5/F6/F7), checkpointy w `/data/models/<slug>/hst/` nie są czytane, nadpisywane ani kasowane (namespace rozłączny z `<slug>/rmad/`), a w UI karta detektora jest opisana jako **„legacy / niekalibrowany — wymaga ręcznego strojenia progów"**. Fabryka `_create_detector` loguje ostrzeżenie **raz na encję, w miejscu wyboru w `ScoreStream`**, nie w fabryce (którą woła też `fit_one`). | Rollback jednym kliknięciem, z zachowanym `n_seen` (load_5m 16061, memory 17824, processor 11380, lodowka 707) — i jednocześnie żaden operator nie wróci do detektora rzadkości nie widząc ostrzeżenia. | Usunięcie `hst` (kasuje jedyną wolną ścieżkę odwrotu) albo naprawianie go „przy okazji" (F5 pozostaje niezałatany, a to jest zadeklarowany cel rollbacku — trzeba to napisać wprost, nie sugerować parytetu). |
| **D-G** | **Tożsamość MQTT zostaje odcięta od nazwy detektora.** `Mqtt/UniqueId.cs:13-18` przechodzi z `argus_{slug}_{detector}_anomaly` / `_score` na `argus_{slug}_anomaly` / `argus_{slug}_score`; migracja (D-L) PRZED zapisem YAML publikuje pustą retained payload na stare tematy `homeassistant/binary_sensor/argus_{slug}_{det}_anomaly/config` i `homeassistant/sensor/argus_{slug}_{det}_score/config` dla KAŻDEJ pary (slug, detektor) z konfiguracji sprzed migracji (`hst`, ale też ręcznie ustawione `mad`/`stl` — `InputValidator.cs:26`). Test musi to przypiąć. | To jedyny blocker upgrade'u, którego pierwsza wersja planu w ogóle nie widziała: `DiscoveryPublisher.cs:224` bierze nazwę detektora z `Detectors[0].Name`, a `RetractAsync` (`DiscoveryPublisher.cs:169-187`) retraktuje tylko encje USUNIĘTE — migrowana encja nadal jest śledzona, więc stara retained config nigdy nie znika, a temat stanu `argus/{slug}/flag/state` jest bez detektora, czyli obie encje HA jechałyby z jednego strumienia. Bez tego operator dostaje duplikaty i traci 24h historii, dashboardy i automatyzacje. | Zaakceptowanie nowych `entity_id` i wypisanie listy podmian — unieważnia porównanie „ta sama encja przed/po", na którym stoją kryteria F1/F3, i zabija tezę „rollback jest darmowy". |
| **D-H** | **`FrozenSensorDetector` zostaje WYŁĄCZONY konfiguracją, nie kodem**: migracja przenosi `frozen_window` VERBATIM i ustawia `frozen_variance_threshold: 0.0` — `FrozenSensorDetector.cs:46` liczy `variance < 0.0`, a `ComputeVariance` (`:50-61`) nigdy nie zwraca liczby ujemnej, więc `IsFrozen` jest trwale false bez dotykania kodu. `frozen_window: "0"` jest **ZAKAZANE**: `FrozenSensorDetector.cs:29-31` robi wtedy `Dequeue()` na pustej kolejce, a `ScoreStreamPipeline.cs:172` woła `AddReading` na każdym odczycie ⇒ `InvalidOperationException`, i osobno `InputValidator.cs:101` wymaga `frozen_window ≥ 1`. Znika też wymuszanie flagi: `ScoreStreamPipeline.cs:270-271` znika, a stan „frozen" wchodzi do bramki jako przesłanka, więc podlega `min_consecutive`, watchdogowi i potrafi ZGASNĄĆ. Musi zostać zachowana gwarantowana ścieżka publikacji flagi dla encji, dla której detektor nie zwraca werdyktu (inwariant opisany w `ScoreStreamPipeline.cs:255-262`) — realizowana jako publikacja change-only z `PublishFrozenAsync`, nie jako usunięcie publikacji. | `lodowkababcia_power` to w 88% zera (SCALE-1), więc przy `frozen_window=10` okno dziesięciu kolejnych 0 W ma wariancję 0 < 0.001 ⇒ `IsFrozen` (`FrozenSensorDetector.cs:38-47`) trzyma się przez cały postój sprężarki, a `AlertPolicy` ma `fire=…||frozen` i `clear=…&&!frozen` — on-time ≈ 88% zamiast `<10%` z §5.2 i 2 epizodów z D-J. Docelowe liczby F13 (lodówka 2 ep./3.7%, memory 0/0%, zamrażarka 0/0%) zmierzono na bramce BEZ kanału frozen, więc włączony frozen unieważnia kalibrację progów. Dziś dodatkowo frozen wymusza ON omijając warm-up, suppression i histerezę, a zgasić może go tylko trzy wyniki < 0.3, których F2 dowodzi, że nie ma — to bezpośredni współsprawca F1. | Pozostawienie 10/0.001 (utrwala współsprawcę F1 i łamie D-J na lodówce) albo `frozen_window: "0"` z WS3 4a (crash `Dequeue()` w `FrozenSensorDetector.cs:29-31` + odrzucenie przez `InputValidator.cs:101`). Utrata pokrycia czujnika zamarłego na niezerowej stałej przyjęta świadomie — §7 #8. |
| **D-I** | `scale_floor` (podłoga estymaty skali, w JEDNOSTKACH czujnika) domyślnie `0.0`, ale **migracja wpisuje `scale_floor="0.3"` każdej migrowanej encji, której `unit_of_measurement` w snapshocie HA to `%`**; brak jednostki ⇒ 0.0. | Zmierzone na szeregu o kształcie `memory_use_percent` (5653 próbki, 1 miejsce po przecinku): przy `scale_floor=0.0` MAD wynosi 0.1, sigma 0.148, a łagodny ruch o 1.1 pp daje z = 7.4 → **4 epizody / 7.02% on-time**; `0.05` i `0.1` nic nie zmieniają, dopiero `0.3` daje **0 epizodów / 0%**. Bez tego trzy z pięciu czujników (memory, processor, disk_use) wchodzą w produkcję z nowym fałszywym alarmem. | Globalne `scale_floor=0.0` i notka w dokumentacji — świadome dowiezienie zmierzonego regresu; heurystyka na jednostce jest tu dopuszczalna, bo `scale_floor` JEST w jednostkach czujnika (inaczej niż okno, które jest w próbkach i którego kadencji z jednostki nie da się zgadnąć). |
| **D-J** | **Kryterium odbioru dla F3 jest per-czujnik, nie globalne:** `lodowkababcia_power` ≥ 2 epizody odpowiadające realnym cyklom 0 W / 984 W (precyzja epizodów ≥ 70%, dziś 83%); `zamrazarkapiwnica_power` **dokładnie 0 alarmów/24h**; `memory_use_percent` **0 alarmów/24h**; `load_5m` ≤ 6 epizodów i ≤ 7% on-time; `processor_use` ≤ 3 epizody i ≤ 2% on-time. Globalna liczba precyzji **nie jest** kryterium odbioru — i to jest zapisane, nie przemilczane. | F3 definiuje „prawdziwy outlier" jako robust z = |x−median|/(1.4826·MAD) > 3.5, czyli DOKŁADNIE statystykę, którą liczy `rmad` — każda podana liczba precyzji byłaby w połowie samopotwierdzająca. Falsyfikowalne pozostają: stopa alarmów, zerowość na zamrażarce i zachowanie zdarzeń lodówki. | Przepuszczenie planu bez żadnego progu dla F3 (cały plan mógłby przejść przy precyzji nadal 1–4%) albo podanie globalnego progu precyzji, którego pomiar jest cyrkularny. |
| **D-K** | Priming pochodzi z **HA Recorder** przez istniejący szew `IInfluxDataSource`: nowy `Batch/HaRecorderHistorySource.cs` wołający `history/history_during_period` na **krótkożyciowym, request/response-only** sockecie (`minimal_response:true`, `no_attributes:true`, `include_start_time_state:false`, jedna encja na komendę), zarejestrowany w gałęzi `else` w `Program.cs:207-214`. `BackfillRowCap=5000` (clamp 1..20000), `BackfillSliceHours=24`, `BackfillMaxEmptySlices=2`. Rejestracja `IBatchDetectorClient` wychodzi poza gałąź Influx (`Program.cs:190`). | To jedyne źródło historii na tej instalacji (F11: `influxUrl=null`; F12: 7 dni w Recorderze, 1546 wierszy dla lodówki); socket bez subskrypcji nie może zjeść ramek `state_changed` ani — przy przekroczeniu fatalnego limitu 4 MB z `HaWebSocketClient.cs:246` — zerwać żywego strumienia. | InfluxDB (nieskonfigurowany), bezpośredni dostęp do bazy Recordera (zakaz w PROJECT.md), pobieranie historii na współdzielonym, zasubskrybowanym sockecie (błąd historii = nieskończona pętla reconnectu dla całego scoringu). |
| **D-L** | `entities.yaml` dostaje `schema_version: 2`; jednorazowa, **idempotentna i fail-loud** migracja w `Config/EntitiesSchemaMigrator.cs` woła się z `Program.cs:22` PRZED `EntitiesConfigLoader.Load`, zapisuje `entities.yaml.pre-v2.bak` (nigdy nie nadpisuje istniejącego), przepisuje encje o **dokładnym** odcisku legacy (`window 250, n_trees 25, high 0.7, low 0.3, min_consecutive 3, frozen_window 10, frozen_variance_threshold 0.001` lub `params: {}`) na `rmad`, zostawia na `hst` i loguje WARN każdą encję z parametrami strojonymi ręcznie, i zachowuje `groups` oraz `_patterns` verbatim. Klucz `schema_version` musi być stemplowany przez **OBU** pisarzy: `Program.cs:463-468` i `Program.cs:604-610`. | Nie istnieje odwzorowanie zachowujące znaczenie z bezwzględnego progu HST na próg robust-z, więc parametry strojone ręcznie muszą zostać nietknięte; pominięcie `Program.cs:604-610` powoduje, że pierwszy zapis grup zdejmuje stempel i migracja przepisuje plik przy KAŻDYM boocie (a każdy zapis to rename → `ConfigFileWatcherService` → Swap → reset wszystkich bramek). Utrata `groups` to potwierdzona przyczyna G-14-1. | Migracja „best effort" z połknięciem wyjątku (uruchomienie nowych semantyk na starych wartościach 0.7/0.3 znaczy „alarm powyżej 70. percentyla" ≈ 30% on-time — gorzej niż nic robienie). |
| **D-M** | **F7 zostaje świadomie NIEROZWIĄZANY jako osobna faza.** Nie ma stanu `CALIBRATING`, nie ma `cal_min`, nie ma protected calibration window. Krocząca mediana/MAD w oknie 720 JEST kalibracją — przeliczaną co tick, per encja. `is_warmed_up` przełącza się przy `n_seen >= min_samples` (60), a `Verdict.window` raportuje **60**, nie 720, bo to jest bramka, która realnie obowiązuje. | Detektor odchyleniowy nie potrzebuje osobnego obserwatora rozkładu — sam estymator skali nim jest; dodanie fazy kalibracji kosztowałoby 750 punktów do pierwszego alarmu (~3.5 doby na wolnych czujnikach) za informację, którą i tak mamy. | Jawna faza kalibracji z `cal_window=2000` / `cal_min=500`: na `memory_use_percent` okno kalibracji zostaje w tyle za oknem zapominania HST i sama gałąź kwantylowa daje 37 epizodów / 35.5% on-time. **Skutek uboczny do udokumentowania:** `EntityRuntimeState.cs:48-54` seeduje `WarmUpWindow` z konfigurowanego `window`, więc chip w `DetectorListRow.tsx:76` pokazuje „Rozgrzewka N/720" do pierwszego werdyktu i „N/60" po nim — WS3 seeduje `WarmUpWindow` z `min_samples` dla encji `rmad`. |
| **N** = **D-N** | `GET /api/sensors` **musi zwracać zapisane `detectors` (name + params)** danej encji, a `getOrInitEdit` (`ui/src/state/sensors.ts:54-61`) hydratować edytor z odpowiedzi zamiast seedować `[makeDetectorEntry('hst')]`. To twardy prerekwizyt, nie follow-up. | Dziś żaden endpoint nie zwraca parametrów, a `save()` (`sensors.ts:161-169`) to podmiana CAŁEJ listy — więc pierwszy zapis z DOWOLNEGO ekranu (łącznie z polami wzorców w Ustawieniach) cofa migrację na wszystkich czujnikach i objawia się jako „nowy detektor nie działa". | Dowiezienie migracji bez read-backu — fix trzyma się do pierwszego kliknięcia operatora. |

## 3. Kolejność wdrożenia

**Uwaga do numeracji (Rule 7):** obowiązuje **jedna** numeracja — ta z nagłówków §4: WS1 detektor, WS2 bramka, WS3 migracja konfiguracji, WS4 rejestr czujników, WS5 backfill z Recordera, WS6 symulator. Numeracja krytyki jest porzucona — to ona wytworzyła pozorną sprzeczność kolejności. **Numer ≠ kolejność wdrożenia: kolejność wiążąca to WS1 → WS2 → WS5 → WS3 → WS4 → WS6** i tak są ponumerowane pozycje 1–6 poniżej. §5, §7 i §8 referencują te same numery WS.

Wspólny krok wydania dla KAŻDEJ pozycji (memory-rule: `git push` ≠ update w HA):
1. Zielone bramki lokalnie: `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` (baseline 463, **0 failed / 0 skipped**; wcześniej `bash deploy/generate-certs.sh 127.0.0.1 gpu-host`), `cd detector && python -m pytest -q` (264 passed / **1** skipped — jedyny dozwolony skip to `test_restart_resilience.py:129`), `python -m pytest tests/ -q` (4/4), `cd orchestrator/ui && npm run build` (**`tsc -b` — sam `npx vitest run` nie łapie błędów typów w fixture'ach**) `&& npx vitest run`.
2. Bump `argus/config.yaml:3`.
3. `./deploy/build-push.ps1 -Version X.Y.Z`, commit `argus/config.yaml`, Update w HA.
4. Weryfikacja po deployu, że artefakt faktycznie dojechał (dla pozycji dotykających SPA: ekran renderuje się pod Ingressem, bo `wwwroot` jest artefaktem builda — edytuje się `ui/public/`, nigdy `wwwroot/`).

---

**1. WS1 — detektor `rmad` (Python).** Zależności: brak.
Obserwowalna zmiana: **żadna po stronie operatora — i to jest zamierzone.** WS1 jest bezwładny, dopóki orkiestrator nie wyśle `params["detector"]="rmad"`. Mierzalne jest wyłącznie offline: replay zmierzonego histogramu F4 (101:10, 103:41, 105:148, 107:230, 109:113) przez `RmadDetector.score_batch` daje średnie ściśle monotoniczne w |x−107| (0.288 / 0.213 / 0.119 / 0.000 / 0.119) i max < 0.5 — inwersja F4 zniknęła; `grep -rn 'MinMaxScaler' detector/argus_detector/` trafia już tylko w `hst_detector.py` (`normalizer.py` skasowany). Zakres obejmuje sześć chirurgicznych edycji `registry.py` (`STREAMING_DETECTORS = {"hst","rmad"}`, dispatch w `_get_or_create`, `_streaming_keys` zamiast `_hst_keys` — bez tego modele `rmad` NIGDY nie są checkpointowane, `apply_params` na żywej instancji, `_create_detector` w `warmup_one` z tym samym guardem `STREAMING_DETECTORS`) i wybór detektora w `ScoreStream` przez `point.params["detector"]` z fallbackiem na `hst` przy nieznanej nazwie — fallback jest OBOWIĄZKOWY, bo `registry.py:481` rzuca `ValueError`, a `servicer.py:104-107` zamienia go w `context.abort(INTERNAL)` ubijający CAŁY multipleksowany strumień, nie jedną encję.
Wydanie: **2.1.12**.

**2. WS2 — higiena bramki i publikacji flagi (.NET).** Zależności: brak (D-C, D-D, D-H).
Obserwowalna zmiana: **koniec spamu i koniec zatrzaśniętej flagi — niezależnie od tego, że scorer jest wciąż zepsuty.** Po 15 minutach log przy poziomie Debug nie zawiera ani jednej linii `Flag <entity> -> ...` przy niezmienionej wartości (dziś ~4 na 15 s na encję, na poziomie Information), przy starcie każda z pięciu encji publikuje jeden jawny **OFF**, i żadna flaga nie zostaje ON dłużej niż `max_event_duration_sec = 21600` — po watchdogu leci WARN `AlertStormRaised`, a nie cisza. Do naprawienia razem z tym: konflikt z `ScoreStreamPipelineTests.cs:297-343` (`PublishedFlag_AlwaysAccompaniedByScore_AcrossFrozenAndVerdictPaths` woła `PublishFrozenAsync` i asertuje `FlaggedEntities`, więc usunięcie `ScoreStreamPipeline.cs:270-271` bez zmiany tego testu łamie build) — flaga frozen zostaje, ale jako publikacja change-only przez `LastPublishedFlag`, zgodnie z D-H. `HysteresisGate.cs` i jego 10 testów (`HysteresisGateTests.cs`, 118 linii) pozostają bez zmian.
Wydanie: **2.1.13**.

**3. WS5 — backfill z HA Recorder.** Zależności: **WS2** (patrz hazard poniżej). D-K.
Obserwowalna zmiana: **każda encja jest primowana z realnej historii przy otwarciu strumienia.** Po starcie log ma jedną linię `HistoryFetched` (5020) na encję: `lodowkababcia_power` i `zamrazarkapiwnica_power` po ~1400–1700 wierszy przy `SpanHours >= 156`, `load_5m` przy `Commands == 1` (pierwszy 24-godzinny plaster sam przekracza cap 5000 przy 5082 wierszach/dobę). `sensor.disk_use_percent` przestaje tkwić na 135/250. Krytyczne poprawki względem planu: guard „już primowane" wchodzi w konflikt z istniejącym `ScoreStreamPipelineTests.cs:840-873` (`servicer.py:498-500` zwraca `ok=True` również przy `skipped=True`, więc drugie wywołanie już nie dojdzie do `WarmupAsync`) — ten test trzeba przepisać, nie deklarować jako „zostaje zielony"; `catch (OperationCanceledException) when (ct.IsCancellationRequested)` musi poprzedzać blankietowy `catch` z `ScoreStreamPipeline.cs:418`, inaczej rutynowy zapis z UI generuje `WarmupFailed` (5019) i psuje sygnaturę „HA nieosiągalne"; `Channel.CreateBounded<HaReading>(500)` z `ScoreStreamPipeline.cs:112` ma domyślny tryb **Wait**, więc nic się nie gubi — blokuje się JEDYNY task fan-outu i zatrzymuje dostarczanie odczytów dla WSZYSTKICH encji, co wymusza per-encyjny timeout (30 s) na `QueryHistoryAsync`.
Wydanie: **2.1.14**.

**4. WS3 — domyślne wartości, presety i migracja konfiguracji.** Zależności: **WS1, WS2, WS5** (backfill MUSI już działać — patrz hazard poniżej). D-A, D-B, D-E, D-F, D-G, D-I, D-L, D-M, D-N.
Obserwowalna zmiana: **stopa alarmów spada z pięciu wiecznie zapalonych flag do ~5–7 zamkniętych epizodów na dobę**, zgodnie z progami D-J, a każdy próg jest czytelny w UI: pole `high_threshold = 0.5` renderuje „= odchylenie 5,0σ (robust)", `window = 720` renderuje zmierzony rozmiar w czasie ściennym dla TEGO czujnika (~3 h dla `memory_use_percent` przy 15.3 s/próbkę, ~78 h dla `lodowkababcia_power` przy 391 s/próbkę, z ostrzeżeniem >48 h i rekomendacją 240), a `CalibratedBandReadout` pokazuje pasmo w jednostkach czujnika (`Norma: 107 W · alarm poza 92–122 W` przy medianie 107 i MAD ≈ 2 W ⇒ sigma 2.965, z=5). Razem z tym musi wejść D-N (read-back parametrów) — inaczej migracja żyje do pierwszego kliknięcia — oraz D-G (retrakcja starych retained discovery `argus_{slug}_{det}_*` dla KAŻDEJ pary (slug, detektor) sprzed migracji, nie tylko `hst`), `argus/CHANGELOG.md` z notą o rollbacku (`cp /data/entities.yaml.pre-v2.bak /data/entities.yaml`, migracja jest forward-only) i o zmianie znaczenia sensora score, oraz poprawka copy w `SaveResultBanner.tsx:17-20` (dziś mówi „HST … window=250 … ~4 minutes", po zmianie wszystkie trzy liczby są fałszywe).
Wydanie: **2.2.0** (minor, bo to jednokierunkowa migracja konfiguracji).

**5. WS4 — rejestr czujników: duch i 246 brakujących encji.** Zależności: brak techniczna, ale świadomie po **WS3**. F9, F10.
Obserwowalna zmiana: **`sensor.zamrazarkapiwnica_power` pojawia się na ekranie Detektorów** z odznaką „Brak w HA" gdy trzeba, z działającym linkiem Edytuj, i daje się odśledzić bez utraty danych (`GlobExpander` przestaje po cichu kasować z `entities.yaml` encję nieobecną w snapshocie). Stan „jest w `entities.yaml`, nie ma go w `GET /api/sensors`" staje się nieosiągalny konstrukcyjnie. Dla F10 dowiezieniem jest **decyzja, nie liczba**: `GET /api/sensors/diagnostics` + rozszerzona linia `SensorRegistryUpdated` raportują `rawStateCount`, `numericCount`, `nonNumericCount`, histogram nienumerycznych literałów i liczności per domena — `rawStateCount ≥ 400` przy `numericCount ≈ 157` potwierdza H1 (jednorazowy snapshot, naprawiany `UpsertFromEvent` z `state_changed`), `rawStateCount ≈ 157` oznacza H3/H4 i wtedy jedyną drogą jest ręczne D4 (dwa tokeny: `$SUPERVISOR_TOKEN` vs admin), czyli problem po stronie HA, a nie Argusa. **Nie stawiamy twardego progu „≥380 encji w 15 minut"** — encje `number.*_termostat_*` zmieniają się na akcję, nie na poll, więc mechanizm zdarzeniowy tego nie gwarantuje; pomiar prowadzimy przez 24 h. Do dowiezienia razem: dwie nowe metody na `IHaSensorRegistry` łamią **pięć** ręcznych fake'ów (`EntitiesConfigTests.cs:339`, `GroupsEndpointsTests.cs:21`, `NetDaemonHaEventSourceLiveFilterTests.cs:34`, `SaveEndpointJsonTests.cs:20`, `SensorsEndpointJsonTests.cs:22`), a `presentInHa` w `ui/src/api/types.ts` musi być **opcjonalne** (`presentInHa?: boolean`), bo inaczej ~14 fixture'ów wywala `tsc -b` i obraz add-ona się nie zbuduje.
Wydanie: **2.2.1**.

**6. WS6 — symulator replay + wykres w Admin UI.** Zależności: **WS1, WS2, WS3** (i praktycznie **WS5** — bez źródła historii panel pokazuje wyłącznie „Brak źródła historii").
Obserwowalna zmiana: **operator może przetestować parametry na własnej historii, zanim zapisze** — panel „Testuj na historii" w edytorze pojedynczego czujnika odtwarza do 5000 punktów przez PRAWDZIWY scorer Pythona (`SimulateBatch`, model efemeryczny, nigdy nie wpisywany do `DetectorRegistry._detectors`, więc niewidoczny dla `_streaming_keys` i sweepu checkpointów) i PRAWDZIWĄ bramkę .NET, i pokazuje trzy liczby: epizody, % czasu w alarmie (ważony czasem, nie liczbą próbek — kadencje idą od 225 do 5082 próbek/dobę), alerty/dobę. Wymuszone poprawki: `proto/argus.proto` dostaje `SimulateRequest`/`SimulateResponse` (+ `window` w odpowiedzi, inaczej komunikat „Za mało historii do rozgrzewki ({pointCount}/{window} pkt)" jest niezapisywalny) i nowe rpc, `IBatchDetectorClient` dostaje `SimulateBatchAsync` — co łamie **trzy** istniejące fake'i (`BatchSchedulerWorkerTests.cs:39`, `GroupBatchSchedulerTests.cs:46`, `ScoreStreamPipelineTests.cs:1108`); `argus/Dockerfile` musi wreszcie uruchamiać `gen_proto.py` (dziś tylko `COPY detector/`, a `*_pb2*.py` są w `.gitignore` — czysty checkout produkuje detektor, który nie zaimportuje własnych stubów; wzorzec do skopiowania jest w `deploy/Dockerfile.detector:22-27`); `SimulateService` rejestruje się jawną fabryką z `GetService<>()` (wzorzec `Program.cs:156-165`), bo `AddSingleton<SimulateService>()` wysypie się 500-tką przy `influxUrl=null`. Panel resetuje `replayState`/`replayEnabled` w `useEffect` po `[entityId]` (są modułowe, a panel jest per-encja — inaczej po przejściu `#/detectors/sensor/A` → `.../B` widać wykres A pod nagłówkiem B), trzyma 60-sekundowy cache historii per `(entityId, lookback)`, i **nie uruchamia się przy montowaniu**. Do wykonania kryterium „200 odtworzeń pod rząd" (nieperturbacja): pętla `curl` z WNĘTRZA kontenera add-ona (`IsAuthorizedRequest` przepuszcza loopback), nie z UI, które ma debounce 400 ms i `Gate.Wait(0)`; kryterium brzmi „zero NOWYCH katalogów/plików pod `/data/models` i `n_seen` w checkpoincie zgodne z licznikiem żywych odczytów", a nie „zero zmienionych mtime" (przy `ARGUS_CHECKPOINT_INTERVAL_SEC=300` i strumieniującym `load_5m` mtime zmieni się na pewno).
Wydanie: **2.2.2**.

---

### Hazard kolejnościowy (sekcja D2 krytyki) i podjęta decyzja

Zagrożenie: przełączenie domyślnego detektora na `rmad` z zerowym stanem, zanim istnieje backfill, oślepia każdą encję na czas dojścia do `min_samples = 60` z ruchu live. Przy zmierzonych kadencjach to ~17 min dla `load_5m`, ~13 min dla `memory`, ~1 h dla `processor_use`, **~6,5 h dla `lodowkababcia_power` i `zamrazarkapiwnica_power`** (~225 próbek/dobę), a pełne okno bazowe 720 próbek zapełnia się na tych dwóch dopiero po ~78 h. Backfill jest w tej naprawie martwy z definicji (F11: `influxUrl=null`), więc bez WS5 są to realne godziny ciszy dokładnie na czujnikach, gdzie leżą jedyne prawdziwe zdarzenia w całym zbiorze (F3: sprężarka lodówki, precyzja 83%).

**Decyzja: backfill (WS5, wydanie 2.1.14) ships PRZED migracją (WS3, wydanie 2.2.0).**
Konsekwencja pozytywna: migracja przepisuje `entities.yaml`, `Program.cs:490` robi Swap → restart pipeline'u, `ScoreStreamPipeline.cs:320` primuje KAŻDY nowo otwarty strumień, a `registry.warmup_one` kluczuje po `(entity_id, detector)` i pomija wyłącznie klucz z `n_seen > 0` (`registry.py:261-271`) — świeży klucz `rmad` przechodzi więc historię przez ten sam `score_one` i okno `rmad` jest napełnione zanim dotrze pierwszy punkt live — F12 potwierdza pokrycie (1546 wierszy lodówki i ~1575 zamrażarki przez 7 dni, przy potrzebnych 60 na bramkę i 720 na pełne okno). Nie ma okna ślepoty; nie ma też potrzeby dopisywania kryterium „pierwszy werdykt ≤ X h".

Konsekwencja negatywna, świadomie przyjęta i dlatego wymuszająca **WS2 przed WS5** (pozycja 2 przed pozycją 3): backfill z `BackfillRowCap = 5000` primuje również `sensor.disk_use_percent` (dziś 135/250) i tym samym ODBLOKOWUJE jego flagę, która pod wciąż nienaprawionym `hst` jest — na mocy F2 — flagą, która nie może zgasnąć. Gdyby WS5 poszło przed WS2, dostalibyśmy szósty na stałe zapalony `binary_sensor`. Przy podjętej kolejności watchdog z D-D (`max_event_duration_sec = 21600`) już działa, więc `disk_use_percent` w najgorszym razie cykluje 6 h ON / 1 h wstrzymania z WARN-em `AlertStormRaised`, zamiast się zatrzasnąć — i przestaje to być problemem w ogóle po WS3. Dodatkowo priming zasiewa nieograniczony `MinMaxScaler` (F5) 7-dniową historią zawierającą skok 13.01, czyli odtwarza zapaść pasma NATYCHMIAST po zimnym starcie zamiast po godzinach; to jest kolejny powód, dla którego okno między WS5 a WS3 ma być krótkie i dlaczego minimum wyników per encja trzeba spisać po WS5 (jako pomiar, nie jako założenie).

## 4. Workstreamy

### WS1 — Detektor: scoring + kalibracja (Python)

**Cel** — dodać `RmadDetector` (rolling median/MAD robust-z, stdlib) jako nowy silnik scoringu pod kluczem rejestru `rmad`, tak by publikowany score był ograniczony, bezwymiarowy i kalibrowany per-encja (`score = z/(z+5)`), usuwając F5/F4/F6/F7 ze ścieżki domyślnej; `hst` zostaje nietknięty jako opt-in/rollback.

**Decyzja tożsamości (rozstrzyga blokera weryfikatora)** — `UniqueId.cs:13-18` (`argus_{slug}_{detector}_anomaly` / `_score`) czyta `DiscoveryPublisher.cs:224` → `entity.Detectors[0].Name`. Dlatego **`detectors[0].name` w entities.yaml przechodzi na `"rmad"`**, a tożsamość MQTT zostaje odcięta od detektora (D-G/WS3: `AnomalyId => argus_{slug}_anomaly`). Wariant „name zostaje hst" jest ODRZUCONY: `DiscoveryPublisher.cs:48-51` wstawia detektor w `unique_id`/`object_id`, a `state_topic` (`argus/{slug}/flag/state`) go nie ma, więc pierwszy wybór `mad`/`stl` z pickera (`InputValidator.cs:26 KnownDetectors`) dokłada DRUGĄ encję HA na tym samym temacie, a `RetractAsync` (`DiscoveryPublisher.cs:171-187`) tego nie sprząta, bo biega tylko dla `removedEntities`. WS2 przepisuje `ScoreStreamPipeline.cs:445-452` (`d.Name=="hst"` → `RmadParams`), a WS3 dokłada `"rmad"` do `InputValidator.cs:26`. Na drucie algorytm jedzie nadal jako `params["algorithm"]` (proto bez zmian), ale jego ŹRÓDŁEM jest `detectors[0].name`. Klucz rejestru i katalog checkpointu = algorytm (`/data/models/<slug>/rmad/`), rozłączny z `<slug>/hst/`. Rollback = `name: hst` w entities.yaml — `unique_id` się nie zmienia.

**Zmiany**
- [create] `detector/argus_detector/rmad_detector.py` — ~200 linii, stdlib (`bisect`, `deque`, `math`, `logging`). Stałe: `_DEFAULT_WINDOW=720`, `_DEFAULT_MIN_SAMPLES=60`, `_DEFAULT_SCALE_FLOOR=0.0`, `_Z_SCALE=5.0` (stała, nie param — `z_scale` i `high_threshold` to ten sam stopień swobody), `_MAD_CONST=1.4826`, `_SCHEMA_VERSION=1`. `_cast_int`/`_cast_float` kopiowane lokalnie z `hst_detector.py:27-35` (Rule 11, bez ekstrakcji). Stan: `_schema,_values(deque),_sorted(list),_n_seen,_window,_min_samples,_scale_floor` — picklowalny, deepcopy-safe (`registry.py:201`, `model_store.py:264`).
  - `from_params(params)` — klucze `window,min_samples,scale_floor`; klucze `algorithm`/`detector` są **odfiltrowane** przed czytaniem, żeby nie wchodziły do odcisku `apply_params`.
  - `_insert`: `bisect.insort` + `while len(_values) > _window:` pop-left i `_sorted.pop(bisect_left(...))` (`while`, nie `if` — drenaż po zmniejszeniu okna).
  - `_mad_sorted(w, med)` — dokładny MAD w O(n/2) bez sortowania: `lo=bisect_left`, `hi=bisect_right`, emisja `(hi-lo)` zer, potem dwuwskaźnikowy marsz na zewnątrz do rangi `n//2`.
  - `score_one(value)` — score-then-learn (jak `hst_detector.py:85-86`): (1) `len(_sorted)<_min_samples` → insert, `return 0.0`; (2) `sigma=1.4826*MAD`; (3) drabina skali: rung1 MAD → rung2 `MeanAD` gdy `sigma<=0` (mierzone: pół-najmniejszej-luki daje lodówce z=2.0 i ZERO zapłonów) → rung3 `max(sigma,_scale_floor)` → rung4 `sigma<=0` → `0.0` jeśli `value==med`, inaczej `1.0` + `logger.warning` raz na instancję; (4) `z=|value-med|/sigma`, `score=z/(z+5.0)`; (5) insert, `_n_seen+=1`.
  - `score_batch(values)` — **niemutujący** (kopie `_values/_sorted/_n_seen` do lokalnych), zwraca **gołą listę** (`registry.py:369-372` czyta 2-krotkę jako `(scores,error)`). To jedyne uzasadnienie dla `fit(values)` (offline replay) — **nie** "Fit RPC rzuca": `servicer.py:162` to `fit_one` w cold-starcie ScoreBatch, `servicer.py:184-186` przerywa UNARNE ScoreBatch (nie stream), a Fit RPC `servicer.py:137-139` zwraca `FitResponse(ok=False)`.
  - `apply_params(params)->bool` — porównanie 3-krotki (O(1) fast path), przy zmianie drenaż okna. Naprawia „params tylko przy tworzeniu" podwójnie: `registry.py:78-81` + `model_store.py:398`→`register_checkpoint(registry.py:403-422)`.
  - `__setstate__` — (a) `_schema > _SCHEMA_VERSION` → `ValueError` (łapane per-encja w `model_store.py:403-408`), (b) `setdefault` każdego pola, (c) self-heal: `sorted(_values) != _sorted` → przebudowa z deque (wyścig `registry.py:201` vs `:106`). Bez `raise` w (c) — `__setstate__` biegnie też przy każdym deepcopy.
  - `window` property zwraca `_min_samples` (denominator chipa), `baseline_window` → `_window`. Konsekwencja do udokumentowania: `EntityRuntimeState.cs:48-54` seeduje `WarmUpWindow` z konfiguracji, więc `DetectorListRow.tsx:76` pokaże „Rozgrzewka N/720" do pierwszego werdyktu i „N/60" potem → WS2 ma seedować z `min_samples`.
  - Zmierzone: 36.3 µs/pkt @720, pickle 13 077 B, deepcopy 0.27 ms (vs HST 200 KB–1.2 MB / 56–96 ms, `test_checkpoint.py:47-67`).
- [modify] `detector/argus_detector/registry.py` — 8 edycji. (1) po `logger` (:23) `STREAMING_DETECTORS = frozenset({"hst","rmad"})`. (2) `_get_or_create` (:69-81): `if key not in self._detectors: self._detectors[key]=self._create_detector(detector, params)`, potem `apply=getattr(det,"apply_params",None)` i wywołanie gdy `params` — guard `getattr` zostawia EntityDetector/PyOD/Stl bajt-identyczne; adnotacja zwrotu `-> EntityDetector` (:69-74) → union; docstring `:92-94` („params only at creation time") poprawiony. (3) `_hst_keys`(:154-161) → `_streaming_keys`, `key[1] in STREAMING_DETECTORS`, jedyny caller `:190`. (4) docstring `checkpoint_dirty` `:164`. (5) `warmup_one` `:268`: `else self._create_detector(detector, params)` **plus** guard `if detector not in STREAMING_DETECTORS: detector="hst"` — bez niego ścieżka, która nigdy nie rzucała, dostaje `ValueError` (`:481`) lub `AttributeError` na `:270`. Reszta nietknięta: bramka `n_seen==0` (:265), lock (:263-272), nieustawianie `_last_checkpointed` (:243-246). (6) `_create_detector` (:445-481): gałąź `hst` (:471-472) `return EntityDetector.from_params(params or {})` (dziś gubi params); nowa gałąź `rmad` z leniwym importem. **Bez WARN w fabryce** — `fit_one` (:307,:326,:328) generowałby spam przy każdym boocie; WARN żyje w servicerze, raz na `(entity_id, algorithm)`. (7) docstring klasy `:29-48` i fabryki `:448-455`. (8) `_create_detector` dalej rzuca `ValueError` przy nieznanej nazwie — to jedyna ścieżka nieosłonięta dla bezpośredniego `registry.score_one(..., detector=<nazwa grupy>)`; udokumentowane i przypięte testem.
- [modify] `detector/argus_detector/servicer.py` — tylko `ScoreStream` (:42-107). **Bez zmiany proto** (`params` to `map<string,string>`, `proto/argus.proto:12`; precedens `pyod_detector.py:63-65`; `argus/Dockerfile` nie odpala `gen_proto.py`, `*_pb2*.py` w .gitignore). Po `value = point.value.value` (:61): `params=dict(point.params)`; `algo = params.get("algorithm") or params.get("detector") or "hst"`; jeśli `algo not in registry_module.STREAMING_DETECTORS` → `logger.warning(...)`, `algo="hst"`; jeśli `algo=="hst"` → WARN raz na encję (F4/F5, „legacy/niekalibrowany"). Potem `:66` `score_one(..., detector=algo, params=params)`, `:71` `get_warmup_state(entity_id, algo)`, `:80` `detector=algo`, `:96` `extra["detector"]=algo`. Fallback jest **obowiązkowy**: `:104-107` zamienia dowolny wyjątek w `context.abort(INTERNAL)` i zrywa CAŁY bidi stream.
- [modify] `detector/argus_detector/hst_detector.py` — **tylko docstring** (:1-14): blok KNOWN DEFECTS z pomiarami — F5 (`learn_one` przed `transform_one`, :83-84; 0.54 → 0.0032 po skoku 13.01), F4 (`1 - mass/max_mass`; 101 W → 0.997 vs modalne 107 W → 0.560), F6/F7; oraz poprawka nieaktualnej linii 6 o domyślnych D-09.
- [delete] `detector/argus_detector/normalizer.py` — `OnlineMinMaxScaler` (:11-32), zero importerów, zero testów, docstring kłamie („clips to [0,1]" — 13.01 → 125.1).
- [modify] `detector/tests/test_registry.py`, `test_checkpoint.py`, `test_warmup.py`, `test_servicer.py` — testy niżej; w `test_servicer.py:226` (`test_params_honored_at_creation_time_only`) komentarz, że kontrakt jest **świadomie uchylony dla rmad** (zostaje zielony przez guard `getattr`).
- [create] `detector/tests/fixtures/real_24h.json` + jednorazowy skrypt zrzutu — 5 serii z HA Recorder (`history/history_during_period`, F12: 7 dni). Warunek finalnego odbioru F13 (patrz DEPENDS).

**Testy**
- `test_rmad_detector.py::TestExactMad::test_mad_merge_walk_equals_statistics_median...` — 3000 losowych okien (gauss, histogram F4 {101,103,105,107,109}, bimodal {0,984}), n∈1..60, parzyste i nieparzyste; równość do 1e-9. Reguła: skala jest mianownikiem każdego score — błąd o jeden cicho przeskalowuje wszystkie alarmy. Zweryfikowane: 0/3000.
- `::TestF4RarityInversion::test_quantized_level_scores_are_monotone_in_deviation_and_never_fire` — 5000 losowań z histogramu F4; `max score < 0.5`; średnie ściśle malejące w |x−107|: 101>103>105>107, 105==109. Zmierzone: max 0.288; 0.288/0.213/0.119/0.000/0.119; 0 epizodów. Reguła: score = „daleko od normy", nie „rzadko widziane".
- `::TestF2ReleaseIsReachable::test_sustained_level_shift_releases_within_one_window` — 900 pkt @20.0±0.05, skok trwały do 30.0; pierwszy po-skoku `>0.5` i jakiś `<0.375` w ciągu 720 próbek. Zmierzone: zwolnienie na próbce 360. Reguła: nieskończony epizod strukturalnie niemożliwy (F1/F2).
- `::TestF5NormalBandSurvivesAnExtreme::test_spike_does_not_collapse_subsequent_normal_scores` — 500 pkt 0.50–0.60, skok 13.01, 200 pkt 0.50–0.60; skok `>0.5`, KAŻDY po-skoku `<0.375`. Zmierzone: 0.993 / max 0.333. Reguła: żaden bieżący ekstremum nie może skompresować pasma.
- `::TestF3RecallPreserved::test_compressor_transition_fires_and_baseline_is_silent` — 1546 pkt (F12), dwa biegi po 90 pkt 984.0 W. **Poprawione wartości**: pierwszy pkt biegu trafia w rung 4 (okno idealnie stałe, MeanAD=0) → dokładnie `1.0`; rung 2 działa od drugiego pkt (`MeanAD=984/720`, z=720); zwolnienie następuje gdy bieg SIĘ KOŃCZY i okno napełnia się zerami (nigdy „pół okna = 984 W"). Asercje: **2 epizody, 11.6% on-time, wszystkie 90 pkt obu biegów > 0.5 (144 pkt >0.5 łącznie), baseline dokładnie 0.0**. Reguła: jedyny czujnik z realną precyzją (83%) nie może wyparować.
- `::TestDegenerateScale::test_constant_window_returns_zero...` / `..._break_scores_one` — 800 identycznych → same 0.0, brak wyjątku; 720 identycznych + 1 inny → 1.0. Reguła: MAD==0 nie może dać `ZeroDivisionError` na gorącej ścieżce (`servicer.py:104-107`).
- `::TestDegenerateScale::test_scale_floor_damps_a_low_noise_quantized_series` — **NOWY, z werdyktu**: 5653 pkt serii procentowej z 1 miejscem po przecinku (kształt `memory_use_percent`), poziomy co 0.1, okazjonalne ruchy 1 pp. Asercje: `scale_floor=0.0` → ≥3 epizody (zmierzone 4 / 7.02% on-time, max score 0.597); `0.05` i `0.1` → identycznie 4/7.02%; `0.3` → 0 epizodów / 0%. Reguła: `scale_floor` to podłoga na sigma w rung 3, więc **tłumi też rung 1** — mechanizm, który realnie gryzie, to MAD=0.1 → sigma=0.148 → ruch 1.1 pp = z=7.4.
- `::TestDegenerateScale::test_scale_floor_suppresses_a_one_lsb_quantisation_step` — 1000 pkt 45.2 → 1000 pkt 45.3 (kształt `disk_use_percent`): floor 0.0 → dokładnie 1 ograniczony, samo-gasnący epizod (18.8% on-time); floor 0.5 → 0.
- `::TestWarmUp::test_cold_phase_returns_exact_zero_until_min_samples` — dokładnie 0.0 przez `min_samples-1`, `is_warmed_up` przełącza na `n_seen==60`, `window==60`, `baseline_window==720`. Reguła: `ScoreStreamPipeline.cs:227` tłumi flagę na `!warmed_up`; 0.0 przechodzi po drucie tylko bo score to `DoubleValue` (`test_score_zero_wire.py:20-41`).
- `::TestScoreBatchIsPure::test_score_batch_does_not_mutate_the_live_model` — `n_seen`/`_values`/`_sorted` bajt-równe przed i po; zwrot to `list`, nie `tuple`. Reguła: `registry.py:359-369` podaje ŻYWY model.
- `::TestApplyParams::test_window_change_takes_effect_on_a_checkpoint_restored_instance` — pickle/unpickle @720 z 720 pkt, `apply_params({'window':'250'})` → `len(_values)==len(_sorted)==250`, zwrot `True`; bez zmiany → `False` i zero mutacji.
- `::TestCheckpointCompat::test_setstate_rebuilds_sorted_from_values_on_mismatch` oraz `::test_setstate_rejects_a_newer_schema_and_fills_missing_fields` — self-heal z deque; `_schema+1` → `ValueError`; brakujące pole → default, brak `AttributeError` w `score_one`. Reguła: `model_store.py:305` (równość `river_version`) nigdy nie odpala dla rmad.
- `test_checkpoint.py::TestCheckpointDirty::test_rmad_entities_are_checkpointed_and_mad_is_still_skipped` — istnieją `<slug>/rmad/checkpoint.pkl` i `<slug>/hst/checkpoint.pkl`, nie istnieje `<slug>/mad`. Rozszerza `test_only_hst_entities_are_checkpointed` (:198), nie zastępuje.
- `test_checkpoint.py::TestPickleSizeAndDeepcopyLatency::test_rmad_pickle_size_and_deepcopy_latency` — 2000 pkt @720: pickle < 32 768 B, deepcopy < 5 ms (zmierzone 13 077 B / 0.27 ms). Reguła: trzymanie locka w `registry.py:194-201`.
- `test_registry.py::TestRegistryCreateDetector::test_create_detector_rmad_and_hst_honour_params` — `_create_detector('rmad',{'window':'99'}).baseline_window==99` **i** `_create_detector('hst',{'window':'50'})._model.window_size==50`.
- `test_registry.py::TestRegistryPerEntityIsolation::test_score_one_dispatches_on_detector_name` — `detector='rmad'` tworzy `RmadDetector` pod `('sensor.a','rmad')`; `detector='mad'` NIE tworzy `EntityDetector`.
- `test_registry.py::...::test_direct_score_one_with_unknown_detector_raises_valueerror` — przypina, że `registry.py:481` jest teraz osiągalne ze ścieżki score i że osłonę ma wyłącznie servicer.
- `test_servicer.py::TestScoreStreamDetectorSelection::test_point_params_algorithm_selects_rmad` — `params={'algorithm':'rmad','window':'720'}` → `Verdict.detector=='rmad'`, `warmed_up/n_seen/window` z klucza `('entity','rmad')`; Point bez params → `'hst'` (addytywność na drucie).
- `test_servicer.py::...::test_unknown_algorithm_falls_back_to_hst_without_aborting_the_stream` — 3 encje na jednym streamie (rmad, `does_not_exist`, rmad) → TRZY werdykty, B ma `'hst'`, `context.abort` nigdy nie wołane. Największy blast radius w całym WS.
- `test_warmup.py::TestWarmupOnePrimesRmad::test_warmup_primes_rmad_window_and_stays_idempotent` — buduje `RmadDetector`, wypełnia okno, `skipped=False`, klucz poza `_last_checkpointed` (:243-246); drugie wywołanie `skipped=True`. Plus `test_warmup_one_unknown_detector_degrades_to_hst` (guard z edycji 5).

**Kryteria akceptacji**
- **F4 usunięte**: replay histogramu F4 przez `score_batch` → średnie 0.288/0.213/0.119/0.000/0.119, ściśle malejące w |x−107|, max < 0.5. Dziś 0.997/0.988/0.663/0.560/0.882.
- **F5 poza ścieżką domyślną**: po ekskursji 13.01 każdy kolejny normalny pkt < 0.375 (zmierzone max 0.333). Dodatkowo `grep -rn 'MinMaxScaler' detector/argus_detector/` daje trafienia **wyłącznie** w `hst_detector.py`.
- **F2 strukturalnie niemożliwe**: po trwałym skoku poziomu jakiś score < 0.375 w ciągu ≤720 próbek (zmierzone: 360).
- **F7 odpowiedziane, nie odroczone**: kalibracja JEST estymatorem skali, przeliczanym co tick nad 720-próbkowym oknem; brak osobnej fazy i żadna nie jest dodawana. `is_warmed_up` przełącza na `n_seen>=60`, `Verdict.window==60`. Czas do pierwszego werdyktu z ruchu live: load_5m ~17 min, memory ~13 min, processor_use ~1 h, lodowkababcia ~1 h, zamrazarkapiwnica ~6 h.
- **F6 rozpuszczone**: kontrakt score jest dokładny — `score>0.5 ⇔ z>5`, `score<0.375 ⇔ z<3`, identycznie na każdym czujniku. WS2 ustawia `high_threshold=0.5`, `low_threshold=0.375`, `min_consecutive=3` (mieszczą się w `InputValidator.cs:107-125`), bez zmiany kodu `HysteresisGate.cs`.
- **F3 (precyzja) — jawny próg**: `lodowkababcia_power` zachowuje oba rzeczywiste zdarzenia sprężarki (2 epizody, wszystkie 90 pkt każdego biegu > 0.5), `zamrazarkapiwnica_power` daje **0 alarmów**. To są jedyne progi precyzyjne, jakich WS1 broni; liczba globalna nie jest kryterium odbioru WS1, bo F3 definiuje prawdę przez robust-z z MAD, a ten detektor liczy robust-z z MAD — pomiar byłby częściowo samopotwierdzający.
- **F13 — DEGRADOWANE do „na fikstursach"**: zmierzone syntetycznie (window 720, min_samples 60, scale_floor 0, bramka 0.5/0.375/3): zamrazarka 0 ep/0%, memory **4 ep/7.02%** (nie 0 — patrz test `scale_floor`), load_5m 3 ep/0.83%, processor_use 1 ep/2.97%, lodowka 2 ep/11.6%. Odbiór na realnych seriach jest **odroczony** do `fixtures/real_24h.json`.
- **`scale_floor` jest wymagany, nie opcjonalny**: WS2 musi ustawić `scale_floor=0.3` dla `memory_use_percent`, `processor_use` i `disk_use_percent` (serie procentowe z 1 miejscem po przecinku). Przy 0.0 te trzy czujniki dają 4 ep/7.02%; 0.05 i 0.1 nic nie zmieniają.
- **Params realnie docierają**: dla encji odtworzonej z checkpointu Point ze zmienionym `window` mierzalnie skraca baseline na następnym punkcie (`apply_params` → `True`). Dziś niemożliwe.
- **Brak regresji, brak zależności**: `cd detector && python -m pytest -q` — 264 istniejące zielone + nowe, **dokładnie 1 skip** (Windows, `test_restart_resilience.py:129`); większa liczba skipów = porażka (Rule 12). `requirements.txt`/`pyproject.toml` bez zmian → bramki CI (torch-free, <2 GB, arm64 bez source-buildów, bookworm/py3.11) nietknięte.
- **Rollback bez nowych encji HA**: po D-G `unique_id` z `UniqueId.cs:13-18` to `argus_{slug}_anomaly`, niezależne od nazwy detektora, więc powrót `rmad → hst` nie tworzy ani jednej encji i nie zostawia sierot. 24 h historii traci się RAZ, przy migracji (§7 #1), nie przy rollbacku. Test: scoring encji pod obiema nazwami daje dwa rozłączne katalogi checkpointów; powrót do `hst` wskrzesza model z `n_seen` (load_5m 16061, memory 17824, processor 11380, lodowka 707).
- **Stream nieuśmiercalny przez jedną encję**: Point z nieznanym algorytmem daje werdykt (fallback hst + WARN), nie `abort`.

**Ryzyka**
- **OTWARTE, blokujące produkcyjnie (nie do zamknięcia w WS1)**: `ScoreStreamPipeline.cs:384` `new WarmupRequest{ Detector="hst" }` jest zaszyte — priming rmad **nie działa za darmo**; `servicer.py:480` rozwiąże `"hst"` i napełni EntityDetector pod złym kluczem. WS2 musi wysyłać algorytm. Analogicznie `ScoreStreamPipeline.cs:432-437` (`BuildHstParamsMap` filtruje do `window`+`n_trees` — musi przepuścić `algorithm/window/min_samples/scale_floor`), `:237` `new RecentAnomaly(..., "hst", ...)`, `Program.cs:497` `hasHst`, `EntityRuntimeState.cs:48-54` (seed `WarmUpWindow` z `min_samples`), `Web/DetectorDefaults.cs:25` + `ui/src/validation/detectorParams.ts` + `DetectorParamGrid.tsx` (mirrory params). **WS1 jest bezczynny do czasu ich wylądowania — sam w sobie nie naprawia niczego widocznego dla operatora.**
- **Drabina skali rung 2 (MeanAD) to decyzja na podstawie pomiaru, nie teorii**: pół-najmniejszej-luki daje lodówce z=2.0 i ZERO zapłonów. Koszt: gruboziarnisty czujnik alarmuje raz na trwały skok 1 LSB (zmierzone 18.8% on-time przy floor 0).
- **To detektor poziomu i nic więcej**: nie widzi zmiany wariancji przy stałym poziomie, anomalii kształtu/fazy ani powolnego dryfu (śledzi go z definicji). Trwała, nie-płaska usterka alarmuje najwyżej `window` próbek i milknie. `FrozenSensorDetector` jest wyłączony (D-H, `frozen_variance_threshold: 0.0`), więc idealnie płaska seria nie ma dozorcy — §7 #8.
- **Okno w próbkach, nie w czasie**: 720 próbek = ~3.4 h dla load_5m i ~75 h dla zamrazarki. `river.utils.TimeRolling` odrzucone (komplikuje eviction, pickle i kontrakt `n_seen/window`).
- **`apply_params` mutuje pod grubym lockiem** `_get_or_create` (`registry.py:78`), trzymanym na KAŻDYM punkcie, podczas gdy `score_one` mutuje poza wszelkim lockiem (`:106`, przypięte jako lock-free w `test_checkpoint.py:229-256`). Fast path to O(1), ale to nowe przeplecenie na najgorętszej ścieżce.
- **Okno rozdartego deepcopy skrócone, nie zamknięte** — rmad mutuje DWA kontenery; `__setstate__` leczy, deepcopy spada z 56–96 ms do 0.27 ms (~250×), ale wyścig `:201` vs `:106` zostaje.
- **`params['algorithm']` to hack drutowy** — uzasadniony precedensem `pyod_detector.py:63-65` i luką `argus/Dockerfile`/`gen_proto.py`, ale mniej odkrywalny niż pole w Point; przyszła promocja do pola = dwie ścieżki na jedno wydanie.
- **Znaczenie publikowanego score się zmienia** (masa rzadkości HST → zduszony robust-z). Szereg historyczny w HA staje się nieporównywalny przez granicę upgrade'u; nic nie ostrzega konsumentów. `DashboardPage.tsx` 0.8/0.5 dalej działa i wreszcie coś znaczy (0.5=z5, 0.8=z20).
- **Liczby per-czujnik pochodzą z fikstur** kształtowanych do opublikowanych statystyk; tylko histogram zamrazarki jest dokładnym pomiarem (F4). F13 nie podaje okna swojego wariantu MAD-on-raw.
- **Brak siatki bezpieczeństwa CI**: `.github/workflows/build.yml` odpala się tylko na tagach `v*`, ostatnie osiem wydań zbudowano lokalnie bez tagu, a suite Pythona nie biega w CI w ogóle.
- **Ryzyko percepcji**: przejście z pięciu wiecznie zapalonych flag do niemal ciszy wygląda jak awaria; brak widoku historii score w SPA (`AttributionBar.tsx:9` — świadoma decyzja o braku biblioteki wykresów).

**Poza zakresem**
- **Brak zmiany proto** — `proto/argus.proto` nietknięty: żadnego `Point.detector`, żadnych pól `calibrated/raw_score/robust_z/thresholds`, brak regeneracji stubów po żadnej stronie; luka `argus/Dockerfile` nie jest ani wykorzystywana, ani naprawiana.
- **Brak zmiany `model_store.py`** — bez `state_version`, bez nowego guardu ładowania. rmad używa `save_checkpoint`/`load_checkpoint` verbatim na rozłącznym `<slug>/rmad/`. Zaakceptowana nadmierna ostrożność: `river_version` (`:305`) bramkuje też rmad, kosztem jednego re-warmu na bump river.
- **Brak naprawy `EntityDetector`/HalfSpaceTrees** — sam docstring. `hst` zostaje verbatim jako opt-in/rollback; brak `limits={'value':(0,1)}`, brak ograniczonego skalera, brak chronionego okna kalibracji, brak ECDF. Odejście od propozycji Calibrated-HST jest oparte na jej własnym prototypie: inwersja F4 tam **przeżywa** (101 W → 0.974 vs 107 W → 0.538), a sama gałąź kwantylowa zostawia memory na 37 epizodach / 35.5% on-time — czyli decyduje MAD, więc MAD ma być detektorem.
- **Brak wariantu sezonowego** (`rmad_seasonal`, kubełki per pora dnia, `tz_offset_minutes`, konsumpcja `Point.timestamp`) — żaden z 5 zmierzonych czujników go nie uzasadnia, potrzebuje ≥5 dni historii przy martwym backfillu (F11), Rule 2.
- **Brak zmian w orchestratorze** — `HysteresisGate.cs`, `FrozenSensorDetector.cs`, `ScoreStreamPipeline.cs`, `EntityRuntimeState.cs`, `EntitiesConfig.cs`, `InputValidator.cs`, `DetectorDefaults.cs`, `StatePublisher.cs`, `BatchSchedulerWorker.cs`, **`UniqueId.cs` i `DiscoveryPublisher.cs`** (jawnie wymienione — tożsamość MQTT tnie D-G, ale robi to WS3, nie WS1) to WS2/WS3. WS1 deklaruje wyłącznie kontrakt score i cztery params do przekazania.
- **Brak pracy nad configiem/migracją/UI** — bez `schema_version`, `EntitiesSchemaMigrator`, zmiany `gen-entities.py`, mirrorów `DETECTOR_DEFAULTS`/`DetectorParamGrid`/`detectorParams.ts`, bez read-backu `detectors` w `GET /api/sensors`. Stojący hazard dla WS2: `sensors.ts:54-61` seeduje czyste defaulty, a `POST /api/sensors/save` to podmiana całej listy, więc dowolny zapis z dowolnego ekranu cofnąłby wybór algorytmu.
- **Brak pracy nad HA Recorder / backfillem (F11/F12)** — `warmup_one` primuje rmad za darmo, gdy źródło historii już istnieje, ale `history/history_during_period`, limit ramki 4 MB i rejestracja `IInfluxDataSource` to osobny workstream. **DEPENDS ON (tylko dla finalnego odbioru F13, nie dla wylądowania kodu): WS5 lub lokalny zrzut do `fixtures/real_24h.json`.**
- **F9, F10, F8 nietknięte** — nic w `detector/argus_detector/` nie filtruje encji ani nie trzyma stanu flagi.
- **Brak sandboxa/rejestru symulacji i nowego RPC** — `score_batch` jest niemutujący, więc offline replay nie potrzebuje ani przestrzeni nazw sandboxa, ani `SimulateBatch`.
- **Brak refaktoru współdzielonych utili** — `_cast_int`/`_cast_float` duplikowane lokalnie; poza ośmioma nazwanymi edycjami nic w `registry.py`/`servicer.py` nie jest porządkowane (Rule 3).

### WS2 — Bramka zdarzen + higiena publikacji (.NET)

**Cel** — zastapic bezwzgledna bramke progowa warstwa zdarzen per-encja (ranga score w wlasnym oknie + robust-z na surowej wartosci + min-duration/refractory/cap/watchdog) i publikowac flage tylko na przejsciu, tak by F1/F2/F8 byly strukturalnie niemozliwe bez zadnej zmiany po stronie Pythona, proto ani checkpointow.

**Zmiany**
- [create] `orchestrator/Argus.Orchestrator/Detection/RollingRank.cs` — `internal sealed class RollingRank`: `double[] _buf; int _head; int _count`. `RollingRank(int windowSize)` (ArgumentOutOfRange < 1), `Count`, `double RankOf(double s)`, `void Push(double s)`. RankOf: `_count==0 → 0.0`; jeden skan po `_count` slotach liczacy `lt`(x<s), `eq`(x==s); zwrot `(lt + 0.5*eq)/_count` (mid-rank — F4 daje ~5 poziomow score na `sensor.zamrazarkapiwnica_power`, strict-less odwrocilby rangi). Push: `_buf[_head]=s; _head=(_head+1)%Length; if(_count<Length)_count++`.
- [create] `orchestrator/Argus.Orchestrator/Detection/RollingRobustZ.cs` — `_buf`, `_scratch`, `_head`, `_count`. `ZOf(double x)`: `_count<10 → 0.0`; kopia `_count` slotow do `_scratch`, `Array.Sort(_scratch,0,_count)`, **Q1/Q3 zapisac TERAZ, przed nadpisaniem** (`q1=_scratch[_count/4]`, `q3=_scratch[3*_count/4]`), `med=Median`; nadpisanie `_scratch[i]=Math.Abs(_scratch[i]-med)`, drugi Sort, `mad=Median`. Drabina skali, pierwsza dodatnia wygrywa: `1.4826*mad` → `(q3-q1)/1.349` → `StdDev(_buf, 0, _count)` (**tylko zywe sloty** — pelna tablica wliczylaby zera i zepsula skale dla 0/984 W) → `0.0` = abstynencja (plaska seria nie ma dozorcy — `FrozenSensorDetector` wylaczony przez D-H, `frozen_variance_threshold 0.0`; §7 #8). Zwrot `Math.Abs(x-med)/scale`. 2x Sort 720 double ≈ 40 us przy <=0.07 probki/s/encja.
- [create] `orchestrator/Argus.Orchestrator/Config/AlertParams.cs` — `public sealed record AlertParams` nad ta sama mapa `DetectorConfig.Params` co HstParams (`EntitiesConfig.cs:69-81`, idiom GetInt/GetDouble/InvariantCulture `:83-88`). Klucze/domyslne: `alert_mode="adaptive"` ("adaptive"|"legacy"), `evidence_mode="any"` ("any"|"both"|"score_only"|"raw_only"), `rank_window=720`, `q_fire=0.99`, `q_clear=0.80`, `raw_window=720`, `z_fire=5.0`, `z_clear=3.0`, `min_consecutive=3` (ISTNIEJACY klucz), `alert_min_samples=240`, `min_duration_sec=120`, `refractory_sec=600`, `max_events_per_hour=4`, `max_event_duration_sec=21600`, `storm_hold_sec=3600`. Brak klucza = default (nigdy blad).
- [create] `orchestrator/Argus.Orchestrator/Detection/AlertPolicy.cs` — `record AlertDecision(bool FlagOn,bool EventStarted,bool EventEnded,bool Storm,double Rank,double RawZ,string Channel)`. Stan: `_firing`, `_consecAbove`, `_consecBelow`, `_samples`, `_lastRawZ`, `DateTimeOffset _eventStartedAt = DateTimeOffset.MinValue`, `_holdUntil`, `_lastEventEndedAt = MinValue`, `_stormUntil = MinValue`, `List<DateTimeOffset> _eventStarts`, `object _gate`. API: `ObserveValue(v)` (`_lastRawZ=_raw.ZOf(v); _raw.Push(v)`), `SeedValue(v)` (tylko Push), `SeedHistory(IReadOnlyList<double>)`, `OnVerdict(score,warmedUp,suppressed,frozen,now)`, `bool? LastPublishedFlag`, `SampleCount`, `RawSampleCount`, `Calibrated`, `LastRawZ`, `State`. Kazde cialo w `lock(_gate)` (ObserveValue = watek write-loop `ScoreStreamPipeline.cs:153-164`, OnVerdict = read-loop `:167-189`); lock nigdy nie trzymany przez await — klasa nie robi I/O, `now` jest parametrem (wzor `ReconnectCooldown.cs:16/26`).
  - OnVerdict: `bool started=false, ended=false, storm=false;` na wejsciu. 1) `rank=_rank.RankOf(score); _rank.Push(score); _samples++`. 2) `calibrated = _samples>=AlertMinSamples && _rank.Count>=50` (przy mid-rank max ranga = `1-0.5/Count`, wiec 0.99 jest arytmetycznie nieosiagalne ponizej 50). 3) `if(!frozen && (!warmedUp || !calibrated)) { if(_firing){ ended = Close(now); } return new AlertDecision(false,false,ended,false,rank,_lastRawZ,"none"); }` — **Close TYLKO gdy `_firing`**, inaczej `_lastEventEndedAt` stemplowane co tick kalibracji i pierwszy fire wpada w galaz refractory. 4) evidence: `scoreHigh=calibrated && rank>=QFire`, `scoreLow=!calibrated||rank<QClear`, `rawHigh=_raw.Count>=10 && _lastRawZ>=ZFire`, `rawLow=_raw.Count<10||_lastRawZ<ZClear`; any → `fire=scoreHigh||rawHigh||frozen`, `clear=scoreLow&&rawLow&&!frozen`; both → `fire=(scoreHigh&&rawHigh)||frozen`, `clear=(scoreLow||rawLow)&&!frozen`; score_only/raw_only zeruja druga strone. 5) Clear: `suppressed → _consecAbove=0`; inaczej inkrement; po `>=MinConsecutive`: reset; `now<_stormUntil` → nic; `EventsInLastHour(now)>=MaxEventsPerHour` → `_stormUntil=now+StormHoldSec; storm=true`; inaczej `_firing=true; _holdUntil=now+MinDurationSec;` **oraz `if(_eventStartedAt==MinValue || (now-_lastEventEndedAt)>=RefractorySec){ _eventStartedAt=now; _eventStarts.Add(now); started=true; }`** — warunek `==MinValue` chroni pierwszy w ogole event przed natychmiastowym watchdogiem. 6) Firing: `_consecBelow = clear?+1:0`; `(now-_eventStartedAt)>MaxEventDurationSec` → `ended=Close(now); storm=true; _stormUntil=now+StormHoldSec`; inaczej `_consecBelow>=MinConsecutive && now>=_holdUntil` → `ended=Close(now)`. 7) `private bool Close(DateTimeOffset now)` → `_firing=false; _lastEventEndedAt=now; _consecAbove=0; _consecBelow=0; return true`. Brak stanu Cooling. 8) `State`: `now<_stormUntil → "storm"` (ma pierwszenstwo nad wszystkim), inaczej `!Calibrated→"calibrating"`, `_firing→"firing"`, else `"clear"`.
- [create] `orchestrator/Argus.Orchestrator/Detection/AlertStateStore.cs` — `ConcurrentDictionary<string,(AlertParams,AlertPolicy)>(StringComparer.OrdinalIgnoreCase)` (jak `ScoreStreamPipeline.cs:443`). `GetOrCreate(entityId,p)`: rownosc rekordu → ta sama polityka; zmiana → nowa (`LastPublishedFlag==null`, wiec nastepny werdykt raz republikuje biezaca wartosc — akceptowane i udokumentowane). `PruneTo(keys)`. Istnieje bo `HaListenerWorker.cs:91-96` przebudowuje kazdy EntityRuntimeState na kazdym Save; bez niego jeden Save kosztuje zamrazarke ~26 h rekalibracji (240 probek / 225 na dobe). Wylacznie w pamieci — D-11 (`EntityRuntimeState.cs:17-22`) utrzymane.
- [modify] `orchestrator/Argus.Orchestrator/Detection/EntityRuntimeState.cs` — ctor `:69` → `(HstParams hstParams, AlertParams? alertParams=null, AlertPolicy? alert=null)`; ciala `:71-81` bez zmian (HysteresisGate zostaje jako sciezka `alert_mode: legacy`). Dodac `AlertParams`, `Alert`. USUNAC `LastPublishedFlag` `:57` (pisane `:231/:271`, nieczytane). Dodac `double LastValue`, `bool FrozenNow` obok `:64`. Parametry opcjonalne utrzymuja **37** istniejacych `new EntityRuntimeState(` przy kompilacji.
- [modify] `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs` — (1) oba ctory `:54-74`/`:79-92`: koncowy `AlertStateStore? alertStore=null` (**32** miejsc `new ScoreStreamPipeline(`). (2) po `:173`: `entityState.LastValue=reading.Value; entityState.Alert.ObserveValue(reading.Value); entityState.FrozenNow=entityState.FrozenDetector.IsFrozen;`, a `:175` czyta `FrozenNow`. Kanal surowy liczony w write-loop → syntetyczny `HaReading(...,0.0,...)` `:161` niezmieniony, kolejnosc CompleteAsync `:191-193` nietknieta. (3) `ProcessVerdictAsync` `:202-245`: `:208-213` verbatim; **rozgalezienie** `if (entityState.AlertParams.Mode=="legacy") { var isAnom=entityState.Hysteresis.Apply(score); ...dotychczasowa sciezka publikacji... } else { var decision=entityState.Alert.OnVerdict(score, entityState.WarmedUp, reading.SuppressBinarySensor, entityState.FrozenNow, DateTimeOffset.UtcNow); ... }`; `PublishScoreAsync` bezwarunkowo (`:26`, `:255-263`); flaga change-only: `if(!reading.SuppressBinarySensor && entityState.Alert.LastPublishedFlag!=decision.FlagOn){ await _publisher.PublishFlagAsync(...); entityState.Alert.LastPublishedFlag=decision.FlagOn; }`; `if(decision.EventStarted){ _recentAnomalies?.Record(...); _logger.LogInformation(LogEvents.AlertEventStarted, "...", entityId, decision.Rank, decision.RawZ, decision.Channel); }`; `if(decision.EventEnded) _logger.LogInformation(LogEvents.AlertEventEnded, ...)`; `if(decision.Storm) _logger.LogWarning(LogEvents.AlertStormRaised, ...)`. Rozgrzewka/kalibracja tlumia WARTOSC flagi (jeden jawny OFF czysci retained ON), reconnect nadal tlumi PUBLIKACJE (`:130` zielone). (4) log Debug `:242-244` + `rank={Rank:F4} z={Z:F2} state={AlertState} published={Published} latency_ms={LatencyMs:F1}`. (5) `PublishFrozenAsync` `:264-275`: **NIE usuwac** `:270` — `PublishFlagAsync(entityId, on:true)` zostaje, ale przez `entityState.Alert.LastPublishedFlag` (change-only), bo `:255-262` deklaruje ta sciezke jako jedyna gwarantowana publikacje flagi dla zamrozonej encji; `:271` (zapis do usunietego pola) usunac. Wyjscie z ON daje `FrozenNow=false` → `OnVerdict`. (6) `BuildEntityStates` `:439-454`: `var alertParams = AlertParams.From(hstDetector?.Params ?? new());`, `new EntityRuntimeState(hstParams, alertParams, _alertStore.GetOrCreate(entity.EntityId, alertParams))`; po petli `_alertStore.PruneTo(states.Keys);`. (7) `PrimeFromHistoryAsync` `:391-401`: **przed** `foreach` `bool seedRaw = entityState.Alert.RawSampleCount==0;`, w petli `if(seedRaw) entityState.Alert.SeedValue(row.Value);` (guard w petli zasialby dokladnie 1 punkt); wewnatrz istniejacego try/catch `:418-424`.
- [create] `orchestrator/Argus.Orchestrator/Mqtt/IMqttPublishSink.cs` — `internal interface IMqttPublishSink { Task PublishAsync(string topic,string payload,bool retain,CancellationToken ct); }`; `MqttConnection` (`:22` sealed, `:97` non-virtual) implementuje. Bez tego szwu retain jest nieobserwowalny.
- [modify] `orchestrator/Argus.Orchestrator/Mqtt/StatePublisher.cs` — `:15` pole `IMqttPublishSink?` + internal `SetConnection(IMqttPublishSink)` obok `:27`; `:62` `retain: false` → `retain: true` (change-only na nieretained zostawia HA w `unknown`; stale ON pokrywa LWT `MqttConnection.cs:172` + availability `DiscoveryPublisher.cs:53-57`); `:61` `LogInformation` → `LogDebug` (zrodlo spamu F8). `PublishScoreAsync` `:66-71` bez zmian.
- [modify] `orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs` — `:8` → `(..., bool Calibrated=false, int CalibrationCount=0, int CalibrationTarget=0, string AlertState="")`.
- [modify] `orchestrator/Argus.Orchestrator/Program.cs` — przed `:156` `builder.Services.AddSingleton<AlertStateStore>();`; `sp.GetRequiredService<AlertStateStore>()` jako ostatni argument fabryki `:156-165`; projekcja `/api/sensors` `:313-319` + `calibrated/calibrationCount/calibrationTarget/alertState` (addytywne JSON, `types.ts:3-17` ignoruje).
- [modify] `orchestrator/Argus.Orchestrator/Config/InputValidator.cs` — `ValidateAlertKeys` wywolane w `ValidateHst` (`:95`) **po `:129`** (koniec frozen_variance). Klucze walidowane TYLKO gdy obecne (SPA ich nie wysyla — `sensors.ts:14-22`; wymog dalby `{ok:false,kind:'validation'}` na kazdy Save). `rank_window>=50`, `raw_window>=10`, `alert_min_samples>=50`, `min_duration_sec/refractory_sec/storm_hold_sec>=0`, `max_events_per_hour>=1`, `max_event_duration_sec>=60`, `q_fire∈(0,1)`, `q_clear∈[0,1)`, `z_fire>0`, `z_clear>=0`, `alert_mode∈{adaptive,legacy}`, `evidence_mode∈{any,both,score_only,raw_only}`; krzyzowe (wzor `:119-126`): `q_fire>q_clear`, `z_fire>z_clear`, `alert_min_samples<=rank_window`. Helpery: `TryGetDouble` `:163`, `TryGetInt` `:173`, `ValidateIntAtLeast` `:184`. `high_threshold/low_threshold` `:107-126` nietkniete (zywe w legacy).
- [modify] `orchestrator/Argus.Orchestrator/Batch/BatchSchedulerWorker.cs` — usunac `:451` `PublishFlagAsync(entityId, last.IsAnomaly, ct)`, zostawic `:450`, log `:452-456` bez pola anomaly. Drugi, niebramkowany pisarz do `argus/{slug}/flag/state`, uspiony tylko przez `Program.cs:175`.
- [modify] `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — po `:89`: `AlertEventStarted=7010`, `AlertEventEnded=7011`, `AlertStormRaised=7012` (nastepne wolne po 7009).
- [modify] `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs` — BEZ ZMIAN, decyzja: klucze alert nie ida do tabeli `:25-34`, bo WR-02 (`:10-13`) wiaze ja z `sensors.ts:14-22` wylacznie komentarzem. UI = WS5.
- [modify] `argus/config.yaml:3` — bump wersji; **release**: `./deploy/build-push.ps1 -Version X.Y.Z`, commit `argus/config.yaml`, Update w HA. Bez tego kryteria polowe A1-A3/A7-A10/A13-A14 sa niemierzalne (push do GitHub ≠ update dodatku).

**Testy**
- `AlertPolicyTests.cs :: RankGate_ScoreStreamNeverBelow048_StillReleasesWithinOneWindow` — F2: zwolnienie nie moze byc stalą (min 0.480 na load_5m vs low_threshold 0.3).
- `... :: RankGate_MemoryBand_100PercentAbove07_ProducesZeroEvents` — 5653 score z pasma min 0.830 + `MemoryDriftBand()`; 0 EventStarted. F3/F6.
- `... :: SustainedExcursion_SelfClears_OnTimeUnder25Percent` — F1: 100/100/99/91% on-time strukturalnie niemozliwe.
- `... :: RawZ_ZamrazarkaLevelHistogram_NeverExceedsFireThreshold` — `ZamrazarkaLevels()` = histogram F4 (101×10,103×41,105×148,107×230,109×113, `new Random(42)`); max |z|<=2.1, 0 zdarzen we WSZYSTKICH evidence_mode. F3/F4.
- `... :: RawZ_LodowkaCompressorCycle_FiresExactlyTwice` — 1546 probek 0 W z dwoma ~90-probkowymi biegami 984 W przy plaskim score; dokladnie 2 zdarzenia, on-time 2-8%. F3 (83% precyzji musi przetrwac); pada, jesli ktos zredukuje projekt do rangi-na-score.
- `... :: Uncalibrated_BelowAlertMinSamples_NeverFires` — prog `_rank.Count>=50`.
- `... :: FirstEventEverInsideRefractoryOfCalibration_DoesNotTripWatchdog` — `_eventStartedAt==MinValue` musi ustawic `now` niezaleznie od refractory; inaczej pierwszy alarm po kazdym starcie = falszywy storm + 1 h slepoty.
- `... :: MaxEventDuration_EvidenceHeldTrueForever_ForceClosesAndRaisesStorm` — F1 watchdog + Rule 12; `State=="storm"` przez StormHoldSec.
- `... :: RateCap_FiveOnsetsInOneHour_RaisesStormAndCapsAtFour`; `... :: Refractory_ReFireInsideWindow_ReRaisesFlagButCountsNoNewEvent`; `... :: MinDuration_SingleTickSpike_HoldsFlagForMinDurationSec`.
- `... :: ReconnectSuppression_BlocksOnTransition_ButAllowsOffTransition` — asymetria D-07.
- `... :: FrozenEvidence_BypassesCalibration_ButStillClearsWhenUnfrozen` — dzis frozen wymusza ON (`ScoreStreamPipeline.cs:269`) i tylko 3 score <0.3 to gasi, co F2 wyklucza.
- `... :: ScaleLadder_TwelveIdenticalThenOutlier_PicksFiniteNonDegenerateScale` — StdDev tylko po `_count`, Q1/Q3 z pierwszego sortu.
- `... :: EvidenceModeBoth_ScoreHighAlone_DoesNotFire`.
- `AlertParamsTests.cs :: From_EmptyDictionary_YieldsF13ValidatedDefaults` (720/0.99/0.80/720/5.0/3.0/3/240/120/600/4/21600/3600/adaptive/any); `:: From_ReusesExistingMinConsecutiveKey_AndParsesInvariantCulture` (`q_fire:"0.995"` przy przecinkowej CurrentCulture, `EntitiesConfig.cs:86-88`).
- `AlertStateStoreTests.cs :: GetOrCreate_SameParamsTwice_ReturnsSamePolicyWithAccumulatedSamples`, `:: GetOrCreate_ChangedParams_ReturnsFreshPolicy`, `:: PruneTo_RemovesUntrackedEntities`.
- [create] `StatePublisherTests.cs :: PublishFlagAsync_UsesRetainTrue_AndScoreUsesRetainFalse` — przez `IMqttPublishSink` (plik nie istnieje dzis).
- `ScoreStreamPipelineTests.cs :: OnVerdict_UnchangedFlagValueAcrossManyVerdicts_PublishesFlagExactlyOnce` (F8) i `:: OnVerdict_FlagTransitionOffOnOff_PublishesAllThreeValuesInOrder` (`FlagHistory==[false,true,false]`).
- `ScoreStreamPipelineTests.cs :: OnVerdict_LegacyMode_UsesHysteresisGateAndPublishesEveryTick` — pinuje `alert_mode: legacy` (A13); bez niego galaz nie istnieje.
- `ScoreStreamPipelineTests.cs :: RunAsync_WriteLoop_FeedsRawChannelWithRealReadingValues` — 100×100 W potem 984 W; `LastRawZ != 0`. Blokuje przeniesienie ObserveValue do read-loop (z liczone na stalym 0.0 z `:161`).
- `ScoreStreamPipelineTests.cs :: RecentAnomalies_OneHundredVerdictsInOneEvent_RecordsExactlyOneEntry` (`RecentAnomaliesCache.cs:35`).
- PRZEPISAC ciala (nie parametry): `OnVerdict_NotSuppressed_PublishesFlag` `:87` i `OnVerdict_PublishedAndAnomalous_RecordsRecentAnomaly` `:193` — steruja `ProcessVerdictAsync` bezposrednio, wiec `_raw.Count==0` i staly score maja range 0.5 na zawsze; nowy driver: `rank_window=200, alert_min_samples=50, min_duration_sec=0, min_consecutive=1`, >=50 scisle rosnacych score, ostatni unikalne maksimum → `Assert.True(publisher.LastFlagValue)`.
- ZMIENIA SIE (zdjac z listy "byte-unchanged"): `PublishedFlag_AlwaysAccompaniedByScore_AcrossFrozenAndVerdictPaths` `:297-343` — `Assert.Contains("sensor.frozen", publisher.FlaggedEntities)` `:339` przezywa tylko dzieki utrzymaniu publikacji flagi w `PublishFrozenAsync`; test rozszerzyc o drugie wywolanie potwierdzajace brak republikacji.
- `OnVerdict_NotWarmedUp_DoesNotPublishFlag` `:168` → `OnVerdict_NotWarmedUp_PublishesOffExactlyOnce_NeverOn` (odwrocona asercja flagi, asercja score `:186` bez zmian).
- Mirror DI `:435-450` musi dostac `services.AddSingleton<AlertStateStore>()` + dodatkowy argument, inaczej przestaje lustrzyc `Program.cs:156-165`.
- `FakeStatePublisher` `:938`: `FlagPublishCount`, `List<bool> FlagHistory`.
- BEZ ZMIAN (jesli wymagaja edycji — projekt zlamal przypiety niezmiennik): `:130`, `:445`, `:477`, `:501`, `:292`, dziewiec testow backfillu `:666-899` (+ asercja `RawSampleCount==history.Count` w `:592`/`:646`), `HysteresisGateTests.cs` (118 linii, 10 testow).
- `InputValidatorTests.cs :: Validate_HstParamsWithNoAlertKeys_ReturnsNoErrors`, `:: Validate_AlertKeysPresentButInverted_ReturnsErrors`.
- `BatchSchedulerWorkerTests.cs :: RunEntityBatch_PublishesScore_ButNeverFlag`.

**Kryteria akceptacji**
- A1 (F1): w dowolnym oknie 24 h zadna flaga Argus nie jest ciagle ON dluzej niz `max_event_duration_sec` (6 h), odczyt z historii HA. Dzis 5/5 ON >24 h.
- A2 (F1, **predykcja, nie pomiar** — F13 mierzyl obie bramki OSOBNO, kompozycja OR-fire/AND-clear nie byla mierzona): on-time spada z 100/100/99/91/25% do <10% dla kazdego z pieciu, przy `memory_use_percent` i `zamrazarkapiwnica_power` = 0 epizodow. Przed wydaniem: offline replay nagranych serii 24 h pod faktyczna kompozycja `any`.
- A3 (F1): kazda z pieciu flag ALBO ma >=1 prawdziwe przejscie ON→OFF (para `on -> off` niebedaca sygnatura restartu `on -> unavailable -> unknown -> on`), ALBO 0 epizodow ON w 24 h. Startowy OFF nie liczy sie tu — jest osobno w A9.
- A4 (F2): `grep` po `AlertPolicy.cs`/`RollingRank.cs`/`RollingRobustZ.cs` nie znajduje zadnego literalu numerycznego porownywanego z `score`.
- A5 (F3/F4): `ZamrazarkaLevels()` → 0 zdarzen, max |z| <= 2.1. **Prog F3: 0 alarmow na zamrazarce w 24 h w polu.**
- A6 (F3): `LodowkaCompressorCycle()` → dokladnie 2 zdarzenia, 2-8% on-time. **Prog F3 w polu: precyzja epizodow `lodowkababcia_power` >= 70% (epizod = prawdziwy, gdy pokrywa przejscie 0 W ↔ 984 W).** Dla pozostalych czterech precyzja per-probka NIE jest kryterium odbioru — F3 definiuje prawde przez robust-z, a kanal surowy liczy te sama statystyke (cyrkularnosc); kryterium jest tam liczba epizodow (A2).
- A7 (F3): 5..15 startow zdarzen lacznie/24 h, liczone z `GET /api/anomalies/recent` i z id 7010 w logu.
- A8 (F8): przy stabilnej wartosci flagi 0 linii `Flag <entity> -> ...` w 15 min na Debug (dzis ~4/15 s/encja na Information). Unit: `FlagPublishCount==1` na 100 werdyktach.
- A9 (F1): w ciagu jednego interwalu werdyktu **po** 60-s cooldownie D-07 (`ReconnectCooldown.cs:11`), tj. <=75 s od restartu, kazda encja czyta OFF w HA.
- A10 (D-07): brak publikacji flagi i brak przejscia Clear→Firing dla zywych odczytow `state_changed` w oknie 60 s; zamkniecie trwajacego zdarzenia dozwolone. Zastrzezenie: burst `get_states` idzie PRZED `MarkReconnect` (`NetDaemonHaEventSource.cs:158-159`), wiec nie jest objety cooldownem — defekt zostaje otwarty.
- A11: payload `argus/{slug}/score/state` bajt-identyczny (`verdict.Score ?? 0.0`, `ToString("G", InvariantCulture)`), tematy i retained discovery (`DiscoveryPublisher.BuildBinarySensorConfig/BuildSensorConfig`) bez zmian → zero churnu encji HA.
- A12: `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` = 0 failed, 0 skipped (baseline 463). **Decyzja, nie ryzyko:** `.github/workflows/build.yml` odpala sie tylko na tagach `v*`, wiec to bramka reczna — uruchamiac przed kazdym `build-push.ps1`.
- A13: `alert_mode: legacy` w params hst w `/data/entities.yaml` przywraca stara bramke dla encji w jednym przeladowaniu (~300 ms debounce `ConfigFileWatcherService.cs:61-64`), bez redeployu.
- A14 (Rule 12): przekroczenie `max_events_per_hour` lub watchdoga → WARN 7012 + `alertState == "storm"` w `/api/sensors`. Zaden alarm nie znika bez jednego z tych sygnalow.
- A15 (**latencja werdyktu**, steady-state — NIE czas odpowiedzi symulatora, ten jest osobno w B8/§5.6): pole `latency_ms` z loga Debug per werdykt (`ScoreStreamPipeline.cs:242-244`, stempel wejsciowy `:208`), okno = 60 min ciagłej pracy po zakonczeniu kalibracji, liczone dla KAZDEJ encji strumieniowej z osobna: `grep '<entity_id>' <log 60 min> | grep -o 'latency_ms=[0-9.]*' | cut -d= -f2 | sort -g | awk '{a[NR]=$1} END{printf "n=%d p95=%.1f max=%.1f\n", NR, a[int(NR*0.95)+0], a[NR]}'` → **p95 < 1000 ms i max < 3000 ms**. Te same progi obowiazuja w §5.6 jako A15-pod-obciazeniem symulatora.

**Ryzyka**
- Kanal robust-z na surowej wartosci to logika detekcji w orkiestratorze, w napieciu z D2. Obrona: regula deterministyczna bez modelu/fitu/persystencji, kategoria `FrozenSensorDetector`. Jesli WS3 dostarczy score o ksztalcie odchylenia — ten kanal wycofac (`evidence_mode: score_only`), nie duplikowac.
- Cyrkularnosc walidacji: F3 definiuje prawde jako robust-z > 3.5, kanal surowy liczy z > 5. Przed uznaniem A6 sprawdzic dwa epizody lodowki wzgledem realnego cyklu sprezarki.
- Degeneracja MAD na seriach bimodalnych/skwantowanych: mediana 0 i MAD 0 przy IQR takze 0 → abstynencja i caly ciezar na kanale rangi. F13 mierzy, ze na lodowce przy oknie 720 to nie zachodzi; awaria bylaby cicha.
- Kalibracja po restarcie procesu: 240 probek = ~61 min (memory), ~68 min (load_5m), ~4.1 h (processor_use), ~26 h (obie lodowki). Znosi to dopiero WS4 (i tylko dla kanalu surowego — kanal rangi potrzebowalby historycznych SCORE, czego Warmup nie zwraca).
- Rangi na skwantowanym rozkladzie maja remisy — na `zamrazarkapiwnica_power` (~5 poziomow, F4) kanal score bywa martwy; projekt opiera sie na kanale surowym mocniej, niz sugeruje dwukanalowa narracja.
- `retain:true` — ostatnie ON przezywa crash dodatku. Lagodzone przez LWT `MqttConnection.cs:172` + availability `DiscoveryPublisher.cs:53-57` (encja `unavailable`, nie stale ON) — zweryfikowac na zywej instancji, nie zakladac.
- Storm/watchdog obcinaja prawdziwa kaskade: >4 startow/h capowane, trwala awaria cykluje 6 h ON / 1 h wyciszenia. Fail-loud (7012 + `alertState`), ale niedoraportowanie wystepuje wlasnie wtedy, gdy alarm ma znaczenie.
- Dostep miedzywatkowy do `AlertPolicy` (write-loop vs read-loop) — jeden lock jest jedynym nowym punktem synchronizacji na goracej sciezce; nigdy trzymany przez await.
- Klucze alert siedza w mapie params detektora `hst` — dla encji na `mad`/`stl` zostana cicho zignorowane (dzis wszystkie sledzone encje sa hst).
- UI cofnie kazdy niedomyslny param alert: brak read-backu (`sensors.ts:54-61`), `save()` `:161-169` to full-list replace. Rollback do `legacy` mozliwy wylacznie edycja `/data/entities.yaml`. Naprawa = WS5, brak zaleznosci twardej.
- **Otwarte po tej korekcie:** `PublishFrozenAsync` publikuje flage change-only, wiec zamrozona encja, ktorej detektor przestal emitowac werdykty, nadal nie przejdzie przez `OnVerdict` — min-duration/refractory/cap nie obejmuja jej wyjscia; wyjscie z ON wymaga co najmniej jednego werdyktu. Niezmiennik `:255-262` utrzymany kosztem tej luki.
- NIENAPRAWIONE tutaj: F4, F5, F7, F9, F10, F11, F12 oraz kolejnosc `NetDaemonHaEventSource.cs:158-159`.

**Poza zakresem**
- Cokolwiek w `detector/` — `hst_detector.py`, `registry.py`, `servicer.py`, `model_store.py` nietkniete; brak nowego pola `EntityDetector`, wiec sciezka bare-pickle (`model_store.py:264`, guard `:305`) nie jest cwiczona i wszystkie encje zachowuja `n_seen` (16061/17824/11380/707/135).
- `proto/argus.proto` — zero pol, zero RPC, zero regeneracji stubow (omija luke: `argus/Dockerfile` nie uruchamia `detector/scripts/gen_proto.py`, a `*_pb2*.py` sa w gitignore).
- Zmiana semantyki score (ECDF / z-squash) — nalezy do WS3; tu `argus/{slug}/score/state` niesie surowy score bez zmian, `retain:false`, co tick.
- Nowa encja diagnostyczna MQTT dla rank/z/alert_state (wymagalaby nowego retained discovery); te same dane sa w `/api/sensors` i logu Debug.
- Usuwanie `HysteresisGate.cs` / `HysteresisGateTests.cs` — zostaja jako sciezka `legacy` z pinujacym testem.
- Persystencja stanu bramki na dysk (D-11), naprawa syntetycznego `HaReading` `:161`, kolejka parowania read/write.
- `orchestrator/ui/**`, blok `alert:` w entities.yaml, nowe pole SaveRequest, zmiany w root dict `Program.cs:463-468` (musi dalej zachowywac `groups` i `_patterns` — potwierdzona przyczyna G-14-1), nowa opcja w `argus/config.yaml` (poza bumpem wersji).
- Auto-degradacja `min_consecutive` dla wolnych encji i seedowanie kanalu rangi z historii (wymagaloby, by Warmup zwracal score per punkt).

### WS3 — Domyslne, presety i migracja konfiguracji

**Cel** — Zabic F6 na warstwie konfiguracji: jedna domyslna tabela `rmad` (score bezwymiarowy `z/(z+z_scale)`) poprawna na kazdym czujniku, plus jednorazowa, idempotentna, glosna migracja `/data/entities.yaml` do `schema_version: 2`, ktora NIE tworzy duplikatow encji w HA.

**Zmiany**

- [modify] `orchestrator/Argus.Orchestrator/Mqtt/UniqueId.cs:12-18` — **ROZSTRZYGNIECIE D1: odcinamy nazwe detektora od tozsamosci.** `AnomalyId => $"argus_{Slug(entityId)}_anomaly"`, `ScoreId => $"argus_{Slug(entityId)}_score"`. Sygnatury tracą parametr `detector`. Uzasadnienie: temat stanu `argus/{slug}/flag/state` (DiscoveryPublisher.cs:49) i availability (:52-56) sa juz bezdetektorowe, wiec zostawienie detektora w `unique_id`+`object_id` (DiscoveryPublisher.cs:44-46, D-14) daje przy `hst`->`rmad` DWIE encje HA karmione jednym tematem. Zmiana entity_id nastepuje RAZ i nigdy wiecej — kazda przyszla zmiana detektora jest juz bezkosztowa.
- [modify] `orchestrator/Argus.Orchestrator/Mqtt/DiscoveryPublisher.cs` — `GetDetectorName` (:224) usuwany z sciezki id (zostaje tylko w `device`/atrybutach jesli uzywany); nowa metoda `RetractLegacyDetectorScopedAsync(IReadOnlyList<EntityConfig> preMigration, CancellationToken)` publikujaca PUSTA ładowność retained na `homeassistant/binary_sensor/argus_{slug}_{det}_anomaly/config` i `homeassistant/sensor/argus_{slug}_{det}_score/config` dla KAZDEJ pary (slug, detektor) z konfiguracji sprzed migracji — `RetractAsync` (:169-187) tego nie robi, bo dziala tylko dla `removedEntities`, a encja migrowana nadal jest sledzona. Wywolanie: raz, przy pierwszym starcie po migracji, przed pierwszym `PublishAsync`.
- [create] `orchestrator/Argus.Orchestrator/Config/EntitiesSchemaMigrator.cs` — `public const int TargetSchemaVersion = 2; public static bool MigrateIfNeeded(string path, ILogger logger)`. Kroki: (1) parse root `Dictionary<string,object>` konfiguracja deserializera jak `EntitiesConfigLoader.cs:24-27`; `schema_version >= 2` -> `return false` (Debug, bez zapisu). (1a) **Bramka sekwencji**: jesli `ScoreStreamPipeline` nadal rozwiazuje detektor literalem `"hst"` (`ScoreStreamPipeline.cs:446`) — sprawdzane przez `RmadParams`-owy punkt wejscia `BuildEntityStates` udostepniony jako `internal static bool SupportsRmad` — migrator loguje Error i NIE zapisuje. Bez tego migrowany plik trafia na `new HstParams()` (250/0.7/0.3) i cicho odtwarza F0. (2) `File.Copy(path, path + ".pre-v2.bak", overwrite:false)`. (3) `EntitiesConfigLoader.Load`. (4) encja z dokladnie jednym detektorem `hst`, ktorego `Params` sa PUSTE albo rowne odciskowi legacy `{window:"250", n_trees:"25", high_threshold:"0.7", low_threshold:"0.3", min_consecutive:"3", frozen_window:"10", frozen_variance_threshold:"0.001"}` -> `{name:"rmad", params: DetectorDefaults.Get("rmad")}`, `min_consecutive` przenoszony verbatim. Log Information `Migrated {EntityId}: hst -> rmad (schema_version 2)`. (4a) **ROZSTRZYGNIECIE D3 (frozen, reguła D-H)**: migracja przenosi `frozen_window` VERBATIM (przy pustych `params` — `10` z `DetectorDefaults`) i zapisuje TYLKO `frozen_variance_threshold: "0.0"` = frozen wylaczony arytmetycznie (`FrozenSensorDetector.cs:46` liczy `variance < 0.0`, a `ComputeVariance` `:50-61` nigdy nie jest ujemna). `frozen_window: "0"` jest ZAKAZANE — `FrozenSensorDetector.cs:29-31` robi wtedy `Dequeue()` na pustej kolejce, wolane bezwarunkowo z `ScoreStreamPipeline.cs:172` (`InvalidOperationException` na pierwszym odczycie), a `InputValidator.cs:101` wymaga `frozen_window >= 1`, wiec encja zostawiona na `hst` przestalaby sie zapisywac z SPA. Dotyczy OBU galezi (migrowanych na `rmad` i POZOSTAWIONYCH na `hst`); log Information `Frozen disabled for {EntityId}`. Inaczej piec sledzonych encji zachowaloby frozen 10/0.001, a `lodowkababcia_power` (88% zer, SCALE-1) siedzialaby ON przez caly postoj sprezarki — wprost przeciw D-J. (5) inny detektor (w tym `rmad`) -> cichy skip (Debug), NIGDY ostrzezenie "tuned hst"; `hst` z innymi paramami -> Warning `Entity {EntityId} has tuned hst params — left on hst`. (6) root w kolejnosci `{schema_version, _patterns, entities, groups}`, `groups` i `_patterns` przenoszone verbatim (G-14-1). (7) zapis przez `ConfigWriter.WriteAsync` (ConfigWriter.cs:16-26). (8) kazdy wyjatek: Error z nazwa `schema_version` + rethrow.
- [modify] `orchestrator/Argus.Orchestrator/Config/EntitiesConfig.cs` — po `HstParams` (konczy sie :89, zostaje BYTE-IDENTICAL) dopisz `RmadParams`: `Window=720`, `MinSamples=60`, `ZScale=5.0`, `ScaleFloor=0.0`, `HighThreshold=0.5`, `LowThreshold=0.375`, `MinConsecutive=3`, `FrozenWindow=10`, `FrozenVarianceThreshold=0.0` (wylaczenie idzie wariancja, NIE oknem — D-H; `0` w oknie crashuje `FrozenSensorDetector.cs:29-31`); `static RmadParams From(Dictionary<string,string>)` z tymi samymi literalami i kopia prywatnych `GetInt`/`GetDouble` (:83-88, InvariantCulture nosna). Mapowanie `z=z_scale*t/(1-t)`: 0.5->5.000, 0.375->3.000 = zmierzony w F13 wariant „fire z>5, release z<3, 3 consecutive".
- [modify] `orchestrator/Argus.Orchestrator/Config/InputValidator.cs` — `:26` `KnownDetectors` += `"rmad"`; komunikat `:68-69` -> `Choose RMAD, HST, MAD, or STL.`; `:73-84` `case "rmad": ValidateRmad(...)`; nowa `ValidateRmad` miedzy :130 a :132: `window` 30..10000 (`MSG_WINDOW_RANGE = "Must be a whole number between 30 and 10000."`), `min_samples >= 10` (`MSG_MIN_SAMPLES = "Must be a whole number ≥ 10."`), `z_scale > 0` (istniejacy komunikat z :136), `scale_floor >= 0` (istniejacy z :129), `min_consecutive >= 1`, `frozen_window >= 1` (BEZ zmiany vs hst `InputValidator.cs:101` — wylaczenie idzie przez `frozen_variance_threshold`, D-H), `frozen_variance_threshold >= 0`, high/low skopiowane VERBATIM z :107-125, cross-field `min_samples > window` -> `MSG_MIN_SAMPLES_LE_WINDOW = "Must not be greater than window."`; nowy helper `ValidateIntInRange` obok `ValidateIntAtLeast` (:184). Brak klucza = blad (:191-192) — bez defaultowania na granicy walidacji.
- [modify] `orchestrator/Argus.Orchestrator/Web/DetectorDefaults.cs` — arm `"rmad"` jako PIERWSZY w switchu :23-48 z wartosciami rownymi literalom `RmadParams.From` (`frozen_window "10"`, `frozen_variance_threshold "0.0"`); `public static Dictionary<string,Dictionary<string,string>> All()`; doc :10-13 przepisany: WR-02 wycofane, SPA pobiera `GET /api/detectors/defaults`.
- [create] `orchestrator/Argus.Orchestrator/Web/SensorPresets.cs` — `DetectorPreset` z `Web/DetectorCatalog.cs:9` (bez drugiego typu). `rmad`: Low `high 0.615`/`low 0.444` (z 7.99/3.99), Med `0.5`/`0.375` (z 5.00/3.00, domyslny label wg `SensitivityPresetPicker.tsx:9`), High `0.444`/`0.286` (z 3.99/2.00). Presety ruszaja WYLACZNIE dwa klucze progowe. Brak tabeli klas czujnikow — kadencja mierzona (15.3 s memory_use_percent .. 391 s lodowkababcia_power), nie zgadywana z jednostki.
- [modify] `orchestrator/Argus.Orchestrator/Program.cs` — (1) `:22` przed `EntitiesConfigLoader.Load`: `EntitiesSchemaMigrator.MigrateIfNeeded(entitiesPath, entitiesLogger)`. (2) `:329-339` bez `name` zwraca `{defaults = DetectorDefaults.All(), presets = new { rmad = SensorPresets.Get("rmad") }}`; wariant `?name=` byte-identical. (3) `:295-322` projekcja `/api/sensors` += `detectors` (z `liveCfg`, slownik OrdinalIgnoreCase budowany RAZ poza `Select`), `calibratedExpected/Lower/Upper`, `medianIntervalSec`. (4) `:430-432` domyslny detektor `"hst"` -> `"rmad"`, `Params = []`. (5) `:463-468` ORAZ **`:604-610` (`POST /api/groups/save` — drugi pisarz)** dostaja `["schema_version"] = EntitiesSchemaMigrator.TargetSchemaVersion` jako pierwszy klucz; bez tego zapis grup zdejmuje stempel i migrator przepisuje plik przy kazdym boocie. (6) `:495-498` `hasHst` -> `hasStreaming` = `d.Name in {hst, rmad}`.
- [modify] `orchestrator/Argus.Orchestrator/Detection/EntityStatusCache.cs:8-12` — `EntityStatusEntry(..., double? CalibratedExpected = null, double? CalibratedLower = null, double? CalibratedUpper = null, double? MedianIntervalSec = null)` — pola z `Verdict.expected/lower/upper` (`proto/argus.proto:17-20`, juz na drucie, zero zmian proto). Null przed pierwszym werdyktem; UI degraduje sie lagodnie.
- [modify] `argus/rootfs/usr/local/bin/gen-entities.py:36,39-50` — obie galezie emituja `yaml.dump({"schema_version": 2, "entities": [...]})`, detektor `{"name":"rmad","params":{}}`; galaz pustej listy tez stempluje wersje (inaczej migrator pisze przy kazdym boocie). Docstring :8 nazywa rmad. `yaml.dump` obowiazkowy (T-1-05).
- [modify] `entities.yaml` (fixture dev) — `schema_version: 2`, trzy encje na `rmad`, blok :11-15 = zestaw Med, `params: {}` na :21/:27 zachowane.
- [modify] `orchestrator/ui/src/api/types.ts` — `export type DetectorName = 'rmad' | 'hst' | 'mad' | 'stl';`; `:24` i `:47` odwoluja sie do niego; `SensorEntry` += `detectors?`, `calibratedExpected/Lower/Upper?`, `medianIntervalSec?`; `:42` `hasStreaming`; `DetectorDefaultsResponse`. Unia jest re-deklarowana niezaleznie w `sensors.ts:34`, `sensors.ts:117`, `detectorParams.ts:126`, `DetectorEntry.tsx:8,18,51`, `DetectorDisclosure.tsx:9`, `SensorList.tsx:15`, `SensorListRow.tsx:14` — WSZYSTKIE przepiac na `DetectorName` (kontrawariancja `strictFunctionTypes` inaczej wysypie `SensorListRow`).
- [create] `orchestrator/ui/src/state/detectorDefaults.ts` — signals `detectorDefaults`, `detectorPresets`, computed `defaultsLoaded`, `loadDetectorDefaults()` przez `apiGet('api/detectors/defaults')` (sciezka wzgledna, Ingress).
- [modify] `orchestrator/ui/src/state/sensors.ts` — USUN `DETECTOR_DEFAULTS` (:13-32) i komentarz WR-02 (:6-12); `defaultsFor(name)`; `makeDetectorEntry` (:34-36) z serwera; `getOrInitEdit` (:54-61) hydratuje z `entry.detectors` (fallback `[makeDetectorEntry('rmad')]` tylko gdy `isTracked` i brak) — to naprawa cichej rewersji; `setTracked` (:91)/`addDetector` (:101) na `rmad`, `addDetector` zablokowany gdy `!defaultsLoaded.value`; `loadSensors` (:67) `Promise.all` z `loadDetectorDefaults()`, guard `loadSensorsSeq` bez zmian.
- [modify] `orchestrator/ui/src/validation/detectorParams.ts` — trzy nowe stale verbatim z C#; `validateField(key, raw, detector?)` z zakresem `window` tylko dla `'rmad'`; `validateRmadParams` wywoluje **`validateField(key, raw, 'rmad')`** (NIE 2-argumentowa forma z `:88`); `z_scale<=0` -> `MSG_GT_ZERO`, `scale_floor<0` -> `MSG_FROZEN_VARIANCE`; cross-field high>low (kopia :95-102) + `min_samples<=window`; `validateDetectorParams` (:125-137) += `case 'rmad'`.
- [modify] `orchestrator/ui/src/components/DetectorParamGrid.tsx` — `FieldSpec` += `unit?`, `help?`, `warn?`; `FieldCtx = {medianIntervalSec:number|null; zScale:number; unitOfMeasurement:string|null}` jako **prop OPCJONALNY** (default `{null,5,null}`), zeby `SensorListRow.tsx:65` kompilowal sie bez zmian. `RMAD_FIELDS` z pomocami PL: `high_threshold` -> `= odchylenie 5,0σ (robust). Alarm powyzej.`, `window` -> `≈ {v*medianIntervalSec}` + warn >48 h z rekomendacja 240 probek (720x391 s = 78 h na lodowkababcia_power; 720x15.3 s = 3 h na memory_use_percent), `min_samples` -> czas do pierwszego alarmu, `scale_floor` w jednostce czujnika. `fieldsFor` (:42-51) += `case 'rmad'` i `default: RMAD_FIELDS`. HST/MAD/STL byte-identical.
- [modify] `orchestrator/ui/src/components/DetectorEntry.tsx:8-12` — `rmad` pierwszy: `streaming (live) — odchylenie od wlasnej normy czujnika; domyslny`; `hst`: `streaming (live) — rzadkosc wartosci; wymaga recznego strojenia progow`.
- [create] `orchestrator/ui/src/components/SensorPresetPicker.tsx` — kopia strukturalna `SensitivityPresetPicker.tsx` (Pitfall 6, `SingleDetectorEditorForm.tsx:24-28`: zaden import z `state/groups`). **Montowany w `SingleDetectorEditorForm` miedzy :58 a :61**, NIE w `DetectorEntry` (ktory nie dostaje `entityId`, `DetectorEntry.tsx:14-24`, i jest renderowany takze z `SensorListRow.tsx:65`).
- [create] `orchestrator/ui/src/components/CalibratedBandReadout.tsx` — `Norma: 107 W · alarm poza 92–122 W` (mediana 107, MAD 2, sigma 1.4826*2 = 2.965, z=5 -> ±14.8). Bez pasma: `Kalibracja {readingCount}/{warmUpWindow}` lub `Prog nieustalony — czujnik nie zmienia wartosci.` Nigdy pasmo zmyslone.
- [modify] `orchestrator/ui/src/components/SaveResultBanner.tsx:16-20` — `hasHst`->`hasStreaming` i USUNIECIE copy „HST ... window=250 at ~1 reading/s ... ~4 minutes"; nowy tekst liczy z `min_samples` (60) i `medianIntervalSec`.
- [modify] `orchestrator/ui/src/components/DetectorListRow.tsx:71-78` — `warmedUp && calibratedUpper != null` -> `Dziala`; `warmedUp && calibratedUpper == null` -> `Kalibracja` (tone warn). `GroupRow` (:20-55) nietkniety.
- [create] `argus/CHANGELOG.md` + [modify] `argus/config.yaml:3` `2.1.11` -> `2.2.0`. CHANGELOG niesie: zmiane znaczenia `argus/{slug}/score/state` (zduszony robust-z, D-E — nieciaglosc historii i porownywalnosci), zmiane entity_id w HA (lista przed/po), rollback `cp /data/entities.yaml.pre-v2.bak /data/entities.yaml`, ~6.5 h ciszy na wolnych czujnikach TYLKO przy niedzialajacym backfillu (WS5 wydany wczesniej, 2.1.14), downgrade jednokierunkowy.
- [deploy] ostatni krok WS3: `./deploy/build-push.ps1 -Version 2.2.0`, commit `argus/config.yaml`, Update w HA. Sam `git push` nie aktualizuje add-onu.

**Testy**

- `EntitiesSchemaMigratorTests.PristineHstEntity_MigratesToRmadWithMedPresetThresholds` — fixture = piec encji z F0; wszystkie na `rmad`, high 0.5 / low 0.375 / window 720 / min_samples 60, `schema_version: 2`.
- `EntitiesSchemaMigratorTests.FrozenDisabledByVarianceOnBothBranches` (D3) — migrowana encja i encja zostawiona na `hst` obie koncza z `frozen_window "10"` (verbatim) / `frozen_variance_threshold "0.0"`; zero wystapien `"0.001"` i zero `frozen_window: "0"` w pliku.
- `FrozenSensorDetectorTests.WindowZero_Throws_OnFirstReading` — `new FrozenSensorDetector(0, 0.0).AddReading(1.0)` rzuca `InvalidOperationException` (`FrozenSensorDetector.cs:29-31`); przypina, dlaczego migracja nigdy nie pisze `"0"` w oknie.
- `EntitiesSchemaMigratorTests.LegacyDiscoveryTopicsAreRetractedExactlyOnce` (D1) — pusty retained payload na `homeassistant/binary_sensor/argus_sensor_load_5m_{det}_anomaly/config` i `.../sensor/argus_sensor_load_5m_{det}_score/config` dla KAZDEGO detektora z konfiguracji sprzed migracji (`hst`, `mad`, `stl` — `InputValidator.cs:26`), dokladnie raz na pare.
- `UniqueIdTests.IdsAreDetectorAgnostic` — `argus_sensor_load_5m_anomaly` niezaleznie od nazwy detektora.
- `EntitiesSchemaMigratorTests.SecondRun_IsNoOp_AndSurvivesGroupsSave` — migruj, `POST /api/groups/save`, restart -> nadal no-op, mtime bez zmian (`ConfigFileWatcherService.cs:57,61` Renamed -> Swap).
- `EntitiesSchemaMigratorTests.TunedHstEntity_IsLeftOnHstAndWarns`; `..RmadEntity_IsSilentSkip_NoTunedWarning`.
- `EntitiesSchemaMigratorTests.Migration_PreservesGroupsAndPatternsByteForByte` — G-14-1.
- `EntitiesSchemaMigratorTests.RefusesToWriteWhenPipelineStillResolvesHst` — bramka sekwencji (`ScoreStreamPipeline.cs:446`).
- `EntitiesSchemaMigratorTests.WriteFailure_Throws_AndLeavesOriginalFileIntact`; `..BackupIsWrittenBeforeFirstWrite_AndNeverOverwritten`.
- `DetectorDefaultsTests.RmadDefaults_MapToRobustZ5AndZ3` (|5.0|,|3.0| ±0.01) i `..MatchRmadParamsFromFallbacks_KeyForKey`.
- `SensorPresetsTests.RmadPresets_AreStrictlyOrdered_AndPassServerValidation`.
- `InputValidatorTests.ValidateRmad_*` — fixture startuje od `DetectorDefaults.Get("rmad")` i nadpisuje TYLKO badany klucz (brak klucza = blad, `:191-192`): `min_samples 720` vs `window 60` -> jeden `MSG_MIN_SAMPLES_LE_WINDOW`; `window 29/10001` -> `MSG_WINDOW_RANGE`, `30/10000` czysto; `ValidateRmad_LegacyHstParamSet_IsRejected`.
- `SensorsEndpointJsonTests.TrackedEntity_Projection_ReturnsSavedDetectorsAndParams` (bez tego migracja cofa sie przy pierwszym Save) i `..CalibratedBand_IsProjectedFromStatusCache` (107/92/122/384.0).
- `detectorParams.test.ts::validateRmadParams` — te same fixtury i te same stringi co C#; dodatkowo `validateField('window','29','rmad')` ORAZ `validateRmadParams` musza dac `MSG_WINDOW_RANGE`.
- `sensors.test.ts::getOrInitEdit_HydratesFromServerDetectors` (`high_threshold` NIE `'0.5'`) i `..Save_AfterPlainLoad_RoundTripsTunedParamsUnchanged`.
- `DetectorParamGrid.test.tsx::RmadHighThreshold_ShowsTheRobustZItMeans` (0.5->5, 0.615->8) i `..WindowField_ShowsWallClockSpan_AndWarnsBeyond48h` (391 s -> 78 h + warn; 15.3 s -> 3 h, brak warn).
- `SensorPresetPicker.test.tsx::SelectingHigh_WritesOnlyThresholdKeys`; `CalibratedBandReadout.test.tsx::NeverAFabricatedBand`; `DetectorListRow.test.tsx::WarmSensorWithoutBand_ShowsKalibracja`.
- `tests/test_gen_entities.py::test_emits_rmad_and_schema_version_2` — takze dla pustej listy encji.

**Kryteria akceptacji**

- F6-1: po upgrade `grep -A12 entity_id /data/entities.yaml` — piec czujnikow z BYTE-IDENTYCZNYM blokiem `rmad` (720/60/5.0/0.0/0.5/0.375/3/10/0.0), plik zaczyna sie `schema_version: 2`. Zero strojenia per-czujnik.
- F6-2: edytor kazdego z pieciu czujnikow pokazuje INNE pasmo w jednostkach czujnika z tego samego progu 0.5: `sensor.zamrazarkapiwnica_power` (101–109 W, MAD ~2 W) ~92–122 W ZAWIERA cale 24 h; `sensor.lodowkababcia_power` pasmo WYKLUCZA 984 W.
- F6-3: kazdy prog renderuje robust-z (0.5 -> `5,0σ`, 0.615 -> `8,0σ`); `window 720` czyta `~3 h` na memory_use_percent i `~78 h` na lodowkababcia_power, warn tylko na drugim.
- D1-1: `mosquitto_sub -t 'homeassistant/+/argus_+/config' -v` po migracji NIE zawiera zadnego id z `_hst_`/`_rmad_`; w HA istnieje dokladnie JEDNA para encji na czujnik (`binary_sensor.argus_sensor_load_5m_anomaly`, `sensor.argus_sensor_load_5m_score`), stare `..._hst_anomaly` znikaja z rejestru po restarcie HA. Operator: podmienic id w dashboardach/automatyzacjach wg listy w `argus/CHANGELOG.md`; historia sprzed migracji zostaje pod starym id i nie jest przenoszona (HA nie umie tego bez recznego `entity_id` rename — alternatywa dla operatora: Ustawienia -> Encje -> zmien `entity_id` starej encji na nowy PRZED pierwszym startem 2.2.0).
- D3-1: `grep -c 'frozen_variance_threshold: "0.001"' /data/entities.yaml` = 0 ORAZ `grep -c 'frozen_window: "0"' /data/entities.yaml` = 0 (okno przenoszone verbatim; `0` wywala `FrozenSensorDetector.cs:29-31`); log zawiera `Frozen disabled` dla kazdej encji.
- MIG-1: dokladnie jedna linia `Migrated {entity_id}: hst -> rmad (schema_version 2)` na encje pristine i jeden WARNING na encje zostawiona; `/data/entities.yaml.pre-v2.bak` istnieje i rowna sie plikowi sprzed.
- MIG-2: drugi restart — brak linii migracji, `stat` mtime bez zmian; to samo PO `POST /api/groups/save`.
- MIG-3: `ls /data/models/*/hst/checkpoint.pkl` nadal listuje piec plikow, mtime bez zmian po 2x300 s (`ARGUS_CHECKPOINT_INTERVAL_SEC`); przelaczenie encji z powrotem na `hst` wskrzesza `n_seen`.
- MIG-4: Save z ekranu Detektorow i Save wzorcow z Ustawien nie zmieniaja `/data/entities.yaml` (dzis oba przepisuja wszystko na hst-defaults).
- SCALE-1 (degeneracja): przy oknie o MAD=0 i >1 wartosci distinct (lodowkababcia_power, 88% zer) detektor MUSI zapalic sie na 984 W (2 epizody/24 h) — kontrakt WS1: `sigma = max(1.4826*MAD, scale_floor, 1e-9*max(1,|med|))`, a okno o JEDNEJ wartosci distinct zwraca score 0.0. Pinowane testem WS1; **konflikt rozstrzygniety (Rule 7): odrzucamy „resolution floor = step/2" z Proposal 1, bo mapuje 984 W na z=2.0 i wycisza jedyny czujnik dzialajacy dzis poprawnie (83% precyzji).**
- SYNC-1: `grep -rn "DETECTOR_DEFAULTS\|'720'\|0.375" orchestrator/ui/src` — zero tabel wartosci poza fixture'ami testowymi; zmiana liczby w `DetectorDefaults.cs` + rebuild tylko .NET zmienia UI.
- REGRESSION-1: `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` 463+ passed/0 skipped, `npx vitest run` 217+, `python -m pytest tests/ -q`, `cd detector && python -m pytest -q` 264 passed/1 skipped (jedyny dozwolony skip: SIGTERM `test_restart_resilience.py:129`).
- SCOPE-1: encja recznie ustawiona na `name: hst` nadal waliduje i zapisuje sie z `window`/`n_trees`; jawnie odnotowane, ze `hst` pozostaje ZNANY-ZEPSUTY (F5, `detector/argus_detector/hst_detector.py:52` MinMaxScaler po nieograniczonej historii) — to sciezka rollbacku, nie parytet jakosciowy. UI oznacza `hst` copy `wymaga recznego strojenia progow`.

**Ryzyka**

- **Otwarty**: zmiana `unique_id` przenosi entity_id RAZ — 24 h historii zostaje pod starym id. Kodem nie da sie tego przeniesc; jedyna sciezka to reczny rename encji w HA przed startem 2.2.0. Udokumentowane w `argus/CHANGELOG.md`, nie zamykane.
- Kontrakt WS1/WS2: jesli WS1 wyda inny mechanizm (np. ECDF z Proposal 1), CALA tabela domyslna jest bledna, a migracja jest jednokierunkowa per plik. Blokada: bramka sekwencji w kroku (1a) + wydanie WS3 jako ostatniego commita po WS1+WS2+WS5.
- `RmadParams` jest martwy, dopoki WS2 nie przepisze `ScoreStreamPipeline.cs:445-452` (`d.Name == "hst"`, fallback `new HstParams()` = 250/0.7/0.3) i `EntityRuntimeState.cs:69-81`.
- `medianIntervalSec` pochodzi z WS2; brak -> UI degraduje sie do samych probek i problem klasy czujnika znika z oczu.
- Kolizja nazw `rmad` vs istniejacy batchowy `mad` (`DetectorDefaults.cs:35-39`) — literowka wybiera detektor, ktory bez InfluxDB nigdy nie startuje (`Program.cs:175`). Mitygacja tylko copy w `DetectorEntry.tsx`.
- Downgrade niebezpieczny: stary obraz odrzuca `rmad` w `InputValidator`; recovery = kopia `.pre-v2.bak`.
- `model_store.py:305` bramkuje wylacznie `river_version`, a `rmad` nie uzywa river — pierwszy checkpoint `rmad` powstaje dzieki tej migracji, a schema-guard nalezy do WS1. Niezalatane tutaj.
- Bez ciszy na `lodowkababcia_power`/`zamrazarkapiwnica_power`: WS5 (2.1.14) jest juz w obrazie, wiec `ScoreStreamPipeline.cs:320` primuje swiezy klucz `rmad` z Recordera przy pierwszym otwarciu strumienia po Swapie (`registry.py:261-271` pomija tylko klucz z `n_seen > 0`). ~6.5 h ciszy (min_samples 60 przy ~391 s/probke, `Kalibracja n/60`) zostaje wylacznie na sciezce awaryjnej: sonda z §5.3 padla albo `BackfillEnabled=false`.
- Brak CI: `.github/workflows/build.yml` odpala sie tylko na tagach `v*`, wydania 2.1.4–2.1.11 nie mialy tagu; cala weryfikacja to cztery lokalne komendy.
- Jezyk UI pozostaje niespojny (PL badge/help, EN chrome i komunikaty walidacji wg kontraktu parytetu) — zglaszane, nie rozstrzygane tutaj.

**Poza zakresem** — implementacja `rmad_detector.py`, `registry._create_detector`/`_get_or_create`, `_hst_keys`, trzy zahardkodowane `'hst'` w `servicer.py`, pole `Point.detector` (WS1). Sciezka werdyktu, `BuildDetectorParamsMap`, wypelnianie `Verdict.expected/lower/upper`, `MedianIntervalSec`, F8 (publikacja flagi tylko na przejsciu), `MaxEventDurationSec` (WS2). `rmad_seasonal`. Usuwanie `hst`, jego UI, walidacji ani checkpointow (`/data/models/*/hst/` NIE czyscimy — to rollback). Opcje add-onu (`argus/config.yaml` options/schema, `argus/translations/*.yaml`, `tests/test_config_schema.py`) poza bumpem `:3`. F9 (`sensor.zamrazarkapiwnica_power` niewidoczny w `/api/sensors`; `GlobExpander.cs:84-88` nadal go usuwa przy zapisie — zywy hazard utraty danych, WS4), F10, F11, F12, F5.

### WS4 — Rejestr czujnikow: duch + 246 brakujacych encji

**Cel** — Doprowadzic `IHaSensorRegistry` do stanu, w ktorym GET /api/sensors zwraca komplet numerycznych encji HA (F10: 403 vs 157) i kazda sledzona encja jest widoczna oraz edytowalna w UI, nawet gdy nie ma jej w snapshotcie (F9: `sensor.zamrazarkapiwnica_power`) — z jawna galezia „diagnoza jako produkt", jesli hipoteza wiodaca upadnie.

**Zmiany**
- [modify] `orchestrator/Argus.Orchestrator/Ha/HaSensorRegistry.cs` — snapshot przestaje byc wylacznie connect-only. Nowa metoda `void Upsert(HaStateDto state, bool isTracked)`: copy-on-write `Dictionary<string,HaSensorEntry>` (klucz = EntityId, `StringComparer.OrdinalIgnoreCase`), filtr wartosci **identyczny** z linia 42 (`double.TryParse(s.State, NumberStyles.Any, InvariantCulture, out _)`) — encja niedajaca sie sparsowac nie usuwa istniejacego wpisu, tylko jest pomijana (unavailable/unknown NIE kasuje encji z pickera). `UpdateSnapshot` (linia 36) zachowuje sygnature i pozostaje pelna wymiana, ale **merguje**: encje obecne w starym snapshotcie a nieobecne w `states` sa zachowywane z flaga `StaleSince`. Rekord `HaSensorEntry` (`Ha/IHaSensorRegistry.cs:44`) zyskuje `bool KnownToHa` i `DateTime? StaleSince`.
- [modify] `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — `OnStateChanged` (podpiety w linii 170) woła `_sensorRegistry.Upsert(dto, _configuredEntities.Contains(dto.EntityId))` **przed** istniejacym filtrem `_configuredEntities`; subskrypcja `state_changed` jest globalna, wiec kazda encja, ktora kiedykolwiek zmieni stan, trafia do pickera bez drugiego WebSocketu (ADR-4, `.planning/milestones/v3.0-ROADMAP.md:128` — jedyny cytat, ktory sie broni; „Anti-Pattern 5" z `ARCHITECTURE.md:468` dotyczy kolizji namespace group/entity i NIE jest tu zrodlem).
- [modify] `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — drugi `GetStatesAsync` w tym samym polaczeniu: po linii 135 (`var states = await client.GetStatesAsync(ct)`) i **przed** `SubscribeStateChangedAsync` (linia 167) dodac `await Task.Delay(RegistrySettleSeconds)` + powtorny `GetStatesAsync` + `UpdateSnapshot`, gdy `isFirstConnection`. Domyslnie `ARGUS_REGISTRY_SETTLE_SEC = 60` (0 = wylaczone). Powod: przy starcie add-onu czesc integracji jeszcze sie laduje, stany sa `unknown`/`unavailable` i wypadaja na filtrze linii 42 — dzis na stale.
- [modify] `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs:144-147` — log rozbity na trzy liczniki: `"Sensor registry updated: {Numeric} numeric / {Total} states / {NonNumeric} non-numeric ({Pass} pass)"`, `Pass` ∈ {initial, settle, reconnect}. To jest sonda D1 — bez niej diagnostyka F10 jest zgadywaniem.
- [modify] `orchestrator/Argus.Orchestrator/Program.cs:283-322` (GET /api/sensors) — projekcja to **suma** `registry.GetFiltered(q)` oraz `SensorTracking.TrackedIds(liveCfg.Get())`. Dla id sledzonego, ktorego nie ma w snapshotcie, syntetyzowany wpis: `currentValue` z `statusCache.Get(id)` (albo `null`), `domain` = prefiks przed pierwsza kropka, `isTracked=true`, nowe pole `knownToHa=false`. To zabija F9: `sensor.zamrazarkapiwnica_power` pojawia sie na ekranie Detektorow i da sie go odznaczyc.
- [modify] `orchestrator/Argus.Orchestrator/Config/GlobExpander.cs:84-88` i `:92-96` — `allIds` rozszerzone o `currentlyTrackedIds` przekazane przez wywolujacego (POST /api/sensors/save, `Program.cs:344`). Dzis `manuallyChecked` przechodzi tylko gdy `allIds.Contains(id)`, wiec kazdy zapis po cichu **usuwa** encje-ducha z entities.yaml (bezposredni krewny G-14-1). Komentarz WR-03 zostaje: dowolne stringi z formularza nadal odrzucane — poszerzenie dotyczy wylacznie id juz obecnych w konfiguracji.
- [modify] `orchestrator/Argus.Orchestrator/Config/ConnectionSettings.cs` — `public int RegistrySettleSeconds { get; set; } = 60;` w bloku D-13/D-16 (orchestrator-only, `ARGUS_REGISTRY_SETTLE_SEC`, `Math.Clamp(v, 0, 600)`, nigdy nie rzuca — precedens D-15 `Program.cs:52-55`). Bez zmian w `argus/config.yaml` schema i w `argus/translations/{en,pl}.yaml`.
- [modify] `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — `SensorRegistryUpserted = new(5023)`, `SensorRegistryGhost = new(5024)` (jeden WARN per sledzona encja nieobecna w snapshotcie, przy kazdym pass).
- [modify] `orchestrator/ui/src/api/types.ts` (`SensorEntry` + `knownToHa: boolean`), `orchestrator/ui/src/components/SensorListRow.tsx` i `DetectorListRow.tsx` — chip PL (D8) „Nieznana w HA" dla `knownToHa === false`; wiersz pozostaje w pelni interaktywny (odznaczenie, edycja detektora).
- [modify] `argus/config.yaml:3` — bump wersji + `./deploy/build-push.ps1 -Version X.Y.Z`; sam blok `schema:` nietkniety. (Zgodnie z memory-rule: push do gita ≠ update w HA.)

**Testy**
- `orchestrator/Argus.Orchestrator.Tests/HaSensorRegistryTests.cs :: Upsert_NonNumericState_DoesNotRemoveExistingEntry` — `unavailable` nie kasuje wpisu; reguła: boot-time `unknown` nie moze byc trwala utrata encji.
- `…HaSensorRegistryTests.cs :: Upsert_NewNumericEntity_AppearsInGetAllWithoutReconnect` — encja widziana wylacznie przez `state_changed` trafia do pickera (rdzen naprawy F10).
- `…HaSensorRegistryTests.cs :: UpdateSnapshot_MergesInsteadOfDropping_MarksStaleSince` — reconnect z krotszym `get_states` nie zeruje pickera.
- `…HaSensorRegistryTests.cs :: Upsert_NumberAndTextDomains_AreAccepted` — `number.x_termostat_algorithm_scale_factor`, `text.y`: filtr jest wylacznie `double.TryParse`, zadnego filtra domeny (F10: brakuje 32 `number.*`, 6 `text.*`).
- `orchestrator/Argus.Orchestrator.Tests/SensorsEndpointTests.cs :: GetSensors_TrackedEntityMissingFromSnapshot_IsStillReturnedWithKnownToHaFalse` — kodyfikuje F9.
- `…SensorsEndpointTests.cs :: GetSensors_UnionDoesNotDuplicate_WhenEntityIsBothTrackedAndInSnapshot`.
- `orchestrator/Argus.Orchestrator.Tests/GlobExpanderTests.cs :: Expand_TrackedGhostEntity_SurvivesSave` — zapis z dowolnego ekranu nie usuwa `sensor.zamrazarkapiwnica_power`.
- `…GlobExpanderTests.cs :: Expand_ArbitraryUnknownId_StillRejected` — WR-03 nienaruszone.
- `orchestrator/Argus.Orchestrator.Tests/NetDaemonHaEventSourceTests.cs :: FirstConnection_RunsSettleSnapshot_BeforeSubscribe` — kolejnosc get_states → settle get_states → subscribe (bez routera wiadomosci, `HaWebSocketClient.cs:35-37`).
- `…NetDaemonHaEventSourceTests.cs :: SettleSecondsZero_IssuesExactlyOneGetStates` — knob wylaczalny.
- `orchestrator/ui/src/components/SensorList.test.tsx :: renders unknown-to-HA tracked entity with the Polish chip`.

**Kryteria akceptacji**
- F10 (glowne): po deployu i `ARGUS_REGISTRY_SETTLE_SEC=60`, `curl` z wnetrza kontenera add-onu (`for i in $(seq 1 1); do curl -s -H "X-Forwarded-For: 172.30.32.2" localhost:8099/api/sensors; done | jq '.entries|length'`) zwraca **liczbe rowna** licznikowi `Numeric` z linii logu `Sensor registry updated` pass=settle. Rozbieznosc > 0 = utrata po stronie endpointu, nie rejestru.
- F10 (wartosc bezwzgledna): po 60 min pracy `.entries|length >= 380` oraz `[.entries[].domain]|group_by(.)` zawiera `number` >= 25 i `text` >= 4. Jesli po 60 min < 380 — patrz galaz B7 nizej; release nie jest blokowany.
- F10 (imiennie): `sensor.expminimp`, `number.*_termostat_occupied_heating_setpoint_scheduled`, `number.*_termostat_external_measured_room_sensor`, `number.*_termostat_algorithm_scale_factor`, `number.*_termostat_load_room_mean` obecne w odpowiedzi.
- F9: `sensor.zamrazarkapiwnica_power` widoczne na ekranie Detektorow z chipem statusu; odznaczenie + Zapisz usuwa je z `/data/entities.yaml`, a log pokazuje 0 linii `SensorRegistryGhost` po zapisie. Przed zmiana encja byla scorowana (score 0.996) i niewidoczna.
- E4 (dowod end-to-end dla `number.*`, nie samo listowanie): wybrac jedna `number.*_termostat_occupied_heating_setpoint_scheduled`, zaznaczyc w pickerze, Zapisz; w ciagu 120 s w HA istnieja `binary_sensor.argus_<slug>_anomaly` i `sensor.argus_<slug>_score` (`Mqtt/UniqueId.cs:12-18` po D-G — bez nazwy detektora), score sensor ma wartosc liczbowa != `unknown`, a w logu jest werdykt dla tego entity_id. Kryterium spelnione tylko przy obu encjach; sam wpis w `entities.yaml` nie liczy sie.
- Regresja: `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` — 463 istniejace + nowe, 0 failed, 0 skipped (wczesniej `bash deploy/generate-certs.sh 127.0.0.1 gpu-host`). `cd orchestrator/ui && npm test` bez regresji (Node 26 + vitest — patrz notatka z Fazy 13). `git diff --stat` nie dotyka `detector/**` ani `proto/argus.proto`.
- Zywy strumien nietkniety: przez pelny cykl startu zero linii „HA WebSocket connection lost — backing off" (`NetDaemonHaEventSource.cs:187-189`) i zero „HA WebSocket message exceeded 4194304 bytes" (`HaWebSocketClient.cs:259-261`).

**Ryzyka**
- **Otwarta niewiadoma (odpowiedz na B7): przyczyna F10 nie jest ustalona i moze obalic hipoteze wiodaca.** Hipotezy: **H1** — snapshot jest connect-only, a przy starcie add-onu czesc encji ma stan `unknown`/`unavailable` i wypada na `HaSensorRegistry.cs:42`; **H2** — `get_states` przekracza 4 MB (`HaWebSocketClient.cs:246`); **H3** — tozsamosc Supervisor-proxy (`argus/rootfs/etc/cont-init.d/10-config-gen.sh:37-38`, SUPERVISOR_TOKEN) widzi mniej encji; **H4** — strata ponizej rejestru (endpoint/SPA).
  Diagnostyka, timebox **jeden cykl restartu add-onu + 2 h**: D1 = trzy liczniki z nowej linii logu; D2 = `jq` z wnetrza kontenera (jak wyzej); D3 = surowa sonda `get_states` przez `ws://supervisor/core/websocket` z SUPERVISOR_TOKEN, liczaca `total`, `numeric` (regex `^-?\d+(\.\d+)?$`), `nonNumeric`; D4 = powtorka D3 30 min pozniej → `lateAddedCount`; D5 = roznica zbiorow D3 vs snapshot z podzialem na kubelki {non-numeric-at-snapshot | absent-from-get_states | numeric-but-missing}.
  **Warunek stopu i co ships, gdy H1 padnie** (D5 pokazuje `lateAddedCount < 10` i kubelek non-numeric < 50): NIE prowadzimy dalszego sledztwa w tym workstreamie. Shipuja bezwarunkowo trzy zmiany, ktore sa poprawne niezaleznie od przyczyny — upsert z `state_changed`, unia w GET /api/sensors (F9), guard w `GlobExpander` — a kryterium „>= 380" zostaje **zastapione** przez: `.entries|length == numeric` z D3, plus wpis w `.planning/debug/f10-missing-entities.md` z kubelkami D5 i wskazaniem H2/H3/H4 jako otwartych. Jesli D3 pokazuje `numeric == 157` przy 403 widocznych w HA UI, to jest H3 — problem po stronie uprawnien HA, nie kodu; WS4 konczy sie diagnoza i to jest pelny wynik workstreamu.
- `Upsert` na kazdym `state_changed` to zapis copy-on-write przy ~403 encjach z instancji; przy 10 zdarzen/s to ~10 kopii slownika/s. Mitigacja: batch-swap co 1 s albo `ImmutableDictionary`; nie optymalizowac przed pomiarem.
- Merge zamiast pelnej wymiany w `UpdateSnapshot` oznacza, ze encja usunieta z HA zostaje w pickerze do restartu (widoczna jako `StaleSince`). Swiadomy kompromis: falszywy pozytyw w liscie jest tansze niz F10.
- Opoznienie 60 s przed `SubscribeStateChangedAsync` wydluza okno, w ktorym zdarzenia nie sa konsumowane — pierwsze scorowanie po starcie przesuwa sie o te 60 s. Przy `ARGUS_REGISTRY_SETTLE_SEC=0` zachowanie sprzed zmiany.
- Encje, ktore nigdy nie zmieniaja stanu i byly `unknown` w obu pass, pozostana niewidoczne. Zaden mechanizm w tym WS ich nie odzyska bez trzeciego `get_states` lub drugiego socketu.

**Poza zakresem**
- Detektor, progi, kalibracja, bramka: `detector/**`, `proto/argus.proto`, `Detection/HysteresisGate.cs`, `Detection/FrozenSensorDetector.cs`, `Mqtt/StatePublisher.cs` — nietkniete (F1–F8 nalezą do innych WS).
- Backfill/historia HA Recorder (F11/F12) — osobny WS; `IInfluxDataSource`, `ScoreStreamPipeline.PrimeFromHistoryAsync` bez zmian.
- Detektory grupowe („Oczekuje", peer_divergence/copod).
- Zmiana `unique_id` (`Mqtt/UniqueId.cs:12-18`) i migracja encji HA po zmianie detektora — to blocker upgrade'u nalezacy do WS migracji detektora, nie tutaj.
- Read-back zapisanych parametrow detektora w GET /api/sensors (`ui/src/state/sensors.ts` `getOrInitEdit`) — osobny defekt, nie naprawiany w tym WS.
- Jakikolwiek nowy add-on option, string tlumaczenia, `tests/test_config_schema.py`, `10-config-gen.sh`.

### WS5 — Historia HA Recordera jako zrodlo danych

**Cel** — Zaimplementować `IInfluxDataSource` nad HA WebSocket (`history/history_during_period`), żeby priming rozgrzewki i podgląd historii działały na tym wdrożeniu, gdzie `influxUrl=null` (F11), korzystając z 7 dni Recordera (F12).

**Zmiany**
- [create] `orchestrator/Argus.Orchestrator/Ha/HaRecorderHistorySource.cs` — implementacja obu metod seamu `Batch/IInfluxDataSource.cs:17`. Kontrakt `lookback` skopiowany **werbatim** z `Batch/InfluxDbReader.cs:25-26,157-158` (`^\d+[smhdw]$`, else `ArgumentException`). Kolejność wyników identyczna jak `InfluxDbReader.cs:167-176`: pobierz okno, weź **ostatnie** `limit` punktów, zwróć **rosnąco**. Filtr wartości: `double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture)` — `unknown`/`unavailable`/tekst odrzucane cicho, nie jako błąd. Cache per `(entityId, lookback, limit)` TTL **60 s**, max 32 wpisy, LRU — zamyka E2 (burza connect+auth przy debounce 400 ms).
- [modify] `orchestrator/Argus.Orchestrator/Ha/HaWebSocketClient.cs` — nowa `Task<JsonElement> GetHistoryAsync(string entityId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)`, kształt 1:1 z `GetAreaRegistryAsync` (`:106-132`). Payload: `{type:"history/history_during_period", start_time, end_time, entity_ids:[id], minimal_response:true, no_attributes:true, significant_changes_only:false}`. **Jedna komenda na encję** — limit ramki 4 MB (`:246`); wyjątek z `ReceiveMessageAsync` (`:259-261`) łapany lokalnie w źródle historii, nigdy nie propagowany.
- [modify] `orchestrator/Argus.Orchestrator/Ha/NetDaemonHaEventSource.cs` — **rozstrzygnięcie konfliktu (Rule 7)**: zapytania historyczne idą przez **osobne, krótkotrwałe** połączenie WS (connect→auth→1 komenda→close, `SemaphoreSlim(1,1)`), nie przez żywy socket. Powód: socket nie ma routera wiadomości (`HaWebSocketClient.cs:35-37`), więc request po `SubscribeStateChangedAsync` (`:167`) zjadałby ramki `state_changed`. ADR-4 „no second WebSocket" (`NetDaemonHaEventSource.cs:142`) dotyczy drugiego **trwałego** kanału zdarzeń — tranzytowe query nie tworzy drugiego strumienia; decyzję dopisać jako komentarz przy `:142`, inaczej następny czytelnik ją cofnie.
- [modify] `orchestrator/Argus.Orchestrator/Program.cs:207-214` — w gałęzi `else` (brak `influxUrl`) rejestruj `HaRecorderHistorySource` jako `IInfluxDataSource`. Komentarz `Program.cs:150-155` (dlaczego dep jest opcjonalny i rozwiązywany przez `GetService`) przepisać: od teraz `IInfluxDataSource` jest **zawsze** zarejestrowany, null tylko gdy HA WS niedostępny.
- [modify] `orchestrator/Argus.Orchestrator/Detection/ScoreStreamPipeline.cs:366` `PrimeFromHistoryAsync` — żądaj `rmad.window` (720) wierszy zamiast 250; `min_samples` 60 jest progiem gotowości, nie żądania. Degrade catch-all `:418-424` bez zmian (9 testów backfillu zielone), `:369-370` no-op tylko gdy `_historySource is null`.
- [modify] `orchestrator/Argus.Orchestrator/Config/ConnectionSettings` + `argus/config.yaml` schema-neutral — domyślny `backfillLookback: "8d"` (F12: Recorder trzyma 7 d; 8 d pokrywa granicę, zapytanie 30 d i tak zwraca te same 1546 wierszy).
- [modify] `argus/config.yaml:3` (`version:`) + `./deploy/build-push.ps1 -Version X.Y.Z`, commit `argus/config.yaml`, Update w HA — **krok kończący WS5** (C3: `git push` ≠ update w HA).

**Testy**
- `Argus.Orchestrator.Tests/HaRecorderHistorySourceTests.cs::Lookback_RejectsBadShape_AcceptsCanonical` — `"7 days"`/`"d7"` → `ArgumentException`; `"8d"`, `"24h"`, `"600s"` OK. Koduje kontrakt seamu (`InfluxDbReader.cs:25-26`).
- `…::SeamParity_SameLookback_YieldsSameOrderAndCount_AsInfluxReader` — fake WS zwraca 1000 punktów, `limit=720`: dokładnie 720, rosnąco, **najnowsze**. Koduje E3 — parzystość dwóch implementatorów `IInfluxDataSource`.
- `…::NonNumericStatesAreDropped_NotFatal` — `unknown`/`unavailable` w środku serii → pomijane, reszta zwrócona. Koduje F10-klasę błędu (parsowanie stanu).
- `…::CachedWithin60s_OpensOneConnection` — 5 wywołań w 1 s = 1 connect; 61 s później = 2. Koduje E2.
- `…::WebSocketFailure_ReturnsEmpty_NeverThrows` — fake rzuca w `ReceiveMessageAsync`; wynik: 0 wierszy, brak wyjątku. Koduje: awaria historii degraduje do „brak primingu", nigdy nie zrywa żywego strumienia (`HaWebSocketClient.cs:259-261`).
- `…::OneCommandPerEntity` — żądanie 3 encji = 3 komendy, nigdy jedna zbiorcza. Koduje limit ramki 4 MB (`:246`).
- `ScoreStreamPipelineTests.cs::PrimeFromHistory_RequestsWindowRows_Not250` — asercja `limit == 720`. Koduje: `min_samples=60` to próg gotowości, ale okno bazowe musi być pełne, inaczej MAD liczony z 60 próbek.
- `ScoreStreamPipelineTests.cs` (istniejące 9 testów degrade, `:666-899`) — muszą zostać zielone bez zmian.
- `…::HistorySourceRegistered_WhenInfluxUrlNull` — kontener bez `influx_url` rozwiązuje `IInfluxDataSource` niepusto. Koduje F11.

**Kryteria akceptacji**
- F11: przy `influxUrl=null` log startowy zawiera po jednej linii INFO na śledzoną encję: `primed <entity> <n> points from HA Recorder`, z `n>0` dla wszystkich 5 encji.
- F12: `lookback=8d` dla `sensor.lodowkababcia_power` zwraca 1546 ±20 wierszy, najstarszy znacznik ≥ `2026-08-27T05:18Z`; to samo zapytanie z `30d` zwraca tę samą liczbę.
- D2 (ślepota po migracji na `rmad`): po restarcie **pierwszy werdykt** dla każdej z 5 encji ≤ 2 min od startu (bez primingu byłoby ~6 h dla `lodowkababcia` przy ~225 pkt/dobę). Mierzone: znacznik pierwszej linii `verdict` w logu minus start dodatku.
- `sensor.zamrazarkapiwnica_power`: 225 pkt/d × 7 d = 1575 ≥ 720 → po starcie `warmed_up=true` bez czekania; jeśli encja ma < 720 punktów w 8 d, log **WARN** z nazwą encji i liczbą punktów (fail loud, Rule 12), flaga zostaje OFF.
- Nieinwazyjność wobec żywego strumienia: w 10 min po starcie brak przerwy > 2× median gap w `state_changed` dla `sensor.load_5m`; zero `WebSocket` reconnect w logu przypisanych do zapytań historycznych.
- Cache: 200 kolejnych zapytań o `(sensor.load_5m, 8d)` w ciągu 60 s → dokładnie 1 połączenie WS (licznik w logu Debug). Receptura wykonania z wnętrza kontenera: `for i in $(seq 200); do curl -s -o /dev/null -X POST http://127.0.0.1:8099/api/sensors/sensor.load_5m/simulate -H 'Content-Type: application/json' -d '{"detector":"rmad","params":{},"lookback":"8d","maxPoints":2000}'; done` — `127.0.0.1`, nie `localhost`: Kestrel binduje wyłącznie IPv4 (`Program.cs:219`) (loopback przechodzi `IsAuthorizedRequest`, `Program.cs:264-278`).
- Zero zmian w `proto/argus.proto`, zero regeneracji stubów, zero nowej zależności: `git diff detector/requirements.txt` i `orchestrator/ui/package.json` puste.
- `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` zielone; `cd detector && python -m pytest -q` zielone (jeden pre-existing skip win32, `test_restart_resilience.py:129`).
- Wydanie: `argus/config.yaml:3` podbity, `./deploy/build-push.ps1` wykonany, Update w HA potwierdzony.

**Ryzyka**
- **OTWARTE**: `history/history_during_period` nie występuje nigdzie w repo — kształt żądania, kodowanie odpowiedzi (`minimal_response` zwraca listy skrócone, pierwszy element pełny) i koszt bajtowy trzeba zmierzyć na żywej instancji przed release'em. Błąd tutaj = wyjątek w `ReceiveMessageAsync` (`HaWebSocketClient.cs:259-261`); dlatego osobne, tranzytowe połączenie, a nie współdzielony socket.
- Decyzja o tranzytowym WS jest sprzeczna z literalnym brzmieniem ADR-4 (`NetDaemonHaEventSource.cs:142`). Alternatywa (kolejka na żywym sockecie) wymaga routera wiadomości, którego nie ma (`:35-37`) — odrzucona jako większa zmiana. Do zatwierdzenia przez właściciela ADR.
- Priming przy starcie to 6 × (connect+auth) w kilka sekund; przy większej liczbie encji trzeba serializować (semafor już to robi) i dodać backoff — nietestowane przy > 20 encjach.
- Recorder trzyma tylko 7 d: encja rzadsza niż ~90 pkt/dobę nigdy nie napełni okna 720. Kryterium WARN wyżej to ujawnia, ale nie naprawia — realny limit `rmad.window` dla wolnych czujników.
- Time-weighted metryki liczone z historii Recordera dziedziczą jego artefakty (jedna wielka luka po przestoju HA przypisana ostatniemu stanowi). Nie clampuję dwella — clamp uczyniłby liczby nieporównywalnymi z F1/F13.
- CI (`.github/workflows/build.yml`) uruchamia tylko `dotnet test`; pytest i vitest są bramką wyłącznie lokalną.

**Poza zakresem**
- Zmiana scorera/kalibracji (`hst_detector.py`, `model_store.py`, `state_version`) — WS1.
- Bramka i progi (`HysteresisGate.cs`, `EntityRuntimeState.cs`, `DetectorDefaults.cs`, `sensors.ts`, `detectorParams.ts`) — WS2.
- Migracja `hst→rmad`, `schema_version: 2`, czyszczenie retained discovery starego `unique_id` (`UniqueId.cs:12-18`), `argus/CHANGELOG.md` — WS3.
- F9 (`sensor.zamrazarkapiwnica_power` poza `GET /api/sensors`), F10 (246 brakujących encji, `GlobExpander.cs:84-88`) — WS4.
- `POST /api/sensors/{entityId}/simulate`, `SimulateService`, `ReplaySimulator`, `SimulateBatch` w proto/servicerze, panel `Testuj na historii`, `argus/Dockerfile` + `gen_proto.py` (wzorzec: `deploy/Dockerfile.detector:22-27`) — WS6; WS5 dostarcza wyłącznie `IInfluxDataSource`, na którym WS6 stoi.
- Ścieżka wsadowa (`BatchSchedulerWorker`), grupy (`GroupInfluxReader`, `ScoreGroupBatch`), zapis czegokolwiek do `/data`.

### WS6 — Symulator odtwarzania + wykres w UI

**Cel** — dać operatorowi panel „Testuj na historii" per encja: odtworzenie zapisanej historii HA/Influx przez detektor w *sandboxie* (bez dotykania żywego modelu) + bramkę, z liczbami epizodów/`alertsPerDay`/on-time, żeby WS2–WS5 dało się zweryfikować liczbowo zanim trafi na produkcję.

**Zmiany**

- [modify] `proto/argus.proto` — nowe wiadomości (własna przestrzeń numerów, nic nie renumerowane, `Verdict` 1-11 i `Point` nietknięte) + nowe rpc na końcu `service AnomalyDetector` (obok ScoreStream/Fit/ScoreBatch/SaveModel/LoadModel/ScoreGroupBatch/FitGroup/Warmup):
  ```
  message SimulateRequest {
    string entity_id = 1; string detector = 2; map<string,string> params = 3;
    repeated Point history = 4; string request_id = 5;
  }
  message SimulateResponse {
    bool ok = 1; string error = 2;
    repeated double scores = 3;      // packed, len == len(history), 1:1 po indeksie
    repeated double robust_z = 4;    // packed, len == len(scores) albo 0 gdy detektor nie liczy z
    uint32 window = 5;               // efektywne okno rozgrzewki (hst: window, rmad: min_samples)
    uint32 warmed_up_from_index = 6; // pierwszy indeks scorowalny; scores[i<idx] == 0.0 i NIE wolno ich bramkować
    string detector_version = 7;
  }
  rpc Simulate(SimulateRequest) returns (SimulateResponse);
  ```
  `repeated double` (nie `DoubleValue`) — tablica, brak potrzeby nullability. Regeneracja: .NET automatycznie przez Grpc.Tools (`Argus.Orchestrator.csproj:23`); Python ręcznie `python detector/scripts/gen_proto.py`.
- [modify] `argus/Dockerfile` — **rozstrzygnięcie C2:** stuby Pythona pozostają gitignorowane i NIE są commitowane; dodaj przed `COPY detector/`-owym etapem uruchomienie `RUN python detector/scripts/gen_proto.py` (dziś Dockerfile nigdy go nie woła → czysty checkout dawał detektor bez `argus_pb2*.py`). Bez tego kroku żadna zmiana proto nie może wyjść.
- [create] `detector/argus_detector/simulate.py` — `run_simulation(detector: str, params: dict, values: list[float]) -> tuple[list[float], list[float], int, int]`. Buduje instancję przez `registry._create_detector(detector, params)` (registry.py:445) w **lokalnej zmiennej**; instancja nigdy nie trafia do `DetectorRegistry._detectors`, nie bierze locka per-encja, nie ustawia `checkpoint_dirty`, nie zapisuje nic pod `/data/models`. To zamyka F14 (`ScoreBatch` fituje żywy model dla `(entity_id, detector)` — niebezpieczny na śledzonej encji).
- [modify] `detector/argus_detector/servicer.py` — `Simulate(request, context)`: waliduje `len(history) >= 1`, woła `run_simulation`, zwraca `SimulateResponse`. **Nigdy `context.abort`** — nieznana nazwa detektora / wyjątek → `ok=false, error="..."` (servicer.py:104-107 abortuje cały multipleksowany stream; symulator nie może zabić scoringu).
- [modify] `orchestrator/Argus.Orchestrator/Batch/IBatchDetectorClient.cs` — dodaj `Task<SimulateResult> SimulateBatchAsync(string entityId, string detector, IReadOnlyDictionary<string,string> parameters, IReadOnlyList<HistoryPoint> history, CancellationToken ct);` + `public sealed record SimulateResult(bool Ok, string? Error, IReadOnlyList<double> Scores, IReadOnlyList<double> RobustZ, int Window, int WarmedUpFromIndex, string DetectorVersion);`. Wszystkie atrapy `IBatchDetectorClient` w testach dostają implementację w tym samym commicie (inaczej CS0535 wywala solution).
- [modify] `orchestrator/Argus.Orchestrator/Batch/BatchDetectorClient.cs` — implementacja: mapuje na `Point{EntityId, Value, Timestamp}`, ustawia `RequestId = Guid`, deadline 30 s, `catch (RpcException ex)` → `SimulateResult(false, ex.Status.Detail, [], [], 0, 0, "")`. Rejestracja klienta musi być poza blokiem gated na Influx (`Program.cs:189`), bo symulator działa też z `influxUrl=null` (F11).
- [create] `orchestrator/Argus.Orchestrator/Detection/ReplaySimulator.cs` — czysta funkcja `SimulateSummary Run(IReadOnlyList<HistoryPoint> history, SimulateResult sim, GateParams gate)`. Odtwarza **tę samą** `HysteresisGate` + `min_consecutive` co produkcja, startując od `sim.WarmedUpFromIndex`. Zwraca `public sealed record SimulateSummary(int Episodes, double OnTimePercent, double SpanHours, double AlertsPerDay, int ScorablePoints, int Transitions, DateTimeOffset FirstScorableAt)`; `AlertsPerDay = Episodes * 24.0 / SpanHours`, `SpanHours = (last.Ts - first.Ts).TotalHours` liczone **tylko po regionie scorowalnym**. To jest miejsce powstania `spanHours`/`alertsPerDay` (luka F/krytyki).
- [create] `orchestrator/Argus.Orchestrator/Web/SimulateService.cs` — `Task<SimulateSummary> RunAsync(string entityId, string detector, IReadOnlyDictionary<string,string> parameters, string lookback, int maxPoints, CancellationToken ct)`. Historia przez istniejący seam `Batch/IInfluxDataSource.cs` (implementacja Influx **lub** HA-Recorder z WS5). **E2:** `ConcurrentDictionary<(string entityId, string lookback), (DateTimeOffset fetchedAt, IReadOnlyList<HistoryPoint> rows)>`, TTL **60 s**, wpis usuwany po TTL — edycja parametrów co 400 ms wykonuje 1 pobranie historii, nie N połączeń WS do Core. `maxPoints` domyślnie 2000, klamrowane do [100, 5000]; `lookback` waliduje `^\d+[smhdw]$` (kontrakt z `InfluxDbReader`), domyślnie `24h` (**B5** — porównywalność z F13).
- [modify] `orchestrator/Argus.Orchestrator/Program.cs` — nowy `POST /api/sensors/{entityId}/simulate` obok `/api/health` (Program.cs:677-685), ten sam `IsAuthorizedRequest` (precedens Program.cs:285). Body `{detector, params, lookback, maxPoints}`; 400 na zły `lookback`, 404 gdy encji nie ma ani w snapshotcie ani w `entities.yaml`, 503 gdy brak `IInfluxDataSource`. Odpowiedź: `{ ok, error, summary, scores, values, timestamps, warmedUpFromIndex, window }` przez jawny rekord projekcji (konwencja allowlisty D-07, Program.cs:675-676), nie surowy `SimulateResult`.
- [modify] `orchestrator/Argus.Orchestrator/Logging/LogEvents.cs` — `SimulateCompleted = new(7013, ...)`, Information, format: `"Simulate {EntityId} detector={Detector} points={Points} episodes={Episodes} alertsPerDay={AlertsPerDay} durationMs={DurationMs}"` — jedyne źródło **czasu odpowiedzi symulatora** (**B8**, §5.6); latencja werdyktu ma osobne źródło i osobne kryterium (A15: pole `latency_ms`, `ScoreStreamPipeline.cs:242-244`).
- [create] `orchestrator/ui/src/components/ReplayPanel.tsx` — panel „Testuj na historii" w edytorze pojedynczej encji. Inline SVG sparkline (surowa wartość + score + pasy epizodów), **bez biblioteki wykresów** (decyzja `AttributionBar.tsx:9`). Nagłówek liczb: `epizody`, `alertów/dobę`, `on-time %`, `zakres h`. Debounce 400 ms + `Gate.Wait(0)` (jedno żądanie w locie). **A/F4:** przy `detector === 'hst'` renderuj `<Badge tone="warn">legacy — niekalibrowany (F4)</Badge>` obok wyniku.
- [modify] `orchestrator/ui/src/state/replay.ts` (create) — `replayState`/`replayEnabled` przestają być globalne: stan trzymany jako `Map<entityId, ReplayState>`, a `ReplayPanel` czyta `replayFor(entityId)`. Dodatkowo `useEffect(() => { resetReplay(entityId) }, [entityId])` (**E1**) — przejście `#/detectors/sensor/A` → `.../B` nigdy nie pokazuje wyniku A pod B.
- [modify] `orchestrator/ui/src/api/client.ts` + `types.ts` — `postSimulate(entityId, body): Promise<SimulateResponseDto>` — wołanie `apiPost('api/sensors/' + encodeURIComponent(entityId) + '/simulate', body)`, ścieżka **względna, bez wiodącego `/`** (guard `client.ts:15-18`, inaczej omija prefiks Ingressu), body `{detector, params, lookback, maxPoints}`; nowe pola DTO deklarowane jako **opcjonalne** tam, gdzie dotykają istniejących fixture'ów, żeby `tsc -b` nie padł.
- [modify] `argus/config.yaml:3` — bump wersji dodatku (`2.1.11` → następna), a po nim pełny `./deploy/build-push.ps1 -Version X.Y.Z` + commit `argus/config.yaml` + Update w HA. `git push` ≠ update w HA (**C3**).

**Testy**

- `detector/tests/test_simulate.py :: simulate_does_not_register_model` — po `Simulate` `registry._detectors` jest puste i żaden plik pod `/data/models` nie powstał; F14 sandbox.
- `test_simulate.py :: simulate_scores_align_with_history` — `len(scores) == len(history)`, `warmed_up_from_index == window` (hst: 250) / `== min_samples` (rmad: 60).
- `test_simulate.py :: simulate_unknown_detector_returns_error_not_abort` — `ok=false`, `error` niepuste, brak `context.abort` (servicer.py:104-107).
- `ReplaySimulatorTests.cs :: AlertsPerDay_NormalizesToSpanHours` — 2 epizody na 12 h → `alertsPerDay == 4`; kryterium B5.
- `ReplaySimulatorTests.cs :: NoTransitionsBeforeWarmedUpFromIndex` — punkty przed indeksem nie bramkują.
- `SimulateServiceTests.cs :: HistoryCached_60s_SingleFetchForRepeatedRuns` — 10 wywołań w 1 s → 1 wywołanie `IInfluxDataSource` (**E2**); po 61 s → 2.
- `SimulateServiceTests.cs :: DifferentLookback_UsesSeparateCacheKey`.
- `InfluxDataSourceParityTests.cs :: Lookback_8d_YieldsIdenticalRowsAcrossImplementations` — `InfluxDbReader` i `HaRecorderHistorySource` przyjmują `8d`/`24h` i zwracają ten sam zakres na wspólnym fake'u (**E3**; dziś testowany tylko zły format).
- `SimulateEndpointTests.cs :: Unauthorized_Returns403`, `:: BadLookback_Returns400`, `:: MaxPoints_ClampedTo5000`.
- `ui/src/components/ReplayPanel.test.tsx :: resets result when entityId changes` (**E1**), `:: single in-flight request under rapid param edits` (debounce 400 ms + `Gate.Wait(0)`), `:: renders legacy badge for hst` (A/F4).
- Bramka: `dotnet test orchestrator/Argus.Orchestrator.sln -c Release` (0 failed / 0 skipped), `cd orchestrator/ui && npx vitest run` **oraz `npm run build`** (`tsc -b`, to samo co `argus/Dockerfile:13` i `.github/workflows/build.yml:53`), `cd detector && python -m pytest`.

**Kryteria akceptacji**

- **C4:** po `build-push.ps1` i Update w HA panel „Testuj na historii" renderuje się pod Ingress w edytorze encji (nie tylko w dev-compose) — sprawdzone wizualnie na żywej instancji.
- **F13 (porównywalne okna):** symulacja uruchomiona jawnie z `lookback=24h` per czujnik; porównujemy wyłącznie `alertsPerDay` i on-time. Cel: `load_5m ≤ 6/dobę`, `processor_use ≤ 4/dobę`, `memory_use_percent == 0`, `zamrazarkapiwnica_power == 0`, `lodowkababcia_power ∈ [1,4]`.
- **F3 (twardy próg odbioru, zamyka A/F3):** na odtworzeniu 24 h precyzja epizodów `lodowkababcia_power ≥ 70 %` (dziś 83 % przy 91 % on-time), `zamrazarkapiwnica_power` = **0 alarmów** (seria 101–109 W, sd 1.87 W, zero outlierów). Poniżej progu WS nie przechodzi.
- **F2 (B4):** odtworzenie zimnego `hst` na tej samej historii → **0 przejść ON→OFF w regionie scorowalnym** (bez liczby procentowej — zimny `MinMaxScaler` nie odtwarza podłogi 0.480), a odtworzenie konfiguracji docelowej → ≥1 przejście ON→OFF na każdej fladze, która w oknie w ogóle zapłonęła; dla `memory`/`zamrazarka` kryterium = 0 epizodów (**B1**).
- **B3 (nieperturbacja):** po 200 odtworzeniach **brak nowych katalogów i plików pod `/data/models`** (`find /data/models -newermt '<start>'` pusty poza checkpointami żywego ruchu) oraz `n_seen` w sidecarach zgodne z licznikiem żywych odczytów z logu. Kryterium mtime porzucone — `ARGUS_CHECKPOINT_INTERVAL_SEC=300` gwarantuje zmianę mtime z żywego strumienia.
- **B6 (receptura 200 odtworzeń):** z wnętrza kontenera dodatku (loopback przechodzi `IsAuthorizedRequest`, omija debounce SPA):
  `for i in $(seq 200); do curl -s -o /dev/null -X POST http://127.0.0.1:8099/api/sensors/sensor.load_5m/simulate -H 'Content-Type: application/json' -d '{"detector":"rmad","params":{},"lookback":"24h","maxPoints":2000}'; done`
- **B8 (czas odpowiedzi symulatora — inna wielkość niż A15):** źródło = pole `durationMs` z `SimulateCompleted` (7013, Information), okno = dokładnie te 200 żądań z B6, jeden pomiar = jedno `POST .../simulate`: `grep SimulateCompleted <log> | grep -o 'durationMs=[0-9.]*' | cut -d= -f2 | sort -g | awk '{a[NR]=$1} END{printf "n=%d p95=%.1f max=%.1f\n", NR, a[int(NR*0.95)+0], a[NR]}'` → **n == 200, p95 < 1000 ms, max < 3000 ms**. Pole `latency_ms` NIE wchodzi do tego pomiaru (mierzy werdykty, nie żądania) — patrz A15.
- **E2:** w logu tych 200 żądań liczba pobrań historii ≤ liczba okien 60 s (dla ~2 min pętli: ≤ 3), nie 200.

**Ryzyka**

- Otwarte: `Simulate` na `hst` startuje zimno — wynik nie jest identyczny z żywym modelem po 16061 punktach; panel musi to napisać wprost („symulacja od zera, bez checkpointu"), inaczej operator porówna nieporównywalne.
- Otwarte: bez WS5 (źródło historii) symulator działa tylko gdy `influxUrl` ustawione; przy `influxUrl=null` endpoint zwraca 503 do czasu wejścia `HaRecorderHistorySource`. WS6 zależy od WS5 w tym jednym punkcie.
- `maxPoints=2000` przy ~5000 pkt/dobę `load_5m` pokrywa ~9.6 h — dlatego `lookback` jest jawny w kryteriach, a wynik zawsze raportuje `spanHours`.
- Zmiana proto + `gen_proto.py` w Dockerfile: pierwszy build po tej zmianie musi być zweryfikowany na czystym checkoucie (`git clean -xdn detector/` → brak `*_pb2*.py`), inaczej obraz wyjdzie bez stubów.
- Skew wersji w trybie remote-detector: stary detektor nie zna `Simulate` → `RpcException{Unimplemented}` mapowane na `ok=false`; panel pokazuje komunikat, scoring nietknięty.
- 4 MB limit ramki HA WS (`HaWebSocketClient.cs:246`) nadal fatalny dla całego połączenia — dlatego historia pobierana jest per encja i cache'owana, nigdy hurtowo.

**Poza zakresem** — algorytm detektora (WS2/WS3), bramka produkcyjna i `retain`/change-only na flagach (WS2, F8), rejestr czujników i widmo `zamrazarkapiwnica_power` (WS4, F9/F10), źródło historii HA-Recorder jako takie (WS5, F11/F12), symulacja detektorów grupowych (`peer_divergence`/`copod` — wszystkie „Oczekuje"), zapis wyników symulacji na dysk oraz jakiekolwiek automatyczne przepisanie `entities.yaml` z poziomu panelu.

## 5. Weryfikacja na żywym HA

Procedura jest **przyrostowa** — wykonywana po **każdym** wydaniu, nie raz na końcu. Każdy krok podaje: co zmierzyć, czym, i jaka wartość dowodzi sukcesu wobec F1/F3/F13.

**Podsekcje są numerowane kolejnością wydań, nie pozycją w pliku:** 5.0 → 5.1 (WS1, 2.1.12) → 5.2 (WS2, 2.1.13) → **5.3 (WS5, backfill, 2.1.14)** → 5.4 (WS3, migracja, 2.2.0) → 5.5 (WS4, rejestr, 2.2.1) → 5.6 (WS6, 2.2.2) → 5.7. W pliku bloki leżą fizycznie w kolejności 5.4, 5.5, 5.3, 5.6 — obowiązuje numer, nie pozycja. Backfill (5.3) wykonuje się PRZED migracją (5.4), zgodnie z decyzją z §3.

**Recepta wydania obowiązuje po każdym workstreamie bez wyjątku** (git push ≠ update w HA):

```
# 1. cztery komendy z 5.0 zielone
# 2. bump argus/config.yaml:3  (2.1.11 -> X.Y.Z)
./deploy/build-push.ps1 -Version X.Y.Z
git add argus/config.yaml && git commit -m "chore(release): bump add-on to X.Y.Z" && git push
# 3. Supervisor -> Argus -> Update
# 4. potwierdzić, że działa NOWY obraz:
curl -s "http://<host>/api/health" | jq '.version'      # == X.Y.Z
```

### 5.0 Przygotowanie i baza (przed pierwszym wydaniem)

```bash
bash deploy/generate-certs.sh 127.0.0.1 gpu-host          # wymagane przez DetectorChannelFactoryTests.cs:22-34
dotnet test orchestrator/Argus.Orchestrator.sln -c Release   # baza 463 passed, 0 failed, 0 skipped
cd detector && python -m pytest -q                            # 264 passed, 1 skip (test_restart_resilience.py:129)
python -m pytest tests/ -q                                    # 4 passed
cd orchestrator/ui && npm run build && npx vitest run         # tsc -b MUSI przejść; 217 passed
```

`npm run build` (czyli `tsc -b && vite build`, `ui/package.json:8`) jest **częścią bramki**, nie opcją — to dokładnie ta komenda, którą wykonuje `argus/Dockerfile:13`. `npx vitest run` samo w sobie **nie** typecheckuje fixture'ów i przepuści zmianę, która zablokuje build obrazu. Skip powyżej jednego to porażka, nie zaliczenie (Rule 12).

Brak siatki CI: `.github/workflows/build.yml` odpala się wyłącznie na tagach `v*`, wydania 2.1.4–2.1.11 nie mają tagu i nie miały przebiegu CI, a pythonowy zestaw testów w ogóle w CI nie biegnie. Cały ciężar poprawności spada na te pięć komend.

Stan wyjściowy do porównania — **zapisać przed pierwszym wydaniem**:

```bash
# 1) on-time i liczba zmian stanu na flagę, 24 h, z historii HA
#    oczekiwane: 100 / 100 / 99 / 91 / 25 %, po 6 zmian stanu, wszystkie w kształcie
#    on -> unavailable -> unknown -> on   (F1)
# 2) minima wyniku, 24 h, z sensorów argus score
#    oczekiwane: load 0.480, memory 0.830, processor 0.562, lodowka 0.492, zamrazarka 0.497 (F2)
# 3) odsetek próbek >= 0.7
#    oczekiwane: memory 100.0 %, load 80.0 %, processor 36.8 %, zamrazarka 41.5 %, lodowka 12.0 % (F3)
# 4) picker
curl -s "http://<host>/api/sensors" | jq '.entries | length'   # oczekiwane 157 (F10)
# 5) inwentarz checkpointów (baza dla 5.6)
find /data/models -type f | sort > /tmp/models.before
```

### 5.1 Po WS1 (detektor `rmad`, bezczynny)

Nic w HA nie może się zmienić — WS1 jest inertny do czasu, aż WS2 wyśle `params["detector"]="rmad"`.

```bash
docker buildx build -f argus/Dockerfile .
docker run --rm <img> python3 -c "from argus_detector.proto import argus_pb2; print('ok')"
cd detector && python -m pytest -q                  # 264 + nowe, 1 skip
grep -rn 'MinMaxScaler' detector/argus_detector/    # WYŁĄCZNIE hst_detector.py
```

**Dowód sukcesu:** żadna z pięciu flag nie zmienia zachowania, `n_seen` wszystkich pięciu encji rośnie normalnie, log nie pokazuje nowych WARN-ów poza jednorazowym ostrzeżeniem o wyborze `hst`. Jeśli cokolwiek w HA się zmieniło — WS1 nie był bezczynny i to jest regresja.

Wydanie wg recepty. (Import stubów wewnątrz obrazu jest tu jedynym prawdziwym testem naprawy `argus/Dockerfile` — `deploy/Dockerfile.detector:22-27` robi tę sekwencję od zawsze, `argus/Dockerfile` nie robił jej nigdy.)

### 5.2 Po WS2 (bramka + higiena) — główny punkt kontrolny

**Krok 0 — wyczyszczenie retained (obowiązkowe, jednorazowe, PRZED pomiarem).** Stare `retain:true` ON na temacie flagi jest redostarczane HA przy każdej subskrypcji, więc kryterium „po restarcie OFF" jest niemierzalne, dopóki ładowność nie zostanie skasowana:

```bash
for slug in sensor_load_5m sensor_memory_use_percent sensor_processor_use \
            sensor_lodowkababcia_power sensor_zamrazarkapiwnica_power; do
  mosquitto_pub -h <broker> -u <user> -P <pass> -t "argus/${slug}/flag/state" -r -n
done
```

Dopiero potem restart add-onu i pomiar. Odczekać **pełne 24 h** od restartu.

| Pomiar | Skąd | Sukces | Baza (F1/F3) |
|---|---|---|---|
| Maks. nieprzerwany czas ON, per flaga | historia HA, 24 h | **< 6 h** na każdej z pięciu | > 24 h na wszystkich pięciu |
| On-time, per flaga | historia HA, 24 h | **< 10 %** na każdej; `memory_use_percent` i `zamrazarkapiwnica_power` = **0 %** | 100 / 100 / 99 / 91 / 25 % |
| Przejścia ON→OFF | historia HA, 24 h | **Dla każdej flagi zachodzi dokładnie jedna z dwóch alternatyw:** (a) ≥ 1 para `on -> off` **nie** w kształcie restartu, albo (b) **0 epizodów ON** w oknie. Dla `memory_use_percent` i `zamrazarkapiwnica_power` obowiązuje wariant (b) — flaga, która nigdy nie zapala się, nie może mieć przejścia | 0 (wszystkie 6 zmian to artefakty restartu) |
| Starty epizodów, suma po 5 czujnikach | `GET /api/anomalies/recent` + log id 7010 | **5..15** (F13 przewiduje ~8) | 5 zawieszonych flag, 0 epizodów |
| Linie `Flag <entity> -> ...` w logu Debug | log add-onu, 15 min po ustabilizowaniu | **0** | ~4 na 15 s na encję, na Information |
| Stan flagi po restarcie add-onu | HA | **OFF**, w ciągu **≤ 75 s** dla `load_5m`/`memory`/`processor` (60 s cooldown D-07 + jeden interwał werdyktu) i **≤ jeden interwał próbkowania** dla obu czujników lodówkowych (~6.5 min zamrażarka, ~6.5 min lodówka). Mierzalne **tylko** po kroku 0 | zachowane ON |
| `AlertEventForceClosed` (7012) | log, 7 dni | **0 wystąpień** | n/d |
| Publikacje flagi w oknie `SuppressBinarySensor` | log, 60 s po rekonekcie | **0 dla odczytów z żywego `state_changed`**. Burza `get_states` nie jest objęta cooldownem (`NetDaemonHaEventSource.cs:158-159` woła `FeedStatesAsync` **przed** `MarkReconnect`) — to znana, nienaprawiona luka, patrz §7 | n/d |
| Ładowność `argus/{slug}/score/state` | podsłuch MQTT | **bajtowo identyczna** z buildem sprzed | — |
| Renderowanie encji po ubiciu add-onu | HA | `unavailable` (bridge LWT `MqttConnection.cs:172` + lista dostępności `DiscoveryPublisher.cs:53-57`), **nie** zatrzaśnięte ON | — |

**Jeśli którakolwiek flaga dalej siedzi ON > 6 h:** sprawdzić najpierw, czy `BuildEntityStates` faktycznie wybrał `rmad` — log Debug per werdykt niesie próg; wartość `0.7` zamiast `0.5` oznacza powrót regresji `ScoreStreamPipeline.cs:446-450` (dopasowanie `d.Name == "hst"` z fallbackiem `new HstParams()`).
**Jeśli `AlertEventForceClosed` odpaliło:** strukturalne zwolnienie z D-C zawiodło. **Blokada wdrożenia kolejnych workstreamów** — wrócić do sweepu cyklu pracy z WS1 i do `scale_floor`.

### 5.4 Po WS3 (domyślne, presety, migracja, read-back) — wykonywane PO 5.3 (backfill)

**Krok 0 — tożsamość encji HA (jedyny bloker upgrade'u, mierzalny).** `UniqueId.cs:13-18` wstawia nazwę detektora w `unique_id`/`object_id`, a `DiscoveryPublisher.cs:224` bierze ją z `Detectors[0].Name`. WS3 (D-G) tnie nazwę detektora z `unique_id`/`object_id` → `argus_{slug}_anomaly` / `argus_{slug}_score`, więc encje HA są przemianowywane RAZ i już nigdy przy kolejnych zmianach detektora. Stare retained discovery configi (`argus_{slug}_{det}_*`) osierociłyby się same — `RetractAsync` (`DiscoveryPublisher.cs:169-187`) biega tylko dla `removedEntities` — dlatego kasuje je jawnie `RetractLegacyDetectorScopedAsync`, raz, przed pierwszym `PublishAsync`. Bez tego temat stanu `argus/{slug}/flag/state` (bezdetektorowy, `DiscoveryPublisher.cs:49`) napędzałby dwie discovery-configi naraz.

```bash
# 1. stare retained discovery MUSZĄ zniknąć — po migracji, dla każdego sluga:
mosquitto_sub -h <broker> -u <user> -P <pass> -v -t 'homeassistant/+/argus_+_+_+/config' -W 3   # kazdy detektor, nie tylko hst
#    oczekiwane: PUSTO (retract wykonany pustą ładownością)

# 2. nowe encje istnieją, stare zniknęły z HA
#    binary_sensor.argus_sensor_load_5m_anomaly       -> istnieje (bez nazwy detektora)
#    binary_sensor.argus_sensor_load_5m_hst_anomaly   -> nie istnieje / unavailable-usunięta

# 3. LISTA DO PODMIANY — wygenerować i wkleić do argus/CHANGELOG.md (jedyny nosnik not upgrade'owych, [create] w WS3):
for s in sensor_load_5m sensor_memory_use_percent sensor_processor_use \
         sensor_lodowkababcia_power sensor_zamrazarkapiwnica_power; do
  echo "binary_sensor.argus_${s}_hst_anomaly -> binary_sensor.argus_${s}_anomaly"
  echo "sensor.argus_${s}_hst_score          -> sensor.argus_${s}_score"
done
```

**Historia HA starych encji jest tracona bezpowrotnie** — patrz §7. Każdy dashboard/automatyzacja odwołujący się do starych `entity_id` musi zostać ręcznie przepięty; żaden kod tego nie zrobi.

```bash
# migracja
grep -c 'name: rmad' /data/entities.yaml          # 5 (albo 6 z zamrażarką)
head -1 /data/entities.yaml                       # schema_version: 2
ls -l /data/entities.yaml.pre-v2.bak              # istnieje, treść == plik sprzed upgrade'u
grep -c 'Migrated .*: hst -> rmad' <log>          # jedna linia na czystą encję
grep -c 'has tuned hst params' <log>              # 0 (wszystkie pięć było nietkniętych)
grep -A5 'entity_id: sensor.load_5m' /data/entities.yaml | grep frozen   # window "10", variance "0.0" — reguła D-H

# idempotencja (trzy przypadki, nie dwa)
#   a) restart add-onu           -> brak linii migracji; stat /data/entities.yaml mtime bez zmian
#   b) zapis czujników z UI      -> j.w.
#   c) zapis GRUPY z UI, restart -> j.w.  (Program.cs:604-610 musi stemplować schema_version,
#                                          inaczej migrator przepisuje plik przy każdym boocie)

# checkpointy hst przeżywają (rollback)
ls /data/models/*/hst/checkpoint.pkl              # dalej pięć

# TRWAŁOŚĆ (MIG-4) — najważniejszy pojedynczy krok tego workstreama
cp /data/entities.yaml /tmp/entities.before
#   1. Detektory -> sensor.load_5m -> nic nie zmieniaj -> Zapisz
#   2. Ustawienia -> Zapisz wzorce (bez zmian)
diff /tmp/entities.before /data/entities.yaml     # BEZ RÓŻNIC
```

**Dowód czytelności (F6-2/F6-3):** otworzyć obok siebie edytory `sensor.memory_use_percent` i `sensor.lodowkababcia_power`. To samo `window '720'` **musi** czytać `~3 h` na pierwszym (mediana odstępu 15.3 s) i `~78 h` na drugim (391 s); tylko drugi pokazuje ostrzeżenie >48 h z rekomendacją 240 próbek. Pole `high_threshold '0.5'` **musi** na obu pokazywać `= odchylenie 5,0σ`. `sensor.zamrazarkapiwnica_power` **musi** pokazywać `Norma: 107 W · alarm poza 92–122 W` (mediana 107, MAD ≈ 2 W, σ = 1.4826·2 = 2.965, z=5), a pasmo **musi zawierać** cały jego zmierzony zakres 101–109 W. `sensor.lodowkababcia_power` **musi** pokazywać pasmo **wykluczające** 984 W. Karta `hst` w radiogrupie detektorów **musi** być oznaczona jako *legacy / niekalibrowany* — inaczej operator wróci do detektora rzadkości bez ostrzeżenia.

**Dowód wycofania WR-02 (SYNC-1):** zmienić jedną wartość w `DetectorDefaults.cs`, przebudować **tylko** stronę .NET, odświeżyć SPA — nowa wartość musi się pojawić bez żadnej edycji TypeScriptu. `grep -rn "DETECTOR_DEFAULTS" orchestrator/ui/src` nie zwraca literalnej tabeli wartości.

**Do `argus/CHANGELOG.md` (jedyny nośnik not upgrade'owych, [create] w WS3, `docs/FIX-PLAN.md:288`; `argus/DOCS.md` zostaje wyłącznie stałą dokumentacją operatorską):** lista podmiany encji z kroku 0, ścieżka odzyskania po downgrade (`cp /data/entities.yaml.pre-v2.bak /data/entities.yaml`) i informacja, że modele `rmad` startują puste, ale są primowane z Recordera przy pierwszym otwarciu strumienia po migracji (WS5 wydany wcześniej, 2.1.14) — kontrola: `grep 'Primed .* -> n_seen=' <log>` daje `n_seen ≥ 720` na obu lodówkowych. Czasy do pierwszego werdyktu z żywego ruchu (~17 min `load_5m`, ~13 min `memory`, ~1 h `processor`, ~6.5 h oba lodówkowe) obowiązują wyłącznie na ścieżce awaryjnej, gdy backfill jest niedostępny. Weryfikacja nośnika: `ls argus/CHANGELOG.md` istnieje, zawiera listę podmiany z kroku 0, notę o zmianie znaczenia `argus/{slug}/score/state` (D-E) i ścieżkę rollbacku; `grep -c 'entities.yaml.pre-v2.bak' argus/DOCS.md` = 0.

### 5.5 Po WS4 (rejestr czujników: duch + brakujące encje)

```bash
# D1 NAJPIERW — wykluczyć brak błędu
curl -s "http://<host>/api/sensors" | jq '.entries | length'   # BEZ ?q ; jeśli >> 157, stop, nie było błędu

# D2 — dowód rozstrzygający, jeden artefakt
curl -s "http://<host>/api/sensors/diagnostics" | jq
#   rawStateCount >= 400 + numericCount ~157 + literały zdominowane przez unavailable/unknown  => H1
#   rawStateCount ~157                                                                          => D4
#   rawStateCount == 0 + WARN "HA WebSocket message exceeded 4194304 bytes"                     => H5

# D5 — potwierdzenie H1
watch -n60 'curl -s http://<host>/api/sensors/diagnostics | jq .snapshot.lateAddedCount'
```

| Pomiar | Sukces (gałąź H1) | Baza |
|---|---|---|
| `GET /api/sensors` (bez `q`) | **≥ 380** w ciągu 24 h | 157 z 403 (F10) |
| `sensor.zamrazarkapiwnica_power`, `sensor.expminimp` | obie w ładowności | brak (F9/F10) |
| Encje `number.*` | **≥ 25 z 32** | 0 |
| `lateAddedCount` | **ściśle rosnące** w pierwszej godzinie, ≥ 200 w ciągu 24 h | n/d |
| Dowód tego samego połączenia | `sensor.load_5m` upsertowany **≤ 5 s** od odczytu | n/d |
| `diagnostics.missing ⊆ payload entityIds`; `payload.count ≥ trackedCount` | prawda (union jest **bezwarunkowy**, nie filtrowany przez `q`) | fałsz |
| Odznaka „Brak w HA" | widoczna na duchu, link Edit działa, Untrack+Zapisz usuwa go z YAML | brak (encja niewidoczna) |
| Zapis przy pustym rejestrze | bajtowy diff `/data/entities.yaml` **bez różnic** | encja kasowana po cichu |

**Test end-to-end `number.*` (bez niego „≥ 25 z 32 w pickerze" nic nie dowodzi):** wybrać jedną encję `number.*` (np. `number.<x>_termostat_occupied_heating_setpoint_scheduled`), zaznaczyć ją, Zapisz, a następnie potwierdzić w HA, że powstały **oba** `binary_sensor.argus_number_<slug>_anomaly` i `sensor.argus_number_<slug>_score` (D-G: id bez nazwy detektora), i że score sensor publikuje wartość. To weryfikuje slug, `GlobExpander` i discovery dla domeny innej niż `sensor.` — trzy rzeczy, których nikt wcześniej nie przeszedł.

**Gałąź falsyfikacji (H1 obalone) jest zdefiniowana, nie otwarta:** jeśli `lateAddedCount` zostaje **< 10 przez godzinę**, gdy HA raportuje 403, wykonać **D4 z twardym budżetem 30 minut**: z wnętrza kontenera add-onu dwa razy `get_states` przez `ws://supervisor/core/websocket` — raz z `$SUPERVISOR_TOKEN`, raz z długożyciowym tokenem admina — i policzyć wiersze w każdej odpowiedzi. Warunek stopu: obie liczby zapisane. Jeśli luka się utrzymuje, **WS4 jest ukończony z wynikiem „diagnoza + zapis"**, kryterium „≥ 380" zostaje formalnie unieważnione, a przyczyna (widoczność encji po stronie HA dla tożsamości proxy Supervisora) trafia do §7 i do `argus/DOCS.md` (stała dokumentacja operatorska; noty upgrade'owe idą wyłącznie do `argus/CHANGELOG.md`). Poprawki #2 (union payload) i #3 (`GlobExpander`) są niezależne od H1 i shipują tak czy inaczej.

### 5.3 Po WS5 (backfill z HA Recordera) — wykonywane PRZED 5.4 (migracja)

**BLOKUJĄCA SONDA — przed wydaniem, nie po.** Z wnętrza kontenera add-onu wykonać dokładną ładowność `history/history_during_period` z `SUPERVISOR_TOKEN` przeciw `ws://supervisor/core/websocket` dla `sensor.lodowkababcia_power` na oknie 24 h. Zanotować: **(a)** `success == true`; **(b)** `result` kluczowany po `entity_id`; **(c)** czy wiersze niosą `s`/`lu` czy `state`/`last_updated`; **(d)** rozmiar odpowiedzi w bajtach; **(e)** **przypadek negatywny** — jeśli `success == true`, ale `result` jest puste dla czujnika, o którym F12 mówi, że ma 1546 wierszy, to **uprawnienia po stronie HA, nie błąd Argusa**: zatrzymać, zapisać, nie shipować. Jeśli **(d) > 1 MB**, obniżyć domyślne `ARGUS_BACKFILL_SLICE_HOURS` przed shipem (twardy cap ramki to 4 MB, `HaWebSocketClient.cs:246`, i jest fatalny dla całego połączenia).

```bash
grep 'HistoryFetched' <log>     # jedna linia na encję w ciągu 60 s od startu
```

| Pomiar | Sukces | Baza |
|---|---|---|
| Linie `HistoryFetched` (5020) | 1 na śledzoną encję, ≤ 60 s od startu | **0 w całej historii wdrożenia** (F11) |
| `lodowkababcia_power`: wiersze / `SpanHours` | ≥ 1400 wierszy, `SpanHours ≥ 156` | n/d (F12: 1546 wierszy przez 7 d) |
| `zamrazarkapiwnica_power` | ≥ 1400 wierszy, `SpanHours ≥ 156` (225 pkt/d × 7 d = 1575) | n/d |
| `Commands` per encja | **≤ 10** przy lookbacku 30 d wobec 7-dniowego Recordera; **== 1** dla `load_5m` (5082 wierszy/24 h > cap 5000) | n/d |
| `Verdict.n_seen` przy pierwszym werdykcie po `rm -rf /data/models` | **≥ 720** na obu czujnikach lodówkowych | 707 / narastane z żywego ruchu |
| `HA WebSocket connection lost — backing off` | **0** przez cały przebieg zasilania | n/d |
| `Sensor registry updated: {Count}` | **dokładnie jedna** na połączenie, `Count` niezmieniony wobec bazy z §5.0 (157 — rejestr z §5.5 jeszcze nie wylądował) | j.w. |
| `HA WebSocket message exceeded 4194304 bytes` | **0** | n/d |
| `WarmupSkipped` (5018) po restarcie z checkpointami | na każdą wcześniej zasiloną encję | n/d |
| Degradacja: `ARGUS_HA_URL` na nieosiągalny host | add-on startuje, strumienie się otwierają, po jednym `WarmupFailed` (5019) na encję | n/d |
| Zapis konfiguracji z UI (przebudowa pipeline'u) | **0 nowych linii `HistoryFetched`** dla encji zasilonych w tym procesie; `WarmupFailed` **nie** pojawia się przy anulowaniu (cancel nie jest porażką) | n/d |

**Regresja krytyczna:** `sensor.disk_use_percent` (dziś 135/250, wygaszony bo niedogrzany) po zasilonej rozgrzewce **nie może** zatrzasnąć się na ON. To był bloker kolejności — dlatego WS5 idzie **po** WS2 i **przed** WS3, nigdy przed WS2. Jeśli się zatrzasnął, WS2 nie zadziałał, WS5 trzeba wycofać, a migracja z §5.4 czeka.

### 5.6 Po WS6 (symulator odtwarzania w SPA)

**Krok 0 — panel faktycznie dotarł do obrazu (C4).** `wwwroot` jest artefaktem builda (`vite.config.ts:11-12`, `emptyOutDir:true`, `.gitignore:40-42`), więc edycja `ui/public/css/argus.css` bez przebudowy SPA nic nie zmienia w kontenerze:

```bash
docker run --rm <img> sh -c 'grep -rl "argus-replay" /opt/argus/orchestrator/wwwroot/assets | head -3'
# musi coś zwrócić; pusto == panel nie jest w obrazie
```

Następnie **przez Ingress w HA** (nie przez localhost): Detektory → `sensor.load_5m` → przycisk **`Testuj na historii`** renderuje się i po kliknięciu rysuje wykres. Renderowanie tylko w dev-compose nie liczy się.

**Odtworzenie F2 (kryterium poprawione — bez liczby procentowej).** Przełączyć `sensor.load_5m` na `hst` z legacy paramami (`window 250, n_trees 25, high 0.7, low 0.3, min_consecutive 3`), `lookback=24h`, `maxPoints=5000`, kliknąć `Testuj na historii`:

- krzywa wyniku **nigdy nie wchodzi** w narysowaną martwą strefę 0.3–0.7,
- **0 przejść ON→OFF w regionie scorowalnym** (poza rozgrzewką) — to jest cała teza F2 i jedyna rzecz, którą zimne odtworzenie może uczciwie udowodnić,
- `Episodes == 1`.

**Uczciwe zastrzeżenie, które musi zostać w copy panelu:** zimne odtworzenie `hst` startuje ze świeżym, nieograniczonym `MinMaxScaler` (`hst_detector.py:50`), podczas gdy zmierzona podłoga 0.480 jest własnością żywego modelu z 16061 obserwacjami, który wchłonął wyskok 13.01 (F5). Weryfikowane twierdzenie brzmi *„raz zapalona flaga nigdy nie zwalnia"*, **nie** *„minimum wynosi 0.480"* i **nie** *„on-time ≥ 99 %"*.

**Odtworzenie F13 (okna uzgodnione).** F13 mierzył 24 h; okno 8 d / 2000 punktów jest z nim **nieporównywalne** (przy 2000 punktach `load_5m` pokrywa ~9.6 h, a `zamrazarka` pełne 7 d). Dlatego przebieg porównawczy uruchamiamy **z jawnym `lookback=24h` i `maxPoints=5000` per czujnik**, a porównujemy **wyłącznie `alertsPerDay`** — jedyną wielkość niezależną od długości okna:

| Czujnik | `alertsPerDay` przy `lookback=24h` | Cel F13 (MAD-on-raw, 24 h) | Dziś (F1/F3) |
|---|---|---|---|
| `load_5m` | ≤ 6 | 4 | 1 epizod / 100 %, precyzja 4.3 % |
| `memory_use_percent` | 0–2 | 0 | 1 / 100 %, precyzja 1.2 % |
| `processor_use` | 2–3 | 2 | 1 / 99 %, precyzja 2.9 % |
| `lodowkababcia_power` | **≥ 2** | 2 | 1 / 91 %, precyzja 83 % |
| `zamrazarkapiwnica_power` | **0** | 0 | 1 / 25 %, precyzja 0.0 % |

Drugi przebieg z `lookback=8d`, `maxPoints=5000` — wyłącznie dla grubości statystycznej na wolnych czujnikach (lodówka ~1546 pkt, zamrażarka ~1575 pkt przez 7 d); **raportować z niego również tylko `alertsPerDay`**, nigdy on-time obok liczb 24-godzinnych. Panel eksponuje `spanHours` przy każdym przebiegu i to jest liczba, która czyni oba przebiegi rozróżnialnymi.

**Nieperturbacja F14/F15 — z wykonywalną receptą (B6) i mierzalnym kryterium (B3).** `IsAuthorizedRequest` przepuszcza loopback (`Program.cs:264-278`), więc pętlę uruchamiamy **z wnętrza kontenera add-onu**:

```bash
PORT=8099            # bind na sztywno, Program.cs:216-219; ss/iproute2 nie ma w obrazie add-onu
ENT=sensor.load_5m
BODY='{"detector":"rmad","lookback":"24h","maxPoints":2000,
       "params":{"window":"720","min_samples":"60","z_scale":"5.0",
       "scale_floor":"0.0","high_threshold":"0.5","low_threshold":"0.375","min_consecutive":"3",
       "frozen_window":"10","frozen_variance_threshold":"0.0"}}'

find /data/models -type f | sort > /tmp/models.pre
READ_PRE=$(curl -s "http://127.0.0.1:$PORT/api/sensors" \
           | jq '.entries[] | select(.entityId=="sensor.load_5m") | .readingCount')
date -u +%s > /tmp/t0

for i in $(seq 200); do
  curl -s -X POST "http://127.0.0.1:$PORT/api/sensors/$ENT/simulate" \
       -H 'Content-Type: application/json' -d "$BODY" | jq -r '.ok'
done | sort | uniq -c        # oczekiwane: 200 true, 0 false (pętla sekwencyjna nie trafia na Gate.Wait(0))

find /data/models -type f | sort > /tmp/models.post
diff /tmp/models.pre /tmp/models.post         # BEZ RÓŻNIC (żaden nowy plik, żaden nowy katalog)
```

Kryterium nieperturbacji, poprawione:

- **zbiór ścieżek** pod `/data/models` identyczny przed i po (`diff` pusty) — wyłącznie wcześniej istniejące `<slug>/hst/` i `<slug>/rmad/`;
- **mtime WOLNO się zmienić** — `ARGUS_CHECKPOINT_INTERVAL_SEC=300` gwarantuje zapis z żywego ruchu w trakcie 200 odtworzeń; „zero zmienionych mtime" było kryterium niezaliczalnym z definicji i zostaje usunięte;
- zamiast tego: `readingCount` po pętli minus `READ_PRE` **równa się liczbie żywych werdyktów w tym samym oknie** — policzonej z logu, z tolerancją ±2:

```bash
T0=$(cat /tmp/t0)
grep 'sensor.load_5m' <log> | grep -c 'latency_ms='   # werdykty od T0; delta readingCount == ta liczba ±2
```

**Latencja werdyktu pod obciążeniem symulatora (A15-pod-obciążeniem — to NIE jest B8).** Mierzy dokładnie tę samą wielkość co A15 (pole `latency_ms`, linia Debug per werdykt `ScoreStreamPipeline.cs:242-244`), ale w oknie = pełny czas trwania pętli 200 odtworzeń, i służy wyłącznie za dowód nieperturbacji z §7 #16: symulator nie może pogorszyć latencji żywego scoringu. Czas odpowiedzi samego symulatora to osobny pomiar na osobnym nośniku — `durationMs` z `SimulateCompleted` (7013), kryterium B8 w „Kryteriach akceptacji" WS6. Poziom logu add-onu musi być `Debug` (jest — `logLevel=Debug`).

```bash
grep -o 'latency_ms=[0-9.]*' <log> | cut -d= -f2 | sort -g | awk '
  {a[NR]=$1}
  END{printf "n=%d p50=%.1f p95=%.1f max=%.1f\n",
      NR, a[int(NR*0.50)+0], a[int(NR*0.95)+0], a[NR]}'
```

Kryterium: **p95 < 1000 ms** i **max < 3000 ms**, dla **każdej** encji strumieniowej z osobna (dodać `grep '<entity_id>'` przed `grep -o`) — te same progi co A15 w WS2, więc porównanie okno-do-okna jest wprost testem regresji; dopuszczalne pogorszenie p95 względem przebiegu A15 bez symulatora: **≤ 1.5×**. Dodatkowo: **0 wystąpień `INTERNAL` w logu** — żaden `ScoreStream` nie może zostać przerwany. Semafor dwuslotowy po stronie detektora i deadline 10 s po stronie adaptera są tym, co czyni to sprawdzalnym.

**Wydajność end-to-end:** odtworzenie 2000-punktowe `rmad` (klik → wyrenderowany wykres) **< 3 s p95** na hoście add-onu — przeliczone z 38.6 µs/pkt zmierzonych na x86 i projekcji 150–300 µs/pkt na arm64. `maxPoints` klamrowane do [100, 5000]; historia 20001-punktowa odrzucona `INVALID_ARGUMENT`; drugi **równoległy** `POST /api/sensors/{entityId}/simulate` zwraca `{ok:false,kind:'busy'}` w < 50 ms i **nie kolejkuje**.

**Degradacja:** przy nieskonfigurowanym źródle historii panel renderuje jedną czytelną linię, zwraca **HTTP 200** z `{ok:false,kind:'unavailable'}`, **nigdy 500**, nigdy pustego wykresu, i nigdy nie blokuje zapisu na ekranie edytora.

**Higiena stanu panelu:** przejście `#/detectors/sensor/A` → `#/detectors/sensor/B` **musi** wyczyścić wykres — jeśli po zmianie encji widać wynik poprzedniej, `replayState`/`replayEnabled` nie są resetowane po `[entityId]` i to jest błąd blokujący, bo operator stroi B patrząc na dane A.

### 5.7 Kryterium końcowe (7 kolejnych dni)

- żadna flaga nie jest ON dłużej niż 6 h nieprzerwanie;
- **5–15 startów epizodów na dobę łącznie**, z czego **≥ 2 dziennie na `sensor.lodowkababcia_power`**;
- `sensor.zamrazarkapiwnica_power` i `sensor.memory_use_percent` na **0 %** on-time;
- **0** wystąpień `AlertEventForceClosed` (7012);
- `GET /api/sensors` ≥ 380 encji (albo zapisany wynik D4, jeśli H1 obalone);
- zapis z dowolnego ekranu nie zmienia `/data/entities.yaml` poza tym, co operator faktycznie edytował.

**Próg odbioru dla F3 — jawny, nie pominięty.** Po 7 dniach odtworzyć pomiar precyzji z F3 na nowych danych, z **twardymi progami tylko tam, gdzie metryka jest niezależna od detektora**:

| Czujnik | Próg odbioru | Uzasadnienie |
|---|---|---|
| `sensor.lodowkababcia_power` | **precyzja ≥ 70 %** epizodów, gdzie „prawdziwy" = potwierdzony cykl sprężarki 0 W / 984 W skonfrontowany z **faktycznym zachowaniem urządzenia**, nie tylko z regułą z | dziś 83 % (F3); to jedyny czujnik z prawdziwymi, etykietowalnymi zdarzeniami i jedyny, którego **nie wolno stracić** |
| `sensor.zamrazarkapiwnica_power` | **0 alarmów w 7 dni** | F3: seria 101–109 W, sd 1.87 W, **zero** prawdziwych odstających w 24 h; dziś 41.5 % próbek ≥ 0.7 przy precyzji 0.0 %. Każdy alarm = porażka |
| `load_5m`, `memory_use_percent`, `processor_use` | **brak progu precyzji — świadomie** | F3 definiuje odstającego jako robust `z = \|x−median\|/(1.4826·MAD) > 3.5`, a `rmad` liczy **tę samą statystykę** na oknie kroczącym; mierzenie tego detektora tą metryką jest **samopotwierdzające**. Progiem odbioru dla tych trzech jest **częstość alarmów** z §5.6 i on-time < 10 % z §5.2, nie liczba precyzji |

Uczciwie falsyfikowalne twierdzenia całego przedsięwzięcia to: **częstość alarmów**, **odwrócenie porządku F4** (101 W przestaje scorować wyżej niż 107 W) i **zachowanie dwóch prawdziwych zdarzeń lodówki**. Nie liczba precyzji na czujnikach systemowych.

---

## 6. Czego celowo nie robimy

**F4 (HST rankuje rzadkość, nie odchylenie) — odroczone, nie naprawione.** `river.anomaly.HalfSpaceTrees.score_one` zwraca `1 − mass/max_mass`; na jednym skwantowanym skalarze rzadkie-ale-normalne 101 W musi scorować wyżej niż modalne 107 W. Usuwamy to ze **ścieżki domyślnej** (`rmad` staje się domyślnym detektorem), zostawiając `hst` jako opt-in i darmową ścieżkę rollbacku. **Odrzucamy Calibrated-HST** (Proposal 1): jego własny prototyp raportuje, że inwersja F4 **przeżywa** naprawę (101 W → 0.974 vs 107 W → 0.538, praktycznie liczby z F4), a jego gałąź kwantylowa zostawia `memory_use_percent` na 37 epizodach / 35.5 % on-time — czyli nośnym dyskryminatorem na 4 z 5 czujników jest jego strażnik odchylenia, a nie HST. Jeśli decyduje statystyka MAD, to ona ma być detektorem. **Mitygacja, nie naprawa:** karta `hst` w `DetectorEntry.tsx` jest oznaczona jako *legacy / niekalibrowany — wymaga ręcznego strojenia progów*, żeby nikt nie wrócił tam nieświadomie.

**F5 (nieograniczony `MinMaxScaler`) — odroczone, nie naprawione.** `hst_detector.py:83-84` woła `learn_one` przed `transform_one`, więc river'owe `stats.Min`/`stats.Max` bez zaniku permanentnie kompresują pasmo normalne po jednym wyskoku (0.54 → 0.0032 po 13.01). `hst_detector.py` dostaje **wyłącznie docstring** z tym pomiarem. Bez `limits={'value':(0,1)}`, bez ograniczonego skalera robust, bez chronionego okna kalibracji, bez publikowania ECDF. Świadoma konsekwencja: **udokumentowana ścieżka rollbacku prowadzi do detektora, o którym wiemy, że jest zepsuty** — to cena za darmowy rollback i jest wpisana w `argus/CHANGELOG.md` (nośnik not upgrade'owych i rollbacku, [create] w WS3).

**F7 (brak fazy kalibracji) — świadomie nierozwiązane w formie, w jakiej postawiono pytanie.** Nie dodajemy stanu `CALIBRATING`, `cal_min`, `cal_window`, ani `calibrated` na `Verdict`. Twierdzenie, które przyjmujemy zamiast tego, jest jawne i testowalne: **krocząca mediana/MAD JEST kalibracją per-encja**, przeliczaną co tick nad pamięcią 720 próbek, więc osobna faza jest zbędna. Kto uważa, że F7 wymaga oddzielnego, obserwowalnego etapu z własnym licznikiem — nie dostaje tego. Odrzucamy `cal_min=240` z Proposal 3 również dlatego, że kosztowałby ~26 h ślepego okna na obu czujnikach lodówkowych po **każdym** restarcie procesu.

**Nie budujemy Event Layer** (Proposal 3): bez kanału rank, bez robust-z na surowych wartościach po stronie .NET, bez `min_duration_sec`, `refractory_sec`, `max_events_per_hour`, `evidence_mode`, `alert_mode: legacy`, bez `AlertStateStore`. Kanał rank jest zbędny (F13 mierzy rank-on-score na 0 epizodach dla 4 z 5 czujników i **kasuje lodówkę** — jedyny czujnik z prawdziwą precyzją), druga kopia MAD w orkiestratorze łamie D2.

**Nie repurposujemy `high_threshold`/`low_threshold` na kwantyle** (Proposal 1). Te same nazwy kluczy, te same typy, te same zakresy walidacji `(0,1]`/`[0,1)`, nowe znaczenie, **żaden błąd nigdzie niemożliwy** — najgroźniejsza dostępna zmiana konfiguracji, i zbędna, gdy sam wynik jest bezwymiarowy.

**Nie dodajemy bloku `alert:` w `entities.yaml`** ani pola DTO w `SaveRequest`. Byłaby to ósma nieegzekwowana powierzchnia lustrzana, a podmiana całej listy w `save()` (`sensors.ts:161-169`) porzuciłaby ją przy pierwszym Zapisz — po cichu.

**Nie robimy wariantu sezonowego** (`rmad_seasonal`, bazy per-kubełek, `tz_offset_minutes`, konsumpcja `Point.timestamp`). Żaden z pięciu zmierzonych czujników tego nie uzasadnia: metryki systemmonitor mają co najwyżej słabą strukturę dobową, a **oba** czujniki mocy lodówkowej są sterowane cyklem pracy sprężarki, nie zegarem. Wymagałby ≥5 dni historii przy 7-dniowej retencji Recordera — spekulatywna abstrakcja (Rule 2).

**Nie utrwalamy stanu bramki ani kalibracji na dysku po stronie .NET.** D-11 (`EntityRuntimeState.cs:17-22`) obowiązuje. Cały drogi wyuczony stan (okno 720 próbek) żyje w checkpoincie Pythona i **dlatego** zapis konfiguracji nie kosztuje już godzin.

**Nie zmieniamy `HysteresisGate.cs` ani `FrozenSensorDetector.cs` kodowo.** Bramka jest poprawna, gdy wynik jest z-kalibrowany; wszystkie 10 testów `HysteresisGateTests.cs` musi zostać zielone bajtowo. Jeśli którykolwiek wymaga edycji — projekt złamał przypięty niezmiennik.

**Nie naprawiamy syntetycznego `HaReading`** (`ScoreStreamPipeline.cs:161`, `Value 0.0` + nieaktualny `SuppressBinarySensor`). Żaden kanał tej wartości nie potrzebuje, a kolejka parowania read/write byłaby najbardziej prawdopodobnym miejscem na zakleszczenie wobec przypiętej kolejności `CompleteAsync`-przed-`await readTask` (`:191-193`, `ScoreStreamPipelineTests.cs:445`).

**Nie naprawiamy luki kolejności cooldownu** — `NetDaemonHaEventSource.cs:158-159` woła `FeedStatesAsync` **przed** `MarkReconnect`, więc burza `get_states` nie jest objęta cooldownem, dla którego cooldown powstał.

**Nie ponawiamy okresowo `get_states`** i **nie dodajemy routera wiadomości na żywym WebSockecie HA**. Musiałoby to biec po `subscribe` na tym samym sockecie, gdzie każda przeplatana ramka jest cicho odrzucana (`HaWebSocketClient.cs:72-78`, `:221-224`).

**Nie zmieniamy nazwy `IInfluxDataSource`** mimo że jeden z dwóch implementatorów nie ma nic wspólnego z InfluxDB (Rule 3). Mylącą nazwę dokumentujemy w docstringu.

**Nie naprawiamy walidacji członków grup** (`GroupInputValidator.cs:52`) ani `MemberPicker`. Ta sama klasa błędu ducha istnieje dla grup; wszystkie grupy są dziś „Oczekuje", więc jest ściśle mniej dotkliwa.

**Nie usuwamy własnych encji `sensor.argus_*` z pickera.** Sonda 403 operatora je wykluczała, Argus nie — po naprawie Argus może zgłaszać **więcej** niż 403 i to jest oczekiwane.

**Nie naprawiamy podmiany całej listy w `save()`.** WS3 naprawia **read-back**, więc zapis utrwala prawdziwe wartości zamiast domyślnych — ale `save()` dalej przepisuje każdy śledzony czujnik.

**Nie dodajemy żadnej biblioteki wykresów, ikon ani routingu, żadnej nowej zależności runtime npm, .NET ani Pythona, i jawnie nie dodajemy `Microsoft.AspNetCore.Mvc.Testing`** — testowalność endpointów rozwiązujemy wyciągnięciem projekcji do statycznych metod w `Web/`.

**Nie dodajemy żadnej opcji add-onu.** `argus/config.yaml` (poza `version:`), oba pliki tłumaczeń i `tests/test_config_schema.py` `EXPECTED_SCHEMA` zostają nietknięte — schema deklaruje `entities: [str]` i **strukturalnie nie może** wyrazić parametrów detektora.

**Nie usuwamy `hst`, jego params, karty UI, walidacji, tabeli domyślnych ani checkpointów** i nie czyścimy automatycznie `/data/models/*/hst/`.

**Nie przeszukujemy parametrów, nie auto-stroimy, nie rekomendujemy ustawień.** Symulator pokazuje konsekwencję liczb operatora; nigdy ich nie wybiera. Bez historii przebiegów, bez porównania A/B, bez eksportu, bez odtwarzania grupowego, bez wsparcia `mad`/`stl`.

**Nie naprawiamy `ScoreBatch` dla detektorów strumieniowych** (`servicer.py:183-186`) — `SimulateBatch` go obchodzi, bo naprawa oznaczałaby batchowe API mutujące żywy model.

**Nie dodajemy tabeli klas czujników** kluczowanej po `unit_of_measurement`. Nic w tej bazie kodu nie przewiduje a priori kadencji czujnika (zmierzone: 15.3 s – 391 s na zwykłych `%` i `W`) — świadomość klasy dostarczamy jako **pomiar**, nie zgadywanie.

**Nie naprawiamy CI.** Stwierdzone, nienaprawione — patrz §7.

---

## 7. Nierozwiązane blokery

Wypisane jawnie, nie schowane w ryzykach. Każdy z nich shipuje **z** planem.

1. **Zmiana identyfikatorów encji HA jest jednokierunkowa i nieodwracalna dla historii.** `UniqueId.cs:13-18` wstawia dziś nazwę detektora w `unique_id`/`object_id`; D-G tnie ją do `argus_{slug}_anomaly`, więc encje HA zmieniają `entity_id` RAZ (i nigdy więcej — także przy przyszłych zmianach detektora). Jedyne złagodzenie: ręczny rename `entity_id` starej encji w HA PRZED startem 2.2.0 (przenosi historię Recordera), opisany w `argus/CHANGELOG.md`. Retract starych retained discovery (§5.4 krok 0) usuwa duplikaty, ale **24 h zmierzonej historii, dashboardy i automatyzacje wskazujące stare `entity_id` są tracone i muszą być przepięte ręcznie**. Żaden kod tego nie zmigruje. Konsekwencja metodologiczna: porównanie „ta sama encja przed/po" wobec bazy F1/F3 jest niewykonalne — bazę trzeba porównywać po **sluggu czujnika**, nie po `entity_id` encji Argusa.

2. **Wyścig `registry.py:201` (deepcopy pod zamkiem) vs `registry.py:106` (`score_one` poza wszystkimi zamkami) nie jest naprawiony.** `rmad` mutuje **dwa** kontenery (deque + posortowana lista) zamiast jednego modelu river, więc rozdarty snapshot jest strukturalnie możliwy. `__setstate__` samonaprawia (odbudowa `_sorted` z deque), a deepcopy spada z 56–96 ms do 0.27 ms (~250× mniejsza ekspozycja), ale sam wyścig zostaje i **nie jest przetestowany nigdzie w repo**.

3. **Klif cyklu pracy `z = 1/p` przy `p ≥ 0.2` ucisza `sensor.lodowkababcia_power` bez ostrzeżenia.** Jedyny czujnik z 83 % precyzją (sprężarka ON ~12 % czasu) przeżywa z ~8 punktami procentowymi marginesu. Przypięty sweepem cyklu pracy w testach, złagodzony `scale_floor`, **nierozwiązany**. Wzrost obciążenia lodówki powyżej 20 % duty cycle = cicha utrata jedynego działającego alarmu.

4. **Kształt `history/history_during_period` jest niezweryfikowany wobec żywego HA** — grep po całym repo (`.cs`/`.py`/`.ts`) nic nie zwraca. Blokująca sonda z §5.3 jest jedyną drogą do zamknięcia — i musi zamknąć się PRZED migracją (§5.4), bo backfill ships wcześniej.

5. **Tożsamość proxy Supervisora może nie mieć prawa czytać historii ani widzieć wszystkich encji (H3).** Argus uwierzytelnia się `SUPERVISOR_TOKEN` przez `ws://supervisor/core/websocket` (`10-config-gen.sh:37-38`), nie tokenem admina operatora. Jeśli D4 (§5.5) i sonda (e) (§5.3) pokażą lukę per-tożsamość, **F10 i F11 nie są naprawialne po stronie Argusa** — to konfiguracja HA. WS4 ships wtedy z wynikiem „diagnoza + zapis"; kryterium „≥ 380 encji" jest formalnie unieważnione.

6. **Blokada czoła kolejki przy wolnym HA.** Wspólne zadanie rozgałęziające (`ScoreStreamPipeline.cs:119-125`) używa `WriteAsync` na kanale `BoundedChannelFullMode.Wait` (`:112`) — nic nie jest **odrzucane**, blokuje się dostarczanie odczytów dla **wszystkich** encji. WS5 wydłuża okno tej blokady o serializowane (SemaphoreSlim) zasilanie. Ograniczone 30-sekundowym `CancelAfter` per encja, czyli **do ~3 minut opóźnienia startu przy sześciu encjach**.

7. **`retain:true` na temacie flagi.** Zatrzaśnięte ON może przeżyć awarię add-onu. Łagodzone bridge LWT (`MqttConnection.cs:172`) + listą dostępności w retained discovery (`DiscoveryPublisher.cs:53-57`), które powinny renderować encję jako `unavailable` — **musi być zweryfikowane na żywej instancji, nie założone** (§5.2, ostatni wiersz tabeli). To zmiana sposobu renderowania tych encji w HA.

8. **Wyłączenie frozen (D-H, `frozen_variance_threshold: 0.0` przy `frozen_window` verbatim) to realna utrata pokrycia** dla czujnika zamarłego na **niezerowej** stałej wartości: `rmad` zwraca wtedy 0.0 (szczebel 4 drabiny skali) i Argus milczy. Pokrycie zastępcze (mechanizmy HA + odznaka „Brak w HA") **nie jest pełnym zamiennikiem**.

9. **Głębsza wada `FrozenSensorDetector` zostaje nienaprawiona** (uśpiona przez `frozen_variance_threshold: 0.0`, wraca przy każdym ręcznym podniesieniu progu) — bezwzględna wariancja `0.001` na surowych jednostkach, liczona po 10 **zdarzeniach**, nie po czasie, przy czym zdarzenia zmieniające tylko atrybuty też się liczą. To **ostatnia bezwzględna stała w systemie**, czyli dokładnie ta klasa błędu, którą ten plan usuwa wszędzie indziej.

10. **Downgrade obrazu po migracji nie jest bezpieczny.** Starszy build odrzuci `rmad` w `InputValidator.KnownDetectors` przy pierwszym zapisie i zbuduje `EntityRuntimeState` z `new HstParams()` (250 / 0.7 / 0.3, czyli dokładnie stan F0). Jedynym odzyskaniem jest `cp /data/entities.yaml.pre-v2.bak /data/entities.yaml`. Kod tego nie zapobiegnie przy rollbacku Supervisora — **musi trafić do `argus/CHANGELOG.md`** (tworzony w WS3, `docs/FIX-PLAN.md:288`, razem z bumpem `argus/config.yaml:3` na `2.2.0`; `argus/DOCS.md` zostaje stałą dokumentacją operatorską).

11. **Checkpointy `rmad` są bramkowane `river_version` mimo że `rmad` nie używa river** (`model_store.py:305`). Bump wersji river odrzuci je i kosztuje jedną re-rozgrzewkę (17 min – 6.5 h per czujnik). Świadoma nadostrożność — naprawa oznaczałaby dotknięcie najbardziej ryzykownego pliku w pakiecie bez zysku poprawnościowego. Właściwy strażnik (`_schema` w `__setstate__`) jest po stronie `RmadDetector`.

12. **Limit 4 MB odbioru gRPC nie jest nigdzie skonfigurowany** (`grep max_receive_message_length` nad `detector/` i `orchestrator/` nie zwraca nic). Przy `BackfillRowCap=5000` i ~57 B/`Point` jesteśmy na ~285 kB, czyli bezpiecznie — ale `ARGUS_BACKFILL_ROW_CAP` podniesiony przez operatora powyżej ~70 000 daje `RESOURCE_EXHAUSTED`, nie klamr. Bramkowane klamrem `Math.Clamp(brc, 1, 20000)`; **sam limit pozostaje nieskonfigurowany i nieudokumentowany**.

13. **Każde odtworzenie w symulatorze po WS5 otwiera nowe połączenie WS do HA** (nie reużywamy żywego połączenia — sekwencyjny socket, brak routera, `HaWebSocketClient.cs:35-37`). Przy edycji parametrów co 400 ms to burza connect+auth do Core. Debounce 400 ms i `Gate.Wait(0)` ograniczają to do jednego połączenia na przebieg, ale **cache historii per `(entityId, lookback)` nie istnieje** — 200 odtworzeń z §5.6 to 200 uwierzytelnień.

14. **Okno `rmad` jest mierzone w próbkach, nie w czasie.** 720 próbek to ~3.4 h dla `load_5m` (~5000 próbek/24 h) i ~78 h dla `zamrazarkapiwnica_power` (~225/24 h). Dwa czujniki na identycznych parametrach mają skrajnie różną pamięć zegarową. `river.utils.TimeRolling` istnieje, ale skomplikowałby eksmisję, pickle i kontrakt `n_seen`/`window`, który orkiestrator już czyta. **Świadomie odroczone**; łagodzone wyłącznie odczytem zegarowym w UI.

15. **On-time ważone czasem jest tak dobre, jak timestampy.** HA Recorder zapisuje zdarzenia `state_changed`, więc czujnik, który przestaje raportować, produkuje jedną ogromną przerwę przypisaną w całości temu, czym flaga była w tamtym momencie. 6-godzinna awaria przy fladze ON zaraportuje ~25 % on-time dla doby z jednym krótkim prawdziwym epizodem. Klamr (cap pojedynczego `dwell` na 10× mediany przerwy) **świadomie niezaimplementowany** — uczyniłby liczby symulatora nieporównywalnymi z F1, które zawierają dokładnie ten sam artefakt.

16. **`SIMULATE` to dźwignia CPU sąsiadująca z brakiem uwierzytelnienia.** `IsAuthorizedRequest` sprawdza wyłącznie peera TCP (IP Supervisora albo loopback, `Program.cs:264-278`), a `ARGUS_DEV_TRUST_ALL_REQUESTS` omija nawet to w dev-compose. `Gate.Wait(0)`, klamr 5000 punktów, `abort` przy 20000 i deadline 10 s to **cała** obrona.

17. **Podmiana całej listy w `save()`** (`sensors.ts:161-169`) — po WS3 zapisuje prawdziwe wartości zamiast domyślnych, ale nadal przepisuje **każdy** śledzony czujnik przy zapisie z **dowolnego** ekranu, włącznie z textareami wzorców w Ustawieniach. Po WS4 lista rośnie ze 157 do ~400 encji, więc zasięg tej wady **rośnie**, nie maleje.

18. **Brak siatki CI dla całej zmiany ścieżki alarmowej.** `.github/workflows/build.yml` odpala się wyłącznie na tagach `v*`, ostatnie osiem wydań zbudowano lokalnie bez tagu i bez przebiegu CI, workflow uruchamia wyłącznie `dotnet test` — pythonowy zestaw testów i `vitest` nie biegną w CI w ogóle. **To decyzja, nie ryzyko:** bramką jest pięć lokalnych komend z §5.0, uruchamianych przed **każdym** `./deploy/build-push.ps1`, i nic tego nie egzekwuje poza dyscypliną.

---

## 8. Audyt kompletności planu

**A (F-statusy)**
- A/F3 (brak progu precyzji): **CLOSED** — D-J + tabela §5.7 (lodówka ≥70%, zamrażarka 0 alarmów).
- A/F4 (`hst` bez ostrzeżenia w UI): **CLOSED** — `DetectorEntry.tsx` copy „legacy/niekalibrowany", badge w `ReplayPanel`, §6.
- A/F7 (nie napisane wprost, że odroczone): **CLOSED** — D-M + §6 akapit „F7 świadomie nierozwiązane". Zgrzyt: WS1 kryteria mówią „F7 odpowiedziane, **nie** odroczone" — ta sama rzecz nazwana dwoma sposobami.
- A/F5, F1, F2, F6, F8–F12, F15: **CLOSED** (bez zmian względem poprzedniej oceny).

**B (sprzeczności/niemierzalność)**
- B1 (ON→OFF vs 0% on-time): **CLOSED** — §5.2 alternatywa (a) albo (b).
- B2 (retained ON blokuje kryterium „OFF po restarcie"): **CLOSED** — §5.2 krok 0 (`mosquitto_pub -r -n`) + jawny OFF przy starcie (D-D).
- B3 (mtime niezaliczalne): **CLOSED** — §5.6 kryterium = `diff` zbioru ścieżek + delta `readingCount`.
- B4 (on-time ≥99% na zimnym hst): **CLOSED** — §5.6 „0 przejść ON→OFF w regionie scorowalnym", bez procentu.
- B5 (nieporównywalne okna): **CLOSED** — jawny `lookback=24h`, porównanie tylko `alertsPerDay`, `spanHours` w odpowiedzi.
- B6 (jak wykonać 200 odtworzeń): **CLOSED** — jeden kształt w całym dokumencie: `POST /api/sensors/{entityId}/simulate`, body `{detector, params, lookback, maxPoints}` (WS6 `Program.cs:677-685`, precedens route-param `Program.cs:654`). Receptury WS5, §5.6 i WS6 są identyczne i uruchamialne z wnętrza kontenera: `http://127.0.0.1:8099` (Kestrel binduje tylko IPv4, `Program.cs:219`), port na sztywno zamiast `ss` (nie ma go w obrazie), klient UI woła tę samą ścieżkę względnie (`client.ts:15-18`).
- B7 (gałąź gdy H1 padnie): **CLOSED** — WS4 „warunek stopu", D4 z budżetem 30 min, „diagnoza + zapis" = pełny wynik.
- B8 (procedura latencji): **CLOSED** — rozdzielone na dwie nazwane wielkości: **A15 = latencja werdyktu** (`latency_ms`, `ScoreStreamPipeline.cs:242-244`, okno 60 min po kalibracji, per encja, p95 < 1000 ms / max < 3000 ms) i **B8 = czas odpowiedzi symulatora** (`durationMs` z `SimulateCompleted` 7013, okno = 200 żądań z B6, n == 200, p95 < 1000 ms / max < 3000 ms). §5.6 mierzy dodatkowo A15-pod-obciążeniem (to samo pole, okno pętli, ≤ 1.5× p95 A15) — wyłącznie jako dowód nieperturbacji.

**C (build/CI/deploy)**
- C1 (brak edycji proto): **CLOSED** — WS6 `[modify] proto/argus.proto` z pełnymi wiadomościami + `IBatchDetectorClient.SimulateBatchAsync` + `BatchDetectorClient`.
- C2 (stuby Pythona): **CLOSED** — WS6 rozstrzyga: gitignored, `RUN python detector/scripts/gen_proto.py` w `argus/Dockerfile`, weryfikacja importu w §5.1.
- C3 (bump w każdym WS): **CLOSED** — recepta w §3 + krok deploy w WS2/WS3/WS4/WS5/WS6. WS1 nie ma go na liście `[modify]` (tylko w §3/§5.1) — kosmetyka.
- C4 (SPA faktycznie w obrazie): **CLOSED** — §5.6 krok 0 `docker run ... grep argus-replay` + render pod Ingressem.
- C5 (nośnik release notes): **CLOSED** — jedynym nośnikiem not upgrade'owych jest `argus/CHANGELOG.md` (D-E §2, [create] w WS3 + bump `argus/config.yaml:3`); Supervisor pokazuje go w oknie Update, czyli zanim padnie pierwszy start 2.2.0 (§5.3 krok 0). `argus/DOCS.md` = stała dokumentacja operatorska. §5.3, §6 i §7 #10 poprawione. Poprzednio: D-E i WS3 tworzyły `argus/CHANGELOG.md`, a §5.4 i §7 #10 każą pisać do `argus/DOCS.md` i twierdzą, że CHANGELOG-a nie ma. Dwa nośniki, sprzeczne.

**D (łamanie wdrożenia)**
- D1 (`unique_id` z nazwą detektora): **CLOSED** — obowiązuje wariant (a) D-G/WS3: `UniqueId.cs:13-18` → `argus_{slug}_anomaly` / `argus_{slug}_score`, `detectors[0].name` = `"rmad"`, retrakcja `argus_{slug}_{det}_*` dla KAŻDEJ pary (slug, detektor) sprzed migracji. Warianty (b) i (c) wykreślone z WS1, §5.4 i WS4/E4 — powód: `DiscoveryPublisher.cs:48-51` daje przy KAŻDEJ zmianie detektora (także `mad`/`stl` z pickera) drugą encję HA na tym samym temacie `argus/{slug}/flag/state`, a `RetractAsync` (`:171-187`) tego nie sprząta.
- D2 (ślepota WS3→WS5): **CLOSED** — jedna numeracja (§4) i jedna kolejność: WS1 → WS2 → **WS5 (backfill, 2.1.14)** → **WS3 (migracja, 2.2.0)** → WS4 → WS6. §5 numeruje podsekcje tą samą kolejnością (5.3 = WS5, 5.4 = WS3, 5.5 = WS4), a §5.4 nie mówi już „dopóki nie wyląduje WS5": po Swapie (`Program.cs:490`) `ScoreStreamPipeline.cs:320` primuje świeży klucz `rmad` z Recordera (`registry.py:261-271`).
- D3 (`frozen_window` w migracji): **CLOSED** — frozen wyłączony JEDNYM kluczem `frozen_variance_threshold: "0.0"` na obu gałęziach, `frozen_window` przenoszony verbatim (`"0"` crashuje `FrozenSensorDetector.cs:29-31` przez `ScoreStreamPipeline.cs:172` i łamie `InputValidator.cs:101`); D-H przepisane pod tę regułę, etykieta `D-J` znaczy już tylko progi F3, frozen nosi wszędzie `D-H`. Nieaktualny opis luki: WS3 4a przeczyło D-H („frozen zostaje włączony, 10/0.001 przenoszone **verbatim, NIE zerowane**"). Dodatkowo etykieta `D-J` znaczy w §2 progi F3, a w WS3 „wyłączenie frozen" — kolizja identyfikatorów.

**E (WS6)**
- E1 (reset per `entityId`): **CLOSED** — `Map<entityId, ReplayState>` + `useEffect([entityId])` + test.
- E2 (cache historii): **CLOSED w kodzie, sprzeczne w tekście** — WS5 i WS6 mają cache 60 s + testy, a §7 #13 nadal twierdzi „cache nie istnieje — 200 odtworzeń to 200 uwierzytelnień".
- E3 (test parzystości seamu): **CLOSED** — `InfluxDataSourceParityTests` + `SeamParity_...`.
- E4 (`number.*` end-to-end): **CLOSED** — WS4 kryterium E4 + §5.5 (ale patrz §6 niżej: niemierzalne dla encji sterowanych akcją).

**F (czego recenzent nie znajdzie)** — wszystkie pięć pozycji **CLOSED** (proto, `SimulateSummary`/`ReplaySimulator.cs`, próg F3, gałąź H1, receptury pomiarowe), bez zastrzeżeń — B6, B8 i D1 zamknięte wyżej.

### 8.2. Pokrycie F1–F15
Wszystkie F1–F15 są adresowane albo jawnie odroczone z uzasadnieniem (F4/F5/F7 — §6). **Nic nie jest cicho porzucone.** Zastrzeżenie: F13 jest odbierane na fiksturach, nie na realnych seriach (WS1 „DEGRADOWANE"), a domknięcie odroczone do `fixtures/real_24h.json`, którego nikt nie zrzuca przed WS5.

### 8.3. Sprzeczności z pomiarami
- **`scale_floor`.** WS1 mierzy: memory przy `scale_floor=0.0` → **4 epizody / 7.02%**; zero dopiero przy `0.3`. WS3 wpisuje w `RmadParams`/`DetectorDefaults` `scale_floor = 0.0` i migruje encje na `DetectorDefaults.Get("rmad")` — reguła D-I („`%` ⇒ `0.3`") **nie występuje w żadnej liście zmian**. Jednocześnie §5.2/§5.7/D-J wymagają `memory_use_percent` = **0 alarmów**. Plan jak napisany dowozi zmierzony regres i sam się nim wywraca. To najpoważniejsza pojedyncza niespójność liczbowa.
- **Lodówka on-time.** WS1: 2 epizody / **11.6%**. WS2 test: „2 zdarzenia, on-time **2–8%**". §5.2: „<10% na każdej". Trzy niekompatybilne liczby dla tego samego czujnika.
- **`processor_use`.** WS1 mierzy 1 ep / **2.97%**; D-J wymaga „≤2% on-time". Własny pomiar przekracza własny próg.
- **`memory` w §5.6.** Tabela dopuszcza `0–2` alerty/dobę, D-J i §5.7 wymagają dokładnie `0`.
- **Deadline symulacji.** WS6: 30 s. §5.6: „deadline 10 s po stronie adaptera". 
- **Przepustowość:** WS1 36.3 µs/pkt vs §5.6 „38.6 µs/pkt zmierzonych".
- **Rozmiar WS2.** §2 (D-C/D-D) i §3 opisują WS2 jako „bramka bez zmian, tylko domyślne 0.5/0.375 + higiena". §6 **jawnie odrzuca** rank/robust-z/`min_duration_sec`/`refractory_sec`/`max_events_per_hour`/`evidence_mode`/`alert_mode: legacy`/`AlertStateStore`. Szczegółowa sekcja WS2 buduje **dokładnie to wszystko** (~6 nowych plików). §5.2 mierzy artefakty wersji odrzuconej (`max_event_duration_sec`, 7012). Nie da się wykonać obu.

### 8.4. Łamanie działającego wdrożenia przy upgrade
- **`unique_id` — rozstrzygnięte na wariant (a)** (D-G/WS3). Operator dostaje dokładnie JEDNĄ nową parę bez detektora (`binary_sensor.argus_{slug}_anomaly`, `sensor.argus_{slug}_score`), a `RetractLegacyDetectorScopedAsync` kasuje retained config każdej pary (slug, detektor) sprzed migracji — zero sierot na jednym temacie stanu. Koszt przyjęty świadomie: 24 h historii zostaje pod starym `entity_id` (§7 #1), złagodzenie = ręczny rename encji w HA przed startem 2.2.0.
- **`params["algorithm"]="rmad"` nie jest niczyją pracą.** WS1 deklaruje wyłącznie kontrakt i sam wpisuje na listę OTWARTYCH: `ScoreStreamPipeline.cs:446` (`d.Name=="hst"`), `BuildHstParamsMap` (filtruje do `window`+`n_trees`), `WarmupRequest{Detector="hst"}`, `Program.cs:497 hasHst`. WS2 pinuje `:445` jako **BEZ ZMIAN**. WS2 (D1/K4) jest wlascicielem: przepisuje `ScoreStreamPipeline.cs:445-452` i `BuildDetectorParamsMap`, WS3 dokłada `"rmad"` do `InputValidator.cs:26`. Nikt tego nie robi → po migracji `entities.yaml` na `rmad` orkiestrator trafia w fallback `new HstParams()` = 250/0.7/0.3 = stan F0. Bramka sekwencji z WS3 (1a) wykryje to i odmówi zapisu, czyli **migracja nigdy się nie wykona**. To blokada wykonalności, nie ryzyko.
- **Kolejność WS3↔WS5** — zamknięta: §3 i §5 mają jedną numerację (5.3 = WS5 backfill, 5.4 = WS3 migracja, 5.5 = WS4 rejestr).
- **Downgrade** po `schema_version: 2` — udokumentowany (§7 #10), nośnik rozstrzygnięty (C5): `argus/CHANGELOG.md`.

### 8.5. Braki build/CI/deploy
- **Brak kroku tworzącego `GET /api/sensors/diagnostics`** — §3 poz. 5 i całe §5.5 (D2/D5, `rawStateCount`, `numericCount`, `lateAddedCount`) stoją na tym endpoincie; lista zmian WS4 dodaje wyłącznie liczniki do linii logu i `LogEvents`.
- **Brak kroku wpisującego `scale_floor` per jednostka** (D-I) — nie ma go ani w `EntitiesSchemaMigrator`, ani w `DetectorDefaults`.
- **Brak kroku przepisującego `ScoreStreamPipeline`/`BuildDetectorParamsMap`/`WarmupRequest.Detector`** — patrz §4.
- **Brak kroku zrzutu `detector/tests/fixtures/real_24h.json`** — WS1 wymienia go jako `[create]`, ale źródło (`history/history_during_period`) powstaje dopiero w WS5, a odbiór F13 jest do niego przypięty.
- Konflikt nośnika dokumentacji operatorskiej — **zamknięty** (C5): noty upgrade'owe wyłącznie w `argus/CHANGELOG.md` ([create] w WS3), `argus/DOCS.md` bez zmiany zakresu.
- CI pozostaje nienaprawione — świadomie (§7 #18); bramką jest pięć lokalnych komend.

### 8.6. Kryteria niemierzalne jak napisane
- `AlertEventForceClosed (7012)` (§5.2, §5.7) — `LogEvents` definiuje pod 7012 `AlertStormRaised`. Grep za nieistniejącym stringiem.
- §5.2 „log Debug per werdykt niesie próg; wartość `0.7` zamiast `0.5`" — format loga z WS2 zawiera `rank/z/state/published/latency_ms`, **nie** próg.
- §5.5 w całości (D1/D2/D5) — brak endpointu `diagnostics`.
- §5.6 receptura 200 odtworzeń — używa `readingCount` z `/api/sensors`, którego żadna lista zmian nie dodaje (URL i body zamknięte w B6).
- §5.6: „drugi równoległy POST → `{ok:false,kind:'busy'}` <50 ms", „historia 20001-punktowa → `INVALID_ARGUMENT`", „semafor dwuslotowy po stronie detektora" — żaden z tych mechanizmów nie występuje w zmianach WS6 (tam: clamp `[100,5000]`, deadline 30 s).
- WS4/E4 + §5.5: „w ciągu 120 s score sensor `number.*_termostat_*` ma wartość ≠ `unknown`" — plan sam stwierdza (§3 poz. 5), że te encje zmieniają się **na akcję, nie na poll**, a `min_samples=60` wymaga 60 punktów. Kryterium nieosiągalne w 120 s z definicji.
- §5.3 nazwa loga: WS5 kryterium mówi `primed <entity> <n> points from HA Recorder`, §5.3 grepuje `HistoryFetched`, a kod loguje `Primed {EntityId} with {PointCount} history points` (`ScoreStreamPipeline.cs:413-414`). Trzy różne stringi.
- Chip encji-ducha: WS4 `„Nieznana w HA"` vs §5.5 `„Brak w HA"`.
- §5.2 wiersz „on-time <10% na każdej" — nieosiągalny dla lodówki wobec własnego pomiaru 11.6% (§3).