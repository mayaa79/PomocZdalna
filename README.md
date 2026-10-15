# Pomoc Zdalna

Własna aplikacja do pomocy zdalnej (wzorowana na AnyDesk): technik łączy się z komputerem klienta, widzi jego pulpit, steruje myszą i klawiaturą oraz wykonuje polecenia (PowerShell / cmd).

## Architektura

```
 Komputer klienta (host)                 Serwer relay                   Komputer technika
 PomocZdalna.exe  ── wss ──►   PomocZdalna.Server (ASP.NET Core)  ◄── wss ──  PomocZdalna.exe
 - ID (9 cyfr) + hasło                 - przydziela ID                 - ID + hasło klienta
 - okno zgody (uprawnienia)            - przekazuje dane               - podgląd, mysz, klawiatura
 - zrzut ekranu → JPEG                 - blokada brute-force           - konsola poleceń
 - SendInput, wykonanie poleceń        - dziennik audytowy
```

* `src/Shared` – protokół (komunikaty JSON + klatki binarne JPEG).
* `src/Relay` – serwer pośredniczący (WebSocket) jako biblioteka. Nie przechowuje haseł; hasło weryfikuje komputer klienta. Aplikacja technika uruchamia go sama w tle (sekcja „Serwer (wbudowany)”, port 5080).
* `src/Server` – opcjonalny samodzielny serwer (ta sama logika), np. na VPS.
* `src/App` – aplikacja WPF (.NET 8) pełniąca obie role: „Udostępnij ten komputer” i „Połącz z klientem”.

## Przepływ połączenia

1. Klient włącza udostępnianie → dostaje 9-cyfrowe ID (stałe między uruchomieniami) i losowe 8-znakowe hasło.
2. Technik podaje ID + hasło → serwer przekazuje prośbę do klienta.
3. Klient sprawdza hasło, pokazuje okno zgody i wybiera uprawnienia: sterowanie, polecenia (podgląd zawsze).
4. Po zgodzie klient strumieniuje ekran (~10 kl./s), a polecenia/sterowanie są wykonywane tylko w ramach nadanych uprawnień (egzekwowane po stronie klienta).
5. Klient widzi czerwony pasek „Trwa sesja zdalna” i może ją zakończyć w każdej chwili.

## Uruchomienie

Serwer (Windows/Linux, .NET 8):

```
cd src\Server
dotnet run            # domyślnie http://0.0.0.0:5080, adres WebSocket: ws://HOST:5080/ws
```

Aplikacja (Windows, .NET 8 SDK):

```
cd src\App
dotnet run
```

W aplikacji w „Ustawieniach” wpisz adres serwera (`ws://adres:5080/ws`, docelowo `wss://`).
Ten sam plik `.exe` uruchamiasz u siebie i u klienta.

Wydanie jednoplikowe dla klienta:

```
dotnet publish src\App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## Bezpieczeństwo (ważne przed użyciem produkcyjnym)

* **Wystaw serwer przez TLS (`wss://`)** – np. za Caddy/nginx/IIS z certyfikatem albo bezpośrednio w Kestrel. Bez TLS hasło i obraz idą otwartym tekstem.
* Zgoda klienta jest wymagana przy każdej sesji; hasło jest losowe i można je zmienić przyciskiem „Nowe hasło”.
* Serwer blokuje IP po 5 nieudanych próbach w 10 minut. Za reverse proxy skonfiguruj przekazywanie prawdziwego IP (ForwardedHeaders), inaczej limit liczy się dla IP proxy.
* Dziennik audytowy sesji: `logs/sessions-RRRRMMDD.log` obok serwera (start, koniec, kto, z jakiego IP, uprawnienia).
* Konsola poleceń działa z uprawnieniami użytkownika, który uruchomił aplikację u klienta.
* Używaj wyłącznie za wiedzą i zgodą użytkowników; informuj klientów o rejestrowaniu sesji.

## Znane ograniczenia MVP / plan rozwoju

* Okna z podwyższonymi uprawnieniami (UAC), ekran blokady i Ctrl+Alt+Del – wymagają usługi Windows działającej jako SYSTEM (kolejny etap).
* Tylko główny monitor; JPEG pełnoklatkowy zamiast kodeka wideo (H.264 / WebRTC) – wyższe zużycie łącza.
* Brak schowka, transferu plików, czatu; brak E2E (serwer widzi ruch po stronie TLS).
* Brak trwałego hasła „dostępu nienadzorowanego” i panelu zarządzania agentami.
