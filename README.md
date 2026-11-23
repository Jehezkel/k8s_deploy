# K8s Deploy Simulator

Lokalne testowanie dostępności TargetApp przed, podczas i po rollout w Kubernetes.

```text
k6 na laptopie → http://target.test → Traefik Ingress → Service → 3 Pody TargetApp
Dashboard lokalnie → Kubernetes API
scripts/deploy.sh → Helm → aktualizacja Deploymentu
```

Chart obejmuje wyłącznie TargetApp. Dashboard i k6 działają poza klastrem,
więc aktualizacja lub usunięcie środowiska testowego ich nie wyłącza.
Obecny dashboard odczytuje Pody przy otwarciu strony; ten setup nie dodaje
automatycznego odświeżania diagramu ani przycisków uruchamiania skryptów.

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
Namespace musi już istnieć, dlatego najpierw wykonaj deploy.

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
