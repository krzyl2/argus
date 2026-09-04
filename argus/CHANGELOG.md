# Argus — historia zmian

Supervisor pokazuje ten plik w oknie **Update**, czyli **zanim** nowa wersja wystartuje.
To jedyny nośnik not wydaniowych — `argus/DOCS.md` jest stałą dokumentacją operatorską i not
upgrade'owych nie niesie.

---

## 2.1.13 — ścieżka InfluxDB (batch + grupy) w ogóle nie znajdowała danych

> **Dotyczy tylko instalacji z ustawionym `influx_url`.** Detekcja strumieniowa
> (pojedyncze sensory, WebSocket HA) była i jest nietknięta.

### Co było zepsute

Trzy wady na ścieżce wsadowej, wszystkie niewidoczne dopóki InfluxDB nie był skonfigurowany —
bez `influx_url` orchestrator w ogóle nie rejestruje `BatchSchedulerWorker`, więc defekty
ujawniały się dopiero w momencie włączenia batcha:

1. **Filtr `entity_id` nie trafiał w nic.** Integracja `influxdb:` w HA taguje punkty
   **object_id** encji (`domain=sensor, entity_id=salon_temperature`), a Argus filtrował po
   pełnym `sensor.salon_temperature`. Zero serii, każdy cykl, zarówno dla grup, jak i dla
   pojedynczych sensorów (także przy zasilaniu historią do rozgrzewki).

2. **Jeden globalny `influx_measurement` nie mógł obsłużyć grupy o mieszanych jednostkach.**
   HA nazywa measurement **jednostką** encji (`°C`, `%`, `V`, `W`, `kPa`), a nie
   `homeassistant`. Grupa temperatura+wilgotność albo napięcie+prąd+moc rozkłada się na kilka
   measurementów; równość na jednym z nich gubiła pozostałe kolumny, a w trybie `joint`
   brakująca kolumna pomija całą grupę w każdym cyklu.

3. **Tryb `peer_divergence` nigdy nie wychodził ze statusu „Oczekuje".** Cache ostatniego
   verdictu zapisywał wyłącznie tryb `joint`, więc `GET /api/groups/{id}/status` zwracał
   `null` na zawsze — lista Detektory pokazywała żółte „Oczekuje" dla grupy, która realnie
   scorowała i publikowała encje w HA co cykl.

### Co się zmieniło

- Zapytania Flux filtrują po object_id; wyniki i świeżość wracają nadal pod pełnymi
  `entity_id`, więc reszta systemu się nie zmienia.
- Dwaj członkowie grupy o tym samym object_id (np. `sensor.x` + `binary_sensor.x`) są w
  InfluxDB nierozróżnialni — cykl jest pomijany z błędem w logu, zamiast przypisać jednemu
  odczyty drugiego.
- **`influx_measurement` jest teraz opcjonalny i domyślnie PUSTY** = brak filtra
  `_measurement`. Serię identyfikuje `entity_id` + `influx_value_field`, co już jest
  unikalne. Wartość ustawiaj tylko, jeśli masz w HA `override_measurement`.
- Tryb `peer_divergence` wypełnia cache statusu z verdictów członków: wynik = wynik
  **najbardziej rozjeżdżonego członka** (nic nie jest agregowane sztucznie), flaga =
  „co najmniej jeden członek się rozjechał", a wkłady to wyniki poszczególnych członków
  posortowane malejąco — dla peer divergence to jest właśnie atrybucja per-cecha.

### Po aktualizacji

Jeśli masz w opcjach `influx_measurement` ustawione na cokolwiek (np. odziedziczone
`homeassistant`), a nie używasz `override_measurement` — **wyczyść to pole**. Stara wartość
nie jest nadpisywana przez aktualizację i nadal odfiltruje wszystko poza jedną jednostką.

---

## 2.2.0 — nowy detektor `rmad` + jednokierunkowa migracja konfiguracji

