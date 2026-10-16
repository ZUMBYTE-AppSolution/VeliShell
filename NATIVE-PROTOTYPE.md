# Optionale Fenstergestaltung

Ab VeliShell 0.14.0 enthält das normale GitHub-Paket einen freiwilligen nativen
Adapter für kompatible klassische 64-Bit-Apps. Das Modul ist **standardmäßig aus**.
Es ersetzt keine Windows-Systemdateien, keine Shell und nicht das Innere fremder Apps.

Der Arbeitsstand **0.14.1-test.1 auf `test`** ergänzt klassische 32-Bit-Apps, WPF
mit Standardrahmen und einen gemeinsamen Glasrahmen rund um das Fenster. Die folgende
Anleitung beschreibt diesen Teststand. Die veröffentlichte 0.14.0 gestaltet weiterhin
nur klassische x64-Titelbereiche; ein lokaler Teststand ist noch kein Kanal-Update.

## Aktivieren

1. Zuerst in einer VM mit Snapshot testen. Windows 11 ab Build 22621, x64,
   eingeschaltete Transparenzeffekte und funktionierende Grafikbeschleunigung sind
   für Live-Glas erforderlich. Windows und der Grafiktreiber bestimmen die Effektqualität.
2. Installer oder das vollständig entpackte portable Paket verwenden. SDK,
   Visual Studio oder eine zusätzliche C++-Runtime sind zum Start nicht erforderlich.
3. **Einstellungen → Windows & Start → Fenstergestaltung verwalten** öffnen.
4. Spiele, Anti-Cheat-geschützte und kritische Anwendungen **vorher** in die
   Ausnahmeliste eintragen. Speichere deine Arbeit. Nicht jede Sonderanwendung ist
   automatisch erkennbar; es gibt keine allgemeine Verträglichkeitszusage.
5. **Automatisch auf kompatible Apps anwenden** einschalten und den Hinweis bestätigen.
   Danach werden bestehende und später gestartete geeignete Fenster automatisch
   erfasst, ohne jedes Fenster einzeln freizugeben. Bei einem normalen VeliShell-Ende
   werden die Rahmen zurückgesetzt; beim nächsten normalen Start darf die gespeicherte
   Zustimmung wieder aufgenommen werden. Nach einem Notfall gilt stattdessen die Sperre unten.

Wenn Stable parallel installiert ist, dort die Fenstergestaltung zuerst deaktivieren
und die Rücksetzung abwarten. Beide Ausgaben beanspruchen denselben echten Notfall-Hotkey;
sie sollen nicht gleichzeitig fremde Apps gestalten. Zwischen unterschiedlichen
Adapterversionen betroffene Apps selbst schließen, damit keine ältere inaktive DLL
geladen bleibt. Der eigene isolierte QA-Modus benötigt diesen Wechsel nicht.

Die Ausnahmeliste akzeptiert einen EXE-Namen wie `beispiel.exe` oder einen vollständigen
EXE-Pfad. Der Name gilt auch nach einem App-Update mit geändertem Installationspfad.
Ein neuer Ausschluss setzt bereits gestaltete Fenster der betroffenen App zurück;
die App bleibt geöffnet. Nach Entfernen eines Ausschlusses darf sie wieder erfasst werden.

## Welche Fenster werden erfasst?

Sichtbare, nicht minimierte klassische x86-/x64-Hauptfenster mit Standardrahmen in derselben
Benutzersitzung und mit mittlerer Berechtigungsstufe. Vor der Einbindung werden Prozess,
Startzeit, EXE-Hash, Fenster und UI-Thread geprüft. Ein kurzer, auf genau diesen Thread
begrenzter DLL-Hook dient zum Bootstrap und wird anschließend entfernt. Die native
Unterklasse arbeitet nur am jeweiligen Fenster. Globale Hooks, Dienste und automatische
Rechteerhöhung gibt es nicht.

WPF wird nicht pauschal ausgeschlossen: Die native Titelhöhe und der Beginn des
App-Inhalts müssen zu einem echten Standardrahmen passen. Für 32-Bit-Ziele startet
ein kleiner nativer x86-Helfer; der Hauptwächter bleibt x64. Beide Architekturen
verwenden passende DLLs und dieselben Zustimmungs-, Identitäts- und Rücksetzprüfungen.

