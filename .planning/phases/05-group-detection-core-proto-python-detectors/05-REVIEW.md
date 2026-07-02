---
phase: 05-group-detection-core-proto-python-detectors
reviewed: 2026-07-02T00:00:00Z
depth: standard
files_reviewed: 13
files_reviewed_list:
  - proto/argus.proto
  - detector/argus_detector/group/__init__.py
  - detector/argus_detector/group/peer_divergence.py
  - detector/argus_detector/group/multivariate_detector.py
  - detector/argus_detector/model_store.py
  - detector/argus_detector/registry.py
  - detector/argus_detector/servicer.py
  - detector/requirements.txt
  - detector/tests/test_peer_divergence.py
  - detector/tests/test_group_multivariate.py
  - detector/tests/test_group_model_store.py
  - detector/tests/test_servicer.py
  - detector/tests/test_proto_codegen.py
findings:
  critical: 0
  warning: 3
  info: 3
  total: 6
status: issues_found
---

# Phase 05: Code Review Report

**Reviewed:** 2026-07-02T00:00:00Z
**Depth:** standard
**Files Reviewed:** 13
**Status:** issues_found

## Summary

Zweryfikowano warstwę group-detection (proto, peer-divergence, joint-multivariate, model store, registry, servicer) oraz testy. Rdzeń logiki jest solidny i zgodny z badaniami z RESEARCH.md:

- Atrybucja ECOD/COPOD (`self._model.O[-len(matrix):]`) zweryfikowana eksperymentalnie (uruchomiono realny kod z zainstalowanym pyod==3.6.0) — tail-slice jest poprawny, wywoływany synchronicznie zaraz po `decision_function()`, wyniki są stabilne przy wielokrotnych wywołaniach. Brak wywołań `predict()` w całym module `group/` ani w `servicer.py` — atrybucja nie jest korumpowana.
- `PCA(standardization=False)` zweryfikowane empirycznie — `threshold_` istnieje i ma sensowną wartość po `fit()` dla wszystkich 4 detektorów (ecod/copod/pca/iforest); brak podwójnego skalowania, RobustScaler pozostaje jedynym źródłem skalowania.
- Guard MAD=0 → meanAD fallback → zera (nie NaN) jest poprawny i odróżnialny od stanu "poniżej progu" (`_MIN_MEMBERS`), zgodnie z GRP-04.
- `group_` prefix (`group_slug()`) jest jedynym miejscem budowania namespace'u, kolizja z encją dosłownie nazwaną `group_x` jest udokumentowanym, zaakceptowanym edge case'em pokrytym testem — nie zgłaszam tego jako defektu.
- Ragged-series guard (mismatched value-array lengths) poprawnie odrzuca dane przed konstrukcją macierzy w obu `ScoreGroupBatch` i `FitGroup`.

Znaleziono jednak lukę walidacji wejścia (pusta lista `series`), niespójność w propagacji błędów dla tego przypadku, oraz drobne braki jakościowe (dostęp do prywatnego atrybutu przez granicę modułu, martwa gałąź NaN/bool, brak dokumentacji nowej zależności).

## Warnings

### WR-01: Pusta lista `series` omija walidację i ląduje jako niekontrolowany `ok=False` zamiast `INVALID_ARGUMENT`

**File:** `detector/argus_detector/servicer.py:228-234` (ScoreGroupBatch) oraz `:338-344` (FitGroup)