> **Przeczytaj przed aktualizacją.** To wydanie zmienia `entity_id` encji Argusa w HA,
> zmienia znaczenie sensora score i migruje `/data/entities.yaml` **bez drogi powrotnej**.

### Dlaczego

Dotychczasowy detektor `hst` (`river.anomaly.HalfSpaceTrees`) liczy **rzadkość** wartości,
a nie **odchylenie** od normy. Na skwantowanym szeregu rzadka‑ale‑całkowicie‑normalna wartość
dostaje wyższy wynik niż wartość modalna, a nieograniczony normalizator po jednym skoku
zapada całe normalne pasmo do ułamka zakresu i już z niego nie wraca. Efekt w polu: pięć
`binary_sensor` zapalonych na stałe przez ponad dobę, przy precyzji alarmów rzędu 1–4 %.

`rmad` liczy krocząco medianę i MAD w oknie i publikuje **bezwymiarowy** wynik
`z / (z + 5)`, gdzie `z = |x − mediana| / (1,4826 · MAD)`. Dzięki temu **jedna** tabela
progów jest poprawna na każdym czujniku niezależnie od jednostki i zakresu.

### 1. Zmiana `entity_id` w Home Assistant — wymaga ręcznego działania

Nazwa detektora znika z identyfikatora encji:

| przed | po |
|---|---|
| `binary_sensor.argus_sensor_<slug>_hst_anomaly` | `binary_sensor.argus_sensor_<slug>_anomaly` |
| `sensor.argus_sensor_<slug>_hst_score` | `sensor.argus_sensor_<slug>_score` |

Przykład: `binary_sensor.argus_sensor_load_5m_hst_anomaly` → `binary_sensor.argus_sensor_load_5m_anomaly`.

Powód: temat stanu (`argus/{slug}/flag/state`) nigdy nie zawierał nazwy detektora, więc
zostawienie jej w `unique_id` oznaczałoby, że każda zmiana detektora tworzy **drugą** encję HA
karmioną tym samym tematem, a stara zostaje jako osierocona retained config. Zmiana następuje
**raz**; każda przyszła zmiana detektora jest już bezkosztowa.

**Co robi Argus:** przy pierwszym starcie po migracji publikuje pustą retained payload na stare
tematy discovery każdej pary (czujnik, detektor) sprzed migracji, więc stare encje znikają
z rejestru HA po jego restarcie. Duplikatów nie będzie.

**Czego Argus NIE zrobi:** nie przeniesie historii. Dotychczasowa historia Recordera, wykresy,
dashboardy i automatyzacje zostają pod **starym** `entity_id`.

**Zalecane działanie — PRZED pierwszym startem 2.2.0:**
Ustawienia → Urządzenia i usługi → Encje → znajdź starą encję → zmień jej `entity_id` na nowy
(bez segmentu `_hst_`). HA przeniesie wtedy historię razem z nazwą. Alternatywnie: po
aktualizacji przepnij dashboardy i automatyzacje ręcznie według tabeli wyżej.

### 2. Sensor score znaczy co innego

`sensor.argus_..._score` publikuje teraz **zduszony robust‑z**, nie masę HST:

| wynik | znaczenie |
|---|---|
| 0,5 | odchylenie 5σ (robust) |
| 0,8 | odchylenie 20σ (robust) |

Zakres `[0, 1)` i temat MQTT są bez zmian, ale **historia sprzed aktualizacji jest
nieporównywalna** z tym, co pojawi się po niej. Progi w automatyzacjach opartych na wartości
score trzeba przeliczyć.

### 3. Migracja `/data/entities.yaml` — jednokierunkowa

Plik dostaje `schema_version: 2`. Migracja jest jednorazowa, idempotentna i głośna:

