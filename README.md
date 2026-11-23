# F3 Window Peek

Podgląd okien Windows działający podczas przytrzymania F3, podobnie do Mission Control.

- **Przytrzymaj F3** — okna płynnie zmniejszają się do układu miniatur, od razu po naciśnięciu.
- **Puść F3** — zamyka podgląd i wraca do poprzedniego okna.
- **Kliknij miniaturę podczas trzymania F3** — powiększa wybrane okno z miniatury do jego rzeczywistej pozycji, a potem przekazuje mu fokus. Puszczenie F3 w trakcie nie przerywa wyboru.
- **Najedź na miniaturę i kliknij ×** — zamyka okno standardową prośbą do aplikacji. Pozostałe miniatury pozostają na swoich miejscach. Jeśli aplikacja potrzebuje np. potwierdzenia zapisu, podgląd odsłania jej okno lub dialog.
- **Kliknij pulpit na dolnym pasku** — przełącza bezpośrednio na wybrany pulpit wirtualny. Aktywny pulpit jest oznaczony jasną ramką.
- **Przeciągnij miniaturę okna na pulpit na dole** — przenosi okno na wskazany pulpit, pozostawiając Cię na bieżącym pulpicie. Miniatura podąża za kursorem również między monitorami; cel podświetla się jasną ramką. Upuszczenie poza paskiem albo Escape anuluje przeniesienie.
- **Prawy przycisk ikony w zasobniku** — pauza lub zakończenie programu. Dwuklik przełącza pauzę.
- **Szybki podgląd / Widok Windows** w menu zasobnika — wybór własnego widoku DWM albo poprzedniego Win+Tab. Wybór zostaje zapisany w `mode.txt`.

Zwykły F3 przejmuje ten program. Kombinacje z Ctrl, Shift, Alt i Win zachowują funkcje aplikacji. Ręcznie otwarty Win+Tab pozostaje otwarty po naciśnięciu i puszczeniu F3.

## Uruchomienie

Uruchom `F3WindowPeek.exe`. Program nie wyświetla konsoli i dopuszcza jedną instancję na sesję użytkownika.

Program jest pojedynczym plikiem EXE napisanym w C# dla systemowego .NET Framework. Nie potrzebuje plików źródłowych, AutoHotkey ani dodatkowej biblioteki do pulpitów. Opcjonalny `mode.txt` przechowuje wybór trybu podglądu.

`install.ps1` uruchamia program i dodaje skrót do Autostartu bieżącego użytkownika. Opcja `-NoStartup` tylko uruchamia program. `uninstall.ps1` zatrzymuje program i usuwa wyłącznie jego skrót z Autostartu. Zmiana lokalizacji folderu programu wymaga ponownego utworzenia skrótu.

Nie wymaga uprawnień administratora, AutoHotkey ani instalowania dodatkowego środowiska — korzysta z .NET Framework 4.x dostarczanego z Windows.

## Działanie i ograniczenia

Domyślny szybki podgląd korzysta z żywych miniatur DWM. Przygotowuje okna docelowe, tło i układ wcześniej, poza obsługą F3. Otwarcie trwa 345 ms, zamknięcie 255 ms; animacja zaczyna się od rzeczywistych pozycji okien i może zmienić kierunek w trakcie. Nieliniowa krzywa Béziera `(0.16, 1, 0.18, 1)` nadaje oknom szybki start i długie, miękkie wyhamowanie. Tło i podpisy są buforowane: w kolejnych klatkach aktualizowane są tylko prostokąty miniatur w DWM. Zegar wysokiej rozdzielczości budzi się co 5 ms wyłącznie podczas ruchu; w spoczynku jego wątki śpią i nie zmieniają globalnej rozdzielczości zegara Windows.

Każdy monitor ma osobny podgląd. Główna siatka pokazuje okna bieżącego pulpitu; dolny pasek pokazuje wszystkie pulpity wirtualne, ich nazwy, miniatury okien i aktywny pulpit. Kliknięcie pulpitu przełącza go z animacją systemową. Zminimalizowane aplikacje mają kafelek z ikoną, bo DWM nie udostępnia ich bieżącej zawartości. Lista okien jest odświeżana co 750 ms, gdy podgląd jest zamknięty. Widok nie zmienia rzeczywistych rozmiarów ani pozycji aplikacji. Kliknięcie miniatury przełącza do oryginalnego okna.

Animacja wyboru trwa 255 ms i używa tej samej nieliniowej krzywej co pozostałe przejścia. Wybrana miniatura jest przenoszona nad pozostałe. Rzeczywiste okno dostaje fokus przed zdjęciem ostatniej klatki nakładki, aby uniknąć mignięcia poprzedniej aplikacji. Okno zminimalizowane jest najpierw przywracane pod podglądem. Zamknięcie przez × wysyła `WM_CLOSE`, bez kończenia procesu i bez automatycznego odpowiadania na pytania aplikacji.

