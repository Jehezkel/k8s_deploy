# K8s Deploy Simulator

Lokalne testowanie dostępności TargetApp przed, podczas i po rollout w Kubernetes.

```text
k6 na laptopie → http://target.test → Traefik Ingress → Service → 3 Pody TargetApp
Dashboard lokalnie → Kubernetes API
scripts/deploy.sh → Helm → aktualizacja Deploymentu
```

Chart obejmuje wyłącznie TargetApp. Dashboard i k6 działają poza klastrem,
więc aktualizacja lub usunięcie środowiska testowego ich nie wyłącza.
Dashboard obserwuje zasoby na żywo i pozwala uruchomić deploy z opcjonalnym k6.

## Wymagania

- Rancher Desktop z włączonym Kubernetes i Traefik; kontekst `rancher-desktop`.
- Helm (sprawdzono lokalnie z Helm 4), kubectl, .NET SDK 10 i lokalne k6.
- Moby/Docker albo containerd/nerdctl, zależnie od ustawień Rancher Desktop.
- Dostępny port wejściowy HTTP 80 Rancher Desktop na laptopie.

Polecenia poniżej wykonuj z katalogu głównego repozytorium.

## Szybki start

### 1. Sprawdź klaster i ustaw domenę

```bash
kubectl --context rancher-desktop get nodes
kubectl --context rancher-desktop get ingressclass
```

Domyślny chart używa klasy `traefik`. Dodaj lokalnie do `/etc/hosts`:

```text
127.0.0.1 target.test
```

To założenie dla Rancher Desktop udostępniającego Ingress na localhost.
Jeśli kontroler jest dostępny pod innym IP, wpisz ten adres. Wpis w hosts
nie otwiera portu ani nie instaluje kontrolera. Gdy localhost:80 nie odpowiada,
sprawdź ustawienia przekierowania portów/uprawnień Rancher Desktop.

### 2. Zbuduj i wdróż pierwszy obraz

```bash
bash scripts/build-image.sh dev-1
bash scripts/deploy.sh dev-1
curl -i http://target.test/
```

Dla containerd zamiast pierwszego polecenia:

```bash
bash scripts/build-image.sh dev-1 containerd
```

Build używa istniejącego Dockerfile z kontekstem `src/`. Chart ma
`image.pullPolicy: Never`, więc obraz musi być w silniku lokalnego klastra.
Odpowiedź `/` to `Hello World!`, z nagłówkami `X-App-Version` i `X-Pod-Name`.
Readiness korzysta z `/health/ready` (w tym przykładzie sygnalizuje tylko
gotowość procesu HTTP, bez sprawdzania zewnętrznych zależności).

### 3. Uruchom dashboard lokalnie

```bash
kubectl config use-context rancher-desktop
dotnet run --project src/KDS.Dashboard
```

Otwórz adres wyświetlony przez dotnet. Dashboard używa bieżącego kontekstu
kubeconfig; domyślny namespace to `kds-test`. Skrypt deployu jawnie wybiera
`rancher-desktop`, niezależnie od bieżącego kontekstu.
Przycisk **Deploy** otwiera edytor o wyglądzie YAML: klucze i struktura są stałe, edytować można wyłącznie wartości. Osobno dostępny jest checkbox **Force rebuild**. Tagi są automatyczne: `v1 → v2`.
Namespace pochodzi z `Kubernetes:Namespace` (domyślnie `kds-test`) i nie jest edytowalny w oknie.
**Dalej** otwiera konfigurację opcjonalnego load testu. **Rozpocznij test** pokazuje potwierdzenie z kontekstem i namespace. Cofanie zachowuje edycję.
Po potwierdzeniu okno zamyka się, a test działa w tle i wykonuje cały przebieg:

1. Waliduje YAML i renderuje obie wersje chartu przed zmianami w klastrze.
2. Przygotowuje jeden obraz pod tagami `v1` i `v2` (opis poniżej).
3. Usuwa cały namespace wraz z zasobami i tworzy go ponownie pod tą samą nazwą.
4. Instaluje release `demo` z tagiem `v1` i czeka na zakończenie rollout.
5. Aktualizuje tylko `image.tag` na `v2`, zachowując pozostałe values, i czeka na rollout.

**Force rebuild** buduje aktualny kod TargetApp jeden raz, bez cache, jako `v1`,
a następnie przypisuje temu samemu obrazowi tag `v2`. Bez zaznaczenia dashboard
sprawdza istniejący `v1` i przypisuje mu `v2`, bez przebudowy. Przy pierwszym
uruchomieniu zaznacz checkbox. Błąd budowania lub brak obrazu zatrzymuje test
przed czyszczeniem namespace. Edytor nie udostępnia `image.tag`;
lokalny test wymaga `image.pullPolicy: Never`.