**Issue:** Guard ragged-input buduje `lengths = {len(s.values) for s in request.series}` i odrzuca tylko gdy `len(lengths) > 1`. Gdy `request.series` jest puste, `lengths` to pusty zbiór — `len(lengths) == 0`, więc walidacja przechodzi. Dalej `matrix = [list(col) for col in zip(*(s.values for s in request.series))]` zwraca `[]` (pusta lista), co przy przekazaniu do `PeerDivergenceDetector.score_batch([])` powoduje `np.array([], dtype=float)` o kształcie `(0,)` — rozpakowanie `n_timestamps, n_members = x.shape` rzuca `ValueError: not enough values to unpack`. Wyjątek jest łapany przez ogólny `except Exception as e` w `ScoreGroupBatch`/`FitGroup` i zwracany jako `ok=False, error=str(e)` — surowy komunikat Pythona zamiast kontrolowanego `context.abort(INVALID_ARGUMENT, ...)`, niespójnie z sąsiednimi guardami (empty `group_id`, unknown detector, ragged series), które wszystkie poprawnie abortują. To jest dokładnie klasa przypadków, którą miał pokryć guard T-05-09 (walidacja wejścia przed konstrukcją macierzy), ale przypadek "zero serii" nie jest przez niego łapany. Brak testu na ten przypadek w `test_servicer.py`.

**Fix:**
```python
lengths = {len(s.values) for s in request.series}
if not request.series or len(lengths) > 1:
    context.abort(
        grpc.StatusCode.INVALID_ARGUMENT,
        "empty series list" if not request.series else f"ragged series: mismatched value-array lengths {sorted(lengths)}",
    )
    return None
```
Zastosować identyczną poprawkę w obu miejscach (`ScoreGroupBatch` i `FitGroup`).

### WR-02: Servicer sięga po prywatny atrybut `model._model.threshold_`, łamiąc enkapsulację `GroupMultivariateDetector`

**File:** `detector/argus_detector/servicer.py:288`

**Issue:** `is_anomaly = bool(group_score > model._model.threshold_)` odwołuje się bezpośrednio do prywatnego pola `_model` obiektu `GroupMultivariateDetector` z zewnętrznego modułu (`servicer.py`). `GroupMultivariateDetector` nie eksponuje żadnej publicznej właściwości `threshold_`/`is_anomaly_at()`, więc servicer musi łamać konwencję nazewniczą (`_model`) żeby zaimplementować logikę progu. To tworzy ukrytą zależność: jeśli `multivariate_detector.py` kiedykolwiek zmieni nazwę wewnętrznego atrybutu `_model` lub sposób budowy PyOD instancji, `servicer.py` przestanie działać bez żadnego ostrzeżenia na granicy modułu (brak kontraktu/interfejsu).

**Fix:** Dodać publiczną metodę/właściwość na `GroupMultivariateDetector`, np.:
```python
# w multivariate_detector.py
def is_anomaly(self, score: float) -> bool:
    """True if score exceeds the underlying PyOD detector's fitted threshold_."""
    return bool(score > self._model.threshold_)
```
i w servicer.py: `is_anomaly = model.is_anomaly(group_score)`.

### WR-03: `score_group()` zawiera martwą gałąź NaN/bool, nieosiągalną przez publiczne API — duplikacja floor-check

**File:** `detector/argus_detector/group/peer_divergence.py:59-73`

**Issue:** `score_group(matrix)` zawiera własny check `if n_members < _MIN_MEMBERS` i w tej gałęzi zwraca `nan.astype(bool)` — rzutowanie NaN na bool jest zdefiniowane jako `True` (niezerowy float), co semantycznie oznaczałoby "wszyscy są anomalią", myląco odwrotnie od zamierzonego "brak werdyktu". Komentarz w kodzie to przyznaje ("NaN cast to bool is undefined; caller must check score NaN first"), ale jedyny produkcyjny wywołujący (`PeerDivergenceDetector.score_batch`) już odrzuca `n_members < _MIN_MEMBERS` PRZED wywołaniem `score_group()` (linia 106-111), więc ta gałąź jest martwym kodem w ścieżce produkcyjnej — osiągalna tylko przy bezpośrednim wywołaniu `score_group()` (jak w testach), czyli poza kontraktem klasy `PeerDivergenceDetector`. Podwójna implementacja tego samego progu w dwóch miejscach ryzykuje rozjazd przy przyszłej zmianie `_MIN_MEMBERS`.

