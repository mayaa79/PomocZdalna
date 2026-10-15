# Pomoc Zdalna – plan prac i kontekst projektu

Plik informacyjny: stan projektu, decyzje i lista rzeczy do zrobienia (żeby nie sięgać za każdym razem do pamięci i czatu).

## Cel
Własna aplikacja do pomocy zdalnej (wzorowana na AnyDesk): technik łączy się z komputerem klienta, widzi pulpit,
steruje myszą i klawiaturą oraz wykonuje polecenia (PowerShell / cmd). Aplikacja udostępnia pulpit użytkownika
i pozwala wykonywać na nim polecenia – zawsze za zgodą klienta.

## Decyzje (15.10.2026)
- Technologia: C# .NET 8 + WPF (Windows), serwer ASP.NET Core WebSocket.
- Architektura: serwer pośredniczący (relay) – klient i technik łączą się do serwera (działa za NAT/firewallem).
- Hasło weryfikuje komputer klienta, serwer go nie przechowuje. Zgoda klienta przy każdej sesji z wyborem uprawnień
  (sterowanie, polecenia; podgląd zawsze).
- Jeden plik .exe pełni obie role (klient/technik).
- Zakres MVP: podgląd i sterowanie pulpitem, ID (9 cyfr) + hasło, zgoda, log sesji. Brak preferencji użytkownika
  co do architektury i zakresu – przyjęte wartości domyślne.

## Struktura
- `src/Shared` – protokół (komunikaty JSON + binarne klatki JPEG)
- `src/Relay` – biblioteka relay: RelayHub (blokada brute-force po 5 próbach/10 min, dziennik sesji `sessions-*.log`),
  RelayServer (Start/Stop – serwer uruchamiany z poziomu aplikacji), RelayEndpoints (AddRelay/MapRelay)
- `src/Server` – opcjonalny samodzielny serwer (cienka nakładka na Relay)
- `src/App` – WPF: MainWindow (host + łączenie + ustawienia), ConsentWindow, ViewerWindow (obraz, mysz, klawiatura,
  konsola); `Host/` (ScreenCapturer GDI, InputInjector SendInput, CommandRunner, HostService), `Core/` (RelayClient, Settings)
- Ustawienia aplikacji: `%AppData%\PomocZdalna\settings.json` (adres serwera, ID hosta, nazwa technika)
- Domyślny adres serwera: `ws://localhost:5080/ws`

## Decyzja: serwer wbudowany w aplikację technika (15.10.2026)
- Ze względów finansowych (projekt hobbystyczny) wariant 1: relay działa w tle w aplikacji na laptopie technika
  (domyślnie włączony, ustawienia: `EmbeddedServer`, `ServerPort` = 5080; logi w `%AppData%\PomocZdalna\logs`).
- Klienci łączą się do publicznego adresu laptopa: przekierowanie portu TCP 5080 na routerze + stałe IP lub DDNS;
  zapora Windows musi zezwolić na ruch przychodzący. Laptop wyłączony = brak usługi.
- Adres wpisywany domyślnie w aplikacji: stała `Settings.DefaultServerUrl` (zmienić przed zbudowaniem wersji dla klientów).
- Wariant 2 (osobny VPS 24/7 z TLS) zostaje na później; ten sam kod, `src/Server`.
- Uwaga: bez TLS (`ws://`) hasło i obraz idą otwartym tekstem – przed użyciem przez internet zrobić `wss://`
  (np. certyfikat samopodpisany z przypięciem odcisku w kliencie).

## Stan (15.10.2026)
- ✅ Serwer zbudowany i przetestowany skryptem (rejestracja, zła hasło, zgoda, relay obrazu/poleceń, blokada, log).
- ⚠️ Aplikacja WPF napisana, ale nieskompilowana w środowisku Claude (brak SDK Windows Desktop). Użytkownik buduje
  w Visual Studio i zgłasza błędy.
- Poprawka 15.10: dodano pakiet `System.Drawing.Common` 8.0.10 (błędy CS0012 dla ImageCodecInfo/Graphics).

## Do zrobienia
1. Pierwszy udany build i test na dwóch komputerach w LAN.
2. TLS (`wss://`) – certyfikat samopodpisany z przypięciem w kliencie albo reverse proxy; przekazywanie IP (ForwardedHeaders).
3. Usługa Windows (SYSTEM): dostęp nienadzorowany, okna UAC, ekran blokady, Ctrl+Alt+Del.
4. Transfer plików, schowek, czat.
5. Wiele monitorów, wydajność (kodek wideo H.264 / WebRTC zamiast JPEG), delta klatek.
6. Instalator (Inno Setup, jak w SystemCare) i wydanie jednoplikowe (`dotnet publish -r win-x64`).
7. Trwałe hasło dostępu nienadzorowanego, lista komputerów, E2E.

## Znane ograniczenia
Tylko główny monitor; obraz JPEG ~10 kl./s; konsola poleceń działa z uprawnieniami użytkownika, który uruchomił
aplikację u klienta; bez TLS hasło i obraz idą otwartym tekstem.