Domyślny silnik to `moby` (`docker --context rancher-desktop`). Dla containerd
ustaw `ImageBuild:Engine=containerd` — używany jest `nerdctl --namespace k8s.io`.
`ImageBuild:DockerContext` zmienia kontekst Dockera. Obrazy muszą powstawać
w silniku używanym przez lokalny klaster. Katalog źródeł jest wyszukiwany w
rodzicach katalogu uruchomienia i aplikacji; możesz wskazać repo przez
`ImageBuild:SourceRoot`. Proces dashboardu musi mieć dostęp do źródeł,
`docker` lub `nerdctl`, `helm`, `kubectl` i kubeconfig. Używa kontekstu wybranego
przy starcie aplikacji; okno pokazuje kontekst i namespace przed uruchomieniem.
Domyślny chart jest kopiowany do katalogu wyjściowego aplikacji. Można wskazać
inny lokalny katalog przez `Helm:ChartPath` / `Helm__ChartPath`.
Obecny Dockerfile dashboardu nie instaluje CLI — ten przebieg jest przeznaczony
do lokalnego uruchomienia powyżej.

Błąd zatrzymuje dalsze etapy; stan klastra pozostaje dostępny do diagnozy.
Postęp, log oraz wynik testu są widoczne nad topologią. Zamknięcie okna, nawigacja
i odświeżenie strony nie przerywają pracy; kolejny test jest blokowany do zakończenia
bieżącego. Stan jest przechowywany w pamięci dashboardu, bez wznowienia po restarcie procesu.

Wspólny serwis backendu pobiera początkowe listy, następnie obserwuje pody, ReplicaSety i Deploymenty przez watch, niezależnie od otwartych kart przeglądarki. Topologia korzysta z tego samego strumienia co oś czasu.
Pokazuje osobne grupy ReplicaSetów z obrazem, rewizją, Deploymentem oraz licznikami
docelowych, aktualnych i gotowych replik, również po zmniejszeniu do zera.
Zdarzenia aktualizują istniejący diagram bez resetowania widoku. Przycisk **Dopasuj widok**
obejmuje wszystkie grupy i pody, w tym puste ReplicaSety. Przy zerwaniu połączenia
dane są oznaczone jako potencjalnie nieaktualne; watch wznawia się automatycznie,
a wygasła wersja zasobów wymusza ponowne pobranie listy. Reset namespace nie wymaga
odświeżania strony. Nie jest pobierany osobny strumień Kubernetes Events.
Okno pozwala opcjonalnie uruchomić k6 — konfigurację opisano w sekcji „Load test z ekranu Deploy”.

Testy przebiegu bez wykonywania komend w klastrze:

```bash
dotnet run --project tests/KDS.Dashboard.Checks
```

### 4. Testuj aktualizację pod ciągłym ruchem

Najpierw przygotuj nowy obraz (po zmianie kodu, jeśli testujesz nowy build):

```bash
bash scripts/build-image.sh dev-2
```

Terminal A — rozpocznij test:

```bash
DURATION=5m RATE=10 bash scripts/test-availability.sh
```

Poczekaj, aż k6 zacznie wysyłać żądania. Po około 30–60 sekundach
w terminalu B wykonaj:

```bash
mkdir -p artifacts
bash scripts/deploy.sh dev-2 > artifacts/deploy-dev-2.log 2>&1
```

Nie kończ k6 podczas rollout. Pozostaw co najmniej 60 sekund ruchu po
zakończeniu deployu. Domyślny test ma stały czas 5 minut; jeśli rollout
trwa dłużej, powtórz test z większym `DURATION`. Setup nie synchronizuje
automatycznie czasu zakończenia k6 ze stanem rollout.

k6 wysyła 10 iteracji/s, po jednym żądaniu na iterację, bez redirectów.
Sprawdza status 200, treść, nagłówki wersji/Poda, zero błędów,
zero pominiętych iteracji i p95 < 500 ms. Nie kończy testu po pierwszym błędzie.
Niespełnienie kryteriów daje niezerowy kod zakończenia skryptu.

W `artifacts/kds-k6.*/` znajdziesz log oraz próbki metryk z czasem.
Metryka `target_responses` ma tagi wersji, Poda i statusu. Log deployu
zawiera znaczniki UTC początku i osiągnięcia gotowości.
Wyniki lokalne pokazują zachowanie rollout; laptop dzieli zasoby z klastrem,
więc nie są benchmarkiem środowiska produkcyjnego.