Vom vollständigen Rahmenersatz ausgeschlossen bleiben erhöhte Apps, Systemfenster,
Explorer, Browser, WPF-/WinUI-Sonderrahmen, geschützte Hosts, bekannte Spiel-Fensterklassen
sowie Fenster mit eigener Region oder ungeeigneten Fensterstilen.
Das Modul ist experimentell; die Erkennung ersetzt keinen
Kompatibilitätstest mit deiner konkreten App. Die Aufnahme ist begrenzt und gestaffelt,
damit viele gleichzeitig geöffnete Fenster nicht auf einmal eingebunden werden.

## Glas und Bedienung

Windows Composition liefert den live weichgezeichneten Hintergrund; Direct2D und
DirectWrite zeichnen Titel und linke Fensterbuttons. Farben kommen aus VeliShells
Hell-/Dunkel-Theme, Matt-/Glas- und Transparenzwerte werden übernommen. Eine begrenzte
Deckkraft schützt die Lesbarkeit des Titels.

Der Glasrahmen umfasst Titel, Seiten, Unterkante und alle vier Rundungen innerhalb
der ursprünglichen Fenstergrenzen. Sein eigener Kompositions-Host gehört zur Ziel-App;
ein ausgesparter Innenbereich hält Menü und App-Inhalt frei. Die Fensterregion wird
vorübergehend angepasst, nicht die logische Client-Geometrie. Kein abstehender
Aufsatz und keine Desktop-Bildkopie. Menü und Clientbereich behalten ihre ursprüngliche
Geometrie und Gestaltung. Der Adapter ersetzt also **nicht** sämtliche Steuerelemente
oder die Oberfläche fremder Programme. Die Fensterregion wird beim Rücksetzen entfernt,
sofern sie noch unsere ist; eigene zwischenzeitliche Änderungen der App werden nicht überschrieben.

Minimieren, Maximieren/Wiederherstellen, Schließen, Ziehen und das Windows-Systemmenü
bleiben vorhanden. **Alt + Leertaste** bietet das tastaturbedienbare Fenstermenü.
Ein zusätzlicher UI-Automation-Anbieter für die linken nativen Buttons ist noch offen.

## Explorer, WinUI und eigene Titelleisten

**Explorer & eigene Titelleisten: native Farbanpassung** ist eine getrennte,
standardmäßig ausgeschaltete Freigabe. Sie benötigt zusätzlich die Hauptautomatik
und berücksichtigt dieselbe Ausnahmeliste. Es wird keine DLL in diese Apps geladen.
Ein unabhängiger Helfer sichert verfügbare DWM-Werte und passt Rahmen-/Titelfarben,
Rundung und das verfügbare Windows-Material an. Die Glasregler wählen dabei einen
Windows-Materialtyp, keine frei steuerbare Composition-Weichzeichnung.

Tabs, Suchfelder, eigene Buttons und App-Inhalte bleiben unverändert. Die Buttons
bleiben rechts, wenn die App sie selbst zeichnet. Manche Apps verdecken diese
Windows-Materialien vollständig. Die Anzeige unterscheidet deshalb ausdrücklich
**Native Farbanpassung** von einem vollständigen Glasrahmen mit linken Buttons.
Es gibt keine Zusage für alle Windows-11- oder WPF-Fenster und keinen Eingriff in
geschützte Sicherheitsdialoge, Taskleiste, Desktop oder Systemdateien.

Beim Ausschalten wird zurückgesetzt. Ändert eine App ihre DWM-Werte zwischenzeitlich
selbst, überschreibt die Wiederherstellung ihre neuere Änderung nicht. Der Helfer
überwacht den Wächter; bei einem Helferabsturz kann der Wächter die gesicherten Werte
selbst zurücksetzen. Ein unerwarteter Helferausfall stoppt die gesamte Aufnahme.

## Rollback und unabhängiger Notfall-Rückweg

- **Deaktivieren & Windows-Rahmen wiederherstellen** schaltet die Aufnahme aus und
  fordert das Rücksetzen aller erfassten Fenster an. Es werden keine fremden Apps beendet.
- **Strg + Alt + Umschalt + F12** wird von einem unabhängigen Hilfsprozess empfangen.
  Ist dieses Kürzel belegt, bleibt das Modul aus.