- Encja z **nietkniętym** blokiem `hst` (dokładnie `window 250`, `n_trees 25`,
  `high_threshold 0.7`, `low_threshold 0.3`, `min_consecutive 3`, `frozen_window 10`,
  `frozen_variance_threshold 0.001`, albo `params: {}`) → przepisana na `rmad`
  z domyślnymi `720 / 60 / 5.0 / 0.0 / 0.5 / 0.375 / 3 / 10 / 0.0`.
  W logu: `Migrated <entity_id>: hst -> rmad (schema_version 2)`.
- Encja ze **strojonymi ręcznie** parametrami `hst` → **zostaje na `hst`**, z ostrzeżeniem
  `Entity <entity_id> has tuned hst params — left on hst`. Nie istnieje odwzorowanie
  zachowujące znaczenie z bezwzględnego progu HST na próg robust‑z, więc taka encja nie jest
  przepisywana. Przełącz ją ręcznie w UI, jeśli chcesz nowego detektora.
- Encja na `rmad`, `mad` lub `stl` → nietknięta, bez ostrzeżenia.
- `groups:` i `_patterns:` przenoszone bez zmian.

**Kopia zapasowa:** `/data/entities.yaml.pre-v2.bak` powstaje przed pierwszym zapisem i nigdy
nie jest nadpisywana.

**Rollback konfiguracji:**

```sh
cp /data/entities.yaml.pre-v2.bak /data/entities.yaml
```

**Downgrade obrazu jest niebezpieczny.** Starszy build odrzuci detektor `rmad` przy walidacji
i zbuduje stan wykonawczy z domyślnych `hst` (250 / 0,7 / 0,3), czyli dokładnie ze stanu sprzed
naprawy. Jedyne poprawne odzyskanie to skopiowanie `.pre-v2.bak` **razem** z cofnięciem obrazu.

### 4. Wykrywanie zamarłego czujnika wyłączone domyślnie

Migracja zapisuje `frozen_variance_threshold: "0.0"` (wariancja próbki nigdy nie jest ujemna,
więc reguła nigdy się nie zapala) i przenosi `frozen_window` **verbatim**.

Powód: przy `frozen_window 10` i progu `0.001` czujnik mocy, który przez większość doby
pokazuje 0 W, był uznawany za zamarły przez cały postój sprężarki — a stan „frozen" omijał
rozgrzewkę, wyciszenie i histerezę, wymuszając flagę ON.

**Świadoma utrata pokrycia:** czujnik zamarły na **niezerowej** stałej wartości nie zostanie
teraz zgłoszony przez Argusa. Jeśli tego potrzebujesz, ustaw `frozen_variance_threshold`
ręcznie i licz się z powrotem powyższego zachowania.

### 5. Cisza po aktualizacji na wolnych czujnikach

`rmad` wydaje pierwszy werdykt po `min_samples` (domyślnie 60) próbkach. Priming z HA Recordera
(wydany w 2.1.14) napełnia okno historią przy otwarciu strumienia, więc normalnie ciszy nie ma.
Jeżeli jednak backfill jest wyłączony (`ARGUS_BACKFILL_ENABLED=false`) albo Recorder nie zwraca
danych, czujnik raportujący co ~6,5 min potrzebuje ~6,5 h do pierwszego werdyktu. W UI widać
wtedy chip `Rozgrzewka n/60`, a w logu linię o pustej historii.

### 6. Pozostałe

- Nowe encje domyślnie dostają detektor `rmad`.
- `hst` zostaje dostępny jako ścieżka rollbacku i jest tak oznaczony w UI
  („legacy / niekalibrowany — wymaga ręcznego strojenia progów"). Jego checkpointy
  w `/data/models/<slug>/hst/` **nie są** kasowane ani nadpisywane, więc przełączenie encji
  z powrotem na `hst` wskrzesza dotychczasowy stan modelu.
- Edytor czujnika pokazuje, co znaczy każdy próg dla **tego** czujnika: `0,5` renderuje się jako
  „= odchylenie 5,0σ (robust)", a `window 720` jako zmierzony czas ścienny (~3 h na jednym
  czujniku, ~78 h na innym — okno jest liczone w próbkach, nie w czasie).