Inny adres testu:

```bash
TARGET_URL=https://twoja-domena.example/ DURATION=10m RATE=20 bash scripts/test-availability.sh
```

## Chart i konfiguracja

```text
helm/target-app/Chart.yaml                 # wersja chartu
helm/target-app/values.yaml                # obraz, repliki, rollout, ingress
helm/target-app/templates/deployment.yaml
helm/target-app/templates/service.yaml
helm/target-app/templates/ingress.yaml
tests/k6/availability.js                   # zewnętrzny test ciągłości
```

Domyślnie 3 repliki, `maxSurge: 0`, `maxUnavailable: 1` i 5 sekund
stabilnej gotowości. Pody kończące działanie mogą chwilowo zwiększyć liczbę
widocznych obiektów ponad 3. Ta strategia nie jest gwarancją braku przerw:
test ma ujawnić problemy z gotowością, zamykaniem połączeń i routingiem.

HTTP jest celowym minimum lokalnego setupu. Aby testować HTTPS, ustaw
`ingress.tls.enabled=true`, host oraz `ingress.tls.secretName` i przygotuj
w tym samym namespace Secret TLS. Certyfikat musi być zaufany przez k6;
chart nie instaluje cert-managera ani nie wystawia certyfikatów.

Zmiana ustawień przez skrypt:

```bash
bash scripts/deploy.sh dev-2 --set replicaCount=2
```

Każdy deploy opiera się na domyślnych wartościach chartu i podanych override'ach.
Stałe ustawienia testu zapisuj w pliku i przekazuj za każdym razem przez `-f`.
Nie zmieniaj release'u, namespace ani hosta pomiędzy wersjami tego samego testu.

## Wersje chartów

`Chart.yaml: version` to wersja chartu; `image.tag` wybiera obraz.
`appVersion` jest informacyjne. Samo podbicie wersji chartu nie restartuje Podów,
jeżeli `spec.template` Deploymentu pozostaje bez zmian.

```bash
helm lint ./helm/target-app --strict
helm template demo ./helm/target-app -n kds-test
mkdir -p dist
helm package ./helm/target-app --destination ./dist
```

Po zmianach zwiększ `version` np. z `0.1.0` na `0.2.0`, zapisz zmianę w Git
i ponownie spakuj chart. Nie nadpisuj paczki już opublikowanej wersji.
Paczki i wyniki są ignorowane przez Git.

Wdrożenie konkretnej paczki:

```bash
CHART=./dist/target-app-0.1.0.tgz bash scripts/deploy.sh dev-1
CHART=./dist/target-app-0.2.0.tgz bash scripts/deploy.sh dev-2
```

Można testować nowy chart z tym samym obrazem, aby oddzielić zmianę
infrastruktury od zmiany kodu. Skrypt obsługuje też `KUBE_CONTEXT`,
`KUBE_NAMESPACE` i `HELM_RELEASE` jako zmienne środowiskowe.

## Diagnostyka i sprzątanie

```bash
kubectl --context rancher-desktop -n kds-test get pods,svc,ingress
kubectl --context rancher-desktop -n kds-test get events --sort-by=.metadata.creationTimestamp
helm status demo --kube-context rancher-desktop -n kds-test
helm history demo --kube-context rancher-desktop -n kds-test
```

`ErrImageNeverPull` oznacza brak obrazu w lokalnym silniku Kubernetes.
404 od Ingress sugeruje sprawdzenie hosta, klasy i reguł; 503 — gotowych
backendów i portu Service. Udany `helm --wait` nie weryfikuje zewnętrznego URL.

Po zapisaniu wyników, aby zachować namespace:

```bash
helm uninstall demo --kube-context rancher-desktop -n kds-test --wait
```

Albo usuń całe dedykowane środowisko (wszystkie zasoby w namespace):

```bash
kubectl --context rancher-desktop delete namespace kds-test
```

k6 działa lokalnie — usunięcie namespace nie zatrzyma testu. Poczekaj na
zakończenie testu przed sprzątaniem. W tym charcie nie ma hooków k6.