- `WINDOW-RECOVERY.cmd`, `VeliShell.exe --restore-native-prototype` oder der
  **VeliShell Notfall-Wiederherstellung**-Startmenüeintrag fordert denselben Rückweg an,
  auch wenn die normale VeliShell-Oberfläche nicht läuft oder nicht reagiert.
- Fällt die normale VeliShell-App aus, fordert der Helfer das Rücksetzen an. Fällt
  der Helfer aus, erkennt die native DLL den fehlenden Heartbeat und setzt auf dem
  Ziel-UI-Thread zurück. Eigene Wiederherstellungs-Timer ersetzen keine Timer der App.
- Nach Notfall oder unvorhergesehenem Ausfall verhindert eine lokale Sicherheitssperre
  die automatische erneute Aktivierung. Du musst das Modul bewusst erneut einschalten.
- **Grenze:** Eine vollständig hängende Ziel-App muss erst wieder ihre Fensternachrichten
  verarbeiten. Bis dahin bleibt das Rücksetzen ausstehend. Responsive Fenster werden
  unabhängig davon zurückgesetzt. Es gibt keine Erfolgsmeldung für noch aktive Rahmen
  und kein automatisches Abschießen oder erzwungenes Entladen fremder Prozesse.

Die DLL bleibt nach dem Rollback inaktiv geladen, bis du die jeweilige Ziel-App selbst
schließt. Deshalb müssen betroffene Apps vor VeliShell-Updates geschlossen werden;
der Updater weist darauf hin. Der Installer verwendet kein automatisches Schließen
fremder Apps durch Restart Manager. Bei direktem Installieren oder Deinstallieren
ebenfalls zuerst das Modul deaktivieren und die betroffenen Apps selbst schließen.

Lokale Journale enthalten Prozessidentität und Rücksetzstatus. Windows stellt nur den
wirksamen NC-Rendering-Zustand lesbar bereit, nicht den ursprünglichen internen
Policy-Enumwert. Wiederhergestellt werden der wirksame Rendering-Zustand und die
gelesenen, von uns veränderten DWM-Werte; keine bytegenaue Kopie aller internen App-Einstellungen.

## Mitgeliefertes Beispielfenster und Testkanal

`VeliShell.ClassicSample.exe` und `VeliShell.ClassicSample.x86.exe` sind unabhängige klassische Win32-Apps mit Menü und
Textfeld, ohne eingebaute VeliShell-Anbindung. Damit kannst du das Modul zuerst gefahrlos
in einer VM ausprobieren. `VeliShell.exe --native-windows-preview` öffnet eine gesonderte
Testverwaltung: Diese darf ausschließlich bytegleiche ClassicSample-Prozesse aus dem
eigenen Paketordner und ausdrücklich markierte, eigene WPF-Beispielfenster gestalten,
niemals deine anderen Apps. `VeliShell.exe --native-wpf-fixture standard` bzw. `custom`
startet solche temporären WPF-Beispiele ohne Speichern von Einstellungen.
Der isolierte QA-Modus registriert kein globales Notfallkürzel, um eine laufende
Installation nicht zu stören; er prüft Rücksetzsignal und Helfer-Ausfall separat.
Er startet weder Dock noch
Menüleiste und speichert keine globale Aktivierungszustimmung.

Unter **Info & Lizenzen → Update-Kanal** wählst du Stable oder Test. Test verwendet
ausschließlich veröffentlichte, nummerierte `-test.N`-Pakete des Branches `test`.
Changelog, SHA-256-Prüfung und gesonderte Installationsbestätigung bleiben erhalten.
Die Test-MSI installiert separat nach `Programme\Zumbyte\VeliShell Test` mit Profil
`%LOCALAPPDATA%\VeliShell-Test`, ohne stabilen Autostart oder Explorer-Befehl zu ersetzen.
Der Rückwechsel auf Stable ist ein Wechsel zwischen getrennten Produkten, kein
MSI-Downgrade. Die Test-Installation bleibt bis zur manuellen Deinstallation bestehen.

Das automatische Modul ist in Store-Paketen derzeit deaktiviert. Die Veröffentlichung
0.14.0 erfolgt ausschließlich auf GitHub, ohne neue Microsoft-Store-Einreichung.