Przeciąganie zaczyna się po przekroczeniu systemowego progu ruchu myszy, więc zwykły klik nadal powiększa wybrane okno. Upuszczenie używa interfejsu widoków powłoki do przenoszenia także okien innych procesów. Nie zmienia ich rozmiaru ani położenia. Po przeniesieniu odświeżają się miniatury pulpitów, a pozostałe okna w głównej siatce pozostają na swoich miejscach. Puszczenie F3 podczas przeciągania pozwala dokończyć upuszczenie, a następnie zamyka podgląd.

Obsługa wyboru pulpitów używa interfejsów powłoki Windows 11 24H2. Przetestowano przełączenie między oboma istniejącymi pulpitami i powrót. Definicje COM pochodzą z projektu MScholtes/VirtualDesktop na licencji MIT; źródło i licencja są w `third_party`. Po dużej aktualizacji Windows ten prywatny interfejs może wymagać aktualizacji; wariant Win+Tab pozostaje dostępny.

Wariant **Widok Windows** otwiera systemowy Win+Tab, wraz z paskiem pulpitów. Ten wariant zachowuje systemowe opóźnienia i animacje.

Rozpoznawanie systemowego podglądu obsługuje polski i angielski tytuł Widoku zadań, sprawdza klasę okna oraz proces powłoki. Przetestowano na Windows 11 / build 26100, na dwóch monitorach. Inne wersje Windows wymagają osobnej weryfikacji. Nie działa na ekranie blokady ani bezpiecznym pulpicie UAC.

Obsługuje powtarzanie klawisza, szybkie tapnięcie przed zakończeniem animacji, ponowne naciśnięcie podczas zamykania i zmianę uchwytu HWND przez Explorer. Escape wysyła tylko do aktywnego podglądu. Jeżeli przy puszczeniu F3 trzymany jest modyfikator, zamknięcie czeka na jego zwolnienie, aby nie wysłać np. Ctrl+Esc. Oczekiwanie na otwarcie ma limit 3 sekund.

Hook sprawdza F3, stan modyfikatorów i Escape służący do zamknięcia szybkiego podglądu. Nie zapisuje klawiszy. Escape nie jest wysyłany do aplikacji w szybkim trybie. Skróty z Alt i Win zamykają nakładkę i działają normalnie.

## Budowanie i sprawdzanie

```powershell
.\build.ps1
.\test.ps1
```

`PreviewSessionTests.cs` sprawdza przypadki czasowe i utratę aktywności bez wysyłania klawiszy do pulpitu. `PreviewLayoutTests.cs` sprawdza 50 układów ekranów, proporcje miniatur i punkty początkowe oraz końcowe interpolacji.

`test.ps1 -Desktop` dodatkowo sprawdza na własnych pustych oknach testowych animowany wybór, przywracanie zminimalizowanego okna, puszczenie F3 podczas wyboru, anulowanie oraz zwykłe zamknięcie i odmowę zamknięcia przez aplikację. Osobny proces testowy sprawdza przeciągnięcie między monitorami i pulpitami, anulowanie, zachowanie bieżącego pulpitu oraz zwykły klik po zmianie obsługi myszy. Testy zapisują wynik w `selection-verified.log`; przed ich uruchomieniem należy zatrzymać normalną instancję programu.

Test integracyjny `F3WindowPeek.exe --demo 3000 --log C:\pelna\sciezka\test.log` symuluje F3 przez ten sam hook, przez 3 sekundy utrzymuje podgląd, wysyła powtórzenia i puszcza F3. Krótkie tapnięcie sprawdza `--demo 25`. Program testowy następnie się zamyka. Test na początku zamyka ewentualny aktywny Widok zadań pozostawiony przez wcześniejszą próbę. Przed testem trzeba zakończyć już uruchomioną instancję (`F3WindowPeek.exe --stop`).

Log jest opcjonalny i służy wyłącznie diagnostyce. Zawiera zmiany stanu oraz tytuł, klasę i uchwyt aktywnego okna. Zwykłe uruchomienie nie tworzy logu.

Opcje `--fast` i `--native` wybierają tryb na czas uruchomienia. `--demo-count 3` powtarza test trzy razy. `--inspect` udostępnia okna podglądu do narzędzi kontroli wizualnej; zwykły tryb ukrywa je z Alt+Tab i paska zadań.

Pokazanie przygotowanych okien w pomiarach zajmowało około 19–28 ms. Fizyczną częstotliwość animacji ogranicza ekran i kompozytor; zegar programu aktualizuje miniatury co około 5 ms.

`--verify-desktops <pełna-ścieżka-logu>` przełącza na drugi istniejący pulpit i wraca, weryfikując jego identyfikator. Nie tworzy ani nie usuwa pulpitów.

Źródła API: [DWM thumbnails](https://learn.microsoft.com/en-us/windows/win32/dwm/thumbnail-ovw), [wirtualne pulpity](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ivirtualdesktopmanager-iswindowoncurrentvirtualdesktop), [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc), [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