Źródła: [Helm](https://helm.sh/docs/),
[Ingress w Rancher Desktop](https://docs.rancherdesktop.io/how-to-guides/traefik-ingress-example/),
[k6 constant-arrival-rate](https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/constant-arrival-rate/).

## Load test z ekranu Deploy

Kroki **Values → Load test → Potwierdzenie** pozwalają włączyć opcjonalne k6.
Domyślnie jest wyłączone; proponowane parametry to 30 sekund przed, 30 sekund
po deployu i 10 użytkowników. Czasy: 0–3600 s, użytkownicy: 1–1000.
Każdy użytkownik wysyła GET `/` bez przerw, z timeoutem 5 s.

Dashboard uruchamia jeden lokalny proces k6 po Ready v1, odczekuje czas „przed”,
wykonuje rollout v2 pod ciągłym ruchem, a po Ready v2 odczekuje pełny czas „po”.
Metryki nad topologią odświeżają się co sekundę: liczba requestów, błędy HTTP,
nieudane checks, bieżący RPS i p95 od początku testu. Błędy requestów nie
przerywają deployu; wynik deployu jest oddzielony od wyniku ruchu.

Load test wymusza `ingress.enabled=true` i `ingress.className=traefik` w obu
wersjach. Host/TLS bierze z values. Automatyczny `kubectl port-forward` otwiera
losowy port wyłącznie na 127.0.0.1 i prowadzi do kontrolera Traefik, nie do poda
aplikacji. Nie trzeba zmieniać `/etc/hosts` ani wystawiać lokalnego portu 80.
HTTPS nadal wymaga zaufanego certyfikatu dla hosta z chartu.

Kontroler jest wykrywany przez etykietę `app.kubernetes.io/name=traefik` poza
namespace testu. Jeśli wynik nie jest jednoznaczny, ustaw razem
`LoadTest:TraefikNamespace` oraz `LoadTest:TraefikService`. Service musi mieć
port 80 dla HTTP lub 443 dla HTTPS. Wymagane są uprawnienia do odczytu Service
oraz port-forward. Dashboard sprawdza k6, skrypt i tunel przed resetem namespace.

Logi, próbki k6, podsumowanie i znaczniki faz UTC zapisują się w
`artifacts/kds-load-*` względem katalogu uruchomienia dashboardu. Katalog bazowy
można zmienić przez `LoadTest:ArtifactsPath`. Pełne próbki pozostają na dysku,
a archiwum przebiegu zachowuje całą historię testu. Awaria k6, tunelu lub deployu
zatrzymuje przebieg i sprząta procesy pomocnicze, pozostawiając klaster do diagnozy.
Tunel przez API Kubernetes służy pomiarowi ciągłości, nie maksymalnej przepustowości.

Opcjonalna próba integracyjna (resetuje dedykowany namespace `kds-load-check`,
używa istniejącego obrazu v1 i hosta `load-check.test`):

```bash
dotnet run --project tests/KDS.Dashboard.Checks -- --integration-load
```

## Nagrania przebiegów testowych

Historia obejmuje wyłącznie testy uruchomione z dashboardu. Nagranie zaczyna się
bezpośrednio przed tworzeniem namespace’a — budowanie obrazu i usuwanie starego
namespace’a są poza nagraniem. Bez k6 cały przebieg od tej chwili ma limit 120 s
i kończy się po Ready v2. Przekroczenie limitu przerywa polecenia i zapisuje wynik
„Przekroczono czas”; zasoby klastra pozostają do diagnozy. Z k6 zapis obejmuje
całość testu, włącznie z ruchem po rollout, bez limitu 120 s.

Panel **Ruch i deploye** pozwala wybrać nagranie, nadać alias, odtwarzać je w tempie
1×, 2× lub 4× oraz przewijać suwakiem lub kliknięciem wykresu. Kursor synchronizuje
odczyty i topologię podów/ReplicaSetów. Przewijanie zatrzymuje odtwarzanie;
Play wznawia od kursora. Każda karta ma własny kursor. **Na żywo** przywraca bieżący
klaster; oglądanie historii nie przerywa nagrywanego testu.

Domyślna nazwa zawiera release, namespace i timestamp UTC. Alias nie usuwa nazwy.
Archiwum zapisuje się przyrostowo w `artifacts/runs` względem katalogu roboczego
backendu; `Runs:Path` / `Runs__Path` ustawia inny katalog. Metadane i aliasy są
aktualizowane atomowo. Nagrania przetrwają restart, a niedokończone zostaną oznaczone
„Przerwany”. Błędy zapisu/odczytu są widoczne w panelu. Nie ma automatycznej retencji;
długie testy nie są obcinane do godziny. Błędy przygotowania przed tworzeniem
namespace’a pozostają w statusie testu, bez tworzenia nagrania.

Przerwy watch pozostają oznaczone jako brak pomiaru. Requesty/s i błędy/s wynikają
z przyrostów liczników k6; przerwy ponad 5 s nie są interpolowane. Oś pokazuje czas
od początku przebiegu, szczegóły zawierają UTC. Monitoring wymaga list/watch dla
podów, ReplicaSetów i Deploymentów; nie pobiera Kubernetes Events.