**Fix:** Usunąć duplikat floor-check z `score_group()` (funkcja modułowa) i pozostawić go wyłącznie w `PeerDivergenceDetector.score_batch()`, albo — jeśli `score_group()` ma pozostać publicznym API — udokumentować jawnie, że wywołujący SAM musi sprawdzić `n_members` przed wywołaniem, i zwracać `None`/rzucać wyjątek zamiast `nan.astype(bool)` które wprowadza w błąd:
```python
def score_group(matrix: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    n_timestamps, n_members = matrix.shape
    if n_members < _MIN_MEMBERS:
        raise ValueError(f"insufficient members: got {n_members}, need >= {_MIN_MEMBERS}")
    ...
```

## Info

### IN-01: `scikit-learn` — nowa zależność nieudokumentowana w CLAUDE.md Technology Stack

**File:** `detector/requirements.txt:6`

**Issue:** `scikit-learn==1.8.0` został dodany jako zależność (dla `RobustScaler` w `multivariate_detector.py`), ale sekcja "Technology Stack" w `CLAUDE.md` wymienia tylko grpcio/PyOD/River/Darts/joblib/statsmodels — scikit-learn nie figuruje. Licencyjnie jest OK (BSD-3, zgodny z ograniczeniem D-constraint), ale dokumentacja stacku rozjeżdża się z rzeczywistym `requirements.txt`.

**Fix:** Dopisać wpis do tabeli stacku w CLAUDE.md: `scikit-learn — 1.8.0` z uzasadnieniem (RobustScaler dla GRP-06).

### IN-02: `model_store.py` — brak jawnego `encoding="utf-8"` w `write_text`/`read_text`

**File:** `detector/argus_detector/model_store.py:306, 324, 334, 339`

**Issue:** Wszystkie wywołania `Path.write_text()`/`read_text()` (m.in. `_write_entity_id`, `_write_version_json`, `_update_latest`, `_read_latest`) polegają na domyślnym kodowaniu platformy zamiast jawnego `encoding="utf-8"`. Na docelowym środowisku (Linux container, `python:3.12-slim-bookworm`) domyślne kodowanie to UTF-8, więc obecnie nieszkodliwe, ale niejawna zależność od lokalnego locale jest krucha — a projekt jest rozwijany także na Windows (obecne środowisko deweloperskie), gdzie domyślne kodowanie bywa `cp1252`.

**Fix:**
```python
(d / "entity_id.txt").write_text(entity_id, encoding="utf-8")
...
(d / "version.json").write_text(json.dumps(meta), encoding="utf-8")
...
tmp.write_text(str(version), encoding="utf-8")
...
return int(latest.read_text(encoding="utf-8").strip())
```

### IN-03: Fit/FitGroup TOCTOU na `next_version()` — brak blokady obejmującej read-version + save

**File:** `detector/argus_detector/servicer.py:111-119` (Fit) oraz `:357-360` (FitGroup)

**Issue:** `version = self._model_store.next_version(...)` czyta bieżącą najwyższą wersję, a zapis (`save_pyod`/`save_group_bundle`) następuje dopiero po treningu modelu — bez blokady spinającej obie operacje. Dwa równoległe wywołania `Fit`/`FitGroup` dla tego samego `(entity_id/group_id, detector)` mogą odczytać tę samą wartość `next_version()` i oba zapisać pod tym samym numerem wersji (nadpisanie), lub – gorzej – jeden zapis nadpisze `latest` wskazujący na wersję drugiego requestu w nieokreślonej kolejności. To pre-istniejący wzorzec (obecny już w `Fit` z wcześniejszych faz), powielony teraz 1:1 w nowym `FitGroup` bez adresowania.

**Fix:** Poza zakresem tej fazy do pełnego naprawienia, ale warto rozważyć w przyszłej fazie: serializować `next_version()` + zapis pod tym samym per-(key) lockiem co `registry._entity_lock()`, albo generować numer wersji atomowo w `ModelStore` (np. `os.O_EXCL` na katalogu wersji) zamiast read-then-increment.

---

_Reviewed: 2026-07-02T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
