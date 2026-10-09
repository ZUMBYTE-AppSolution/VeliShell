<p align="center">
  <img src="src/VeliShell.Desktop/Assets/VeliShellApp.png" width="132" alt="VeliShell App-Icon">
</p>

<h1 align="center">VeliShell</h1>

<p align="center">
  Ein ruhiges, frei anpassbares Dock für Windows – mit macOS-inspirierter Bedienung,<br>
  sauberer Fensterintegration und einem Design, das sich nicht in den Vordergrund drängt.
</p>

<p align="center">
  <a href="https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/latest/download/VeliShell-Setup-win-x64.msi"><strong>⬇️ Installer herunterladen</strong></a>
  ·
  <a href="https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/latest/download/VeliShell-Portable-win-x64.zip">📦 Portable Version</a>
  ·
  <a href="https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/latest">📝 Neueste Version</a>
</p>

<p align="center">
  <img alt="Windows 10 und 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-5B8DEF?style=flat-square">
  <img alt="Aktuelle Version" src="https://img.shields.io/github/v/release/ZUMBYTE-AppSolution/VeliShell?style=flat-square&label=Version">
  <img alt="Build" src="https://img.shields.io/github/actions/workflow/status/ZUMBYTE-AppSolution/VeliShell/ci.yml?branch=main&style=flat-square&label=Windows%20Build">
</p>

---

## ✨ Was VeliShell ausmacht

VeliShell ersetzt nicht die Windows-Shell. Es ergänzt den Desktop um ein Dock, das sich vertraut anfühlt und trotzdem Windows respektiert.

- **Einheitliche App-Icons:** Jedes Symbol belegt exakt dieselbe eingestellte Fläche. Transparente Ränder oder ungewöhnliche Quelldateien verändern die Größe im Dock nicht.
- **Gemeinsame Rundung für Apps:** App-Icon, geladene App-Symbole, Hover-Zustand und Drag-Ghost verwenden dieselbe kontinuierliche Superellipse. Ordner und Papierkorb behalten dagegen ihre Originalform.
- **Drag & Drop wie erwartet:** Programme, Dateien und Ordner lassen sich vom Desktop ins Dock ziehen und dort verschieben. Ziehst du einen Eintrag aus dem Dock heraus, wird er nur aus dem Dock entfernt – VeliShell erstellt dabei keine Verknüpfung. Der Ghost sitzt direkt am künftigen Einfügeplatz.
- **Direkt aus dem Explorer anheften:** Dateien und Ordner erhalten den statischen Kontextmenü-Befehl **„Im Dock anheften“**. Unter Windows 11 kann er im klassischen Bereich **„Weitere Optionen anzeigen“** stehen; VeliShell lädt dafür keinen Erweiterungscode in den Explorer.
- **Ordner direkt im Dock durchsuchen:** Für jeden echten Ordner wählst du zwischen Liste, Raster und App-Launcher. Die Ansicht hat Zurück-Navigation, Breadcrumbs und „Im Explorer öffnen“; sie bleibt auf den angehefteten Stammordner begrenzt und folgt keinen Junctions oder symbolischen Ordnerlinks hinaus.
- **Virtuelle App-Ordner:** Mehrere Dock-Apps lassen sich in einem eigenen VeliShell-Ordner bündeln. Du kannst Apps hineinziehen, umbenennen oder wieder einzeln ins Dock zurücklegen. Dabei werden keine Dateien oder Windows-Verknüpfungen verschoben.
- **Fenster auf einen Blick:** Laufende Apps erhalten einen einzelnen Punkt. Bei mehreren Fenstern zeigt das Kontextmenü seitlich eine Live-Miniatur des gerade berührten Eintrags.
- **Suche ohne Umweg:** VeliShell findet Dock-Apps, Startmenü-Einträge, registrierte Programme, installierte Web-Apps und geöffnete Anwendungen schon beim Tippen. Die Websuche öffnet erst nach deinem Klick den Browser; eine Live-Webabfrage gibt es nicht.
- **Web-Apps als eigene Programme:** Installierte Chrome- und Edge-Web-Apps aus Startmenü oder Desktop behalten ihren Verknüpfungsnamen und ihr eigenes Symbol. Laufende Fenster werden anhand ihrer Windows-App-ID zugeordnet, nicht pauschal dem Browser zugeschlagen.
- **Steam ohne doppelten WebHelper:** Steam-Oberflächen im `steamwebhelper.exe` werden dem Steam-Eintrag zugeordnet, wenn die zugehörige Steam-Installation sicher erkannt wird.
- **Steam in der Menüleiste:** Läuft Steam nur im Hintergrund, bleibt sein echtes Symbol aus der installierten `steam.exe` sichtbar – für die Leiste lediglich entsättigt. Linksklick öffnet Steam; Rechtsklick bietet Öffnen, Einstellungen und Beenden nach Bestätigung. Ein bloßer SteamWebHelper ohne Steam-Client erscheint nicht.
- **Andere Hintergrund-Apps in der Menüleiste:** VeliShell merkt sich Apps, deren Fenster es bei aktivierter Menüleiste gesehen hat. Schließt sich das letzte Fenster, während derselbe Prozess weiterläuft, erscheint sein lokal installiertes App-Symbol rechts in der Leiste. Linksklick öffnet die App; Rechtsklick bietet Öffnen, Speicherort und – falls ein Schließfenster vorhanden ist – reguläres Beenden. „Sofort beenden …“ erfordert eine Warnungsbestätigung und beendet ausschließlich den geprüften Prozess, nicht seine gesamte Prozessgruppe. Das gilt auch für Apps im `+N`-Menü. Explorer, Windows-Shell-Hosts, Helferprozesse und reine Dienste werden nicht als Apps angezeigt. Bereits vor dem Start von VeliShell geschlossene Fenster können nicht rückwirkend erkannt werden.
- **Eigenes Startmenü am Dock-Button:** Der optionale Start-Knopf öffnet VeliShells App-Launcher direkt über seiner Position im Dock. Dort kannst du lokale Programme suchen, starten oder per Drag & Drop ins Dock ziehen. Das reguläre Windows-Startmenü bleibt über einen separaten Knopf erreichbar; nicht alle Windows-Start-Funktionen sind bereits nachgebaut.
- **Sanfte Startanimation:** Das angeklickte App-Symbol hüpft kurz, solange das Programm startet. Mit „Bewegung reduzieren“ entfällt diese Animation.
- **Jedes Dock-Icon anpassbar:** Angeheftete und aktuell laufende Apps sowie das feste VeliShell-Symbol lassen sich einzeln ersetzen und zurücksetzen. Für den Papierkorb können Leer- und Vollzustand getrennt gestaltet werden.
- **Papierkorb im Dock:** Leer- und Vollzustand haben eigene Symbole; der Papierkorb lässt sich direkt über das Dock leeren.
- **Milchiger Hintergrund:** Helles und dunkles Design, eine bewusst leichtere Glas-Transparenz für Dock und Menüleiste sowie eine zurückhaltende Vergrößerung beim Darüberfahren.
- **VeliShell-Farbwelt:** Einstellungen, Info und Update-Dialog passen sich mit gut lesbaren Blau-Cyan-Violett-Verläufen, sanftem Glow und eigener Hell-/Dunkelabstimmung an das Markensignet an.
- **Skalierbar:** Die Icongröße lässt sich von 32 bis 96 DIP einstellen und bleibt auch bei Windows-Skalierung sauber zentriert.
- **Windows-Taskleiste optional ausblenden:** Nur nach ausdrücklicher Aktivierung. Der freigegebene Arbeitsbereich reicht wirklich bis zum Bildschirmrand – ohne reservierten schwarzen Streifen – und wird beim Einblenden, Beenden oder nach einem unterbrochenen Lauf wiederhergestellt.
- **Desktopsymbole optional ausblenden:** Ebenfalls nur nach ausdrücklicher Aktivierung und ausschließlich für die Laufzeit von VeliShell. Es wird keine persistente Explorer- oder Registry-Einstellung verändert; ein Wiederherstellungsjournal schützt den zuvor sichtbaren Zustand nach einem unterbrochenen Lauf.
- **VeliShell-Symbol ist optional:** Das feste Einstellungssymbol lässt sich über sein Dock-Menü entfernen und unter **Einstellungen → Apps & Symbole** jederzeit wieder hinzufügen.
- **VeliShell-Menüleiste mit zwei Centern:** App-Menü, offene Fenster, Uhr und Statusanzeigen werden durch ein milchiges Kontrollzentrum für Netzwerk, Bluetooth, Fokus, Anzeige, Energie, Lautstärke und Stummschaltung ergänzt. Ein Klick auf den Ton-Indikator zeigt erkannte Ausgabegeräte samt eigener Lautstärke und Stummschaltung; das Windows-Standardgerät wählst du weiterhin in den Windows-Toneinstellungen. Die Mitteilungszentrale sammelt VeliShell-Hinweise und besitzt einen ausdrücklich freizugebenden Adapter für aktuelle Windows-Mitteilungen. Dieser Windows-Zugriff verlangt eine signierte Paketidentität; in der derzeitigen MSI-/Portable-Ausgabe bleibt er deshalb sicher deaktiviert. Die Leiste reserviert ihren oberen Bildschirmbereich, sodass maximierte Fenster nicht darunterrutschen.
- **Scharfe Vektorsymbole:** Menüleiste, Center-Panels und Einstellungsnavigation verwenden einen eigenen VeliShell-Symbolsatz auf gemeinsamem 24-Punkt-Raster. Er skaliert verlustfrei, übernimmt automatisch das aktive Theme und bündelt keine Apple-SF-Symbol-Dateien.
- **Deutsch, Englisch oder Systemsprache:** Die Sprache kann jederzeit in den Einstellungen gewechselt werden.
- **Geführter erster Start:** Sechs erklärende Schritte führen durch Design, Dock, Symbole, Online-Dienste, Windows-Optionen und Updates. Für den optionalen macOSicons-Schlüssel gibt es eine direkte Anleitung und ein geschütztes Eingabefeld; freiwillige Systemeingriffe bleiben zunächst aus.

## 🚀 Installation

1. Den [aktuellen VeliShell-Installer](https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/latest/download/VeliShell-Setup-win-x64.msi) herunterladen.
2. Die MSI-Datei öffnen, die Windows-Administratorabfrage bestätigen und den gebrandeten VeliShell-Dialogen folgen.
3. VeliShell über das Startmenü starten und die sechs Schritte der Ersteinrichtung durchgehen. Alle Optionen bleiben danach in den Einstellungen erreichbar.

Für einen Test ohne Installation gibt es zusätzlich eine [portable ZIP-Datei](https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/latest/download/VeliShell-Portable-win-x64.zip). Sie muss vollständig entpackt werden; die EXE allein reicht nicht aus.

Die erste Microsoft-Store-Ausgabe wird als MSIX im Partner Center eingereicht. Das MSIX auf der GitHub-Release-Seite ist nur für diese Einreichung bestimmt und nicht für eine direkte Installation signiert. Erst nach erfolgreicher Zertifizierung stellt Microsoft die signierte Store-Version bereit; der MSI-Installer und die portable ZIP bleiben unabhängig davon auf GitHub verfügbar.

> [!IMPORTANT]
> Die ersten Releases können noch ohne kostenpflichtiges Herausgeberzertifikat erstellt sein. Windows SmartScreen kann deshalb warnen. Die Release-Seite enthält SHA-256-Prüfsummen und eine von GitHub erzeugte Build-Herkunftsbestätigung. Sicherheitsfunktionen von Windows müssen dafür nicht abgeschaltet werden.

## 🧭 Die wichtigsten Handgriffe

| Aktion | Bedienung |
|---|---|
| Einstellungen öffnen | VeliShell-Icon im Dock oder `Strg` + `Alt` + `V` |
| Programme suchen | `Win` + `Leertaste`, sofern Windows das Kürzel freigibt; sonst `Strg` + `Alt` + `Leertaste` oder das Dock-/Menüleisten-Menü |
| VeliShell-Startmenü öffnen | Optionales Start-Symbol links im Dock; es öffnet sich direkt über dem Knopf. Aktivierbar unter **Einstellungen → Apps & Symbole** |
| Windows-Startmenü öffnen | Knopf **Windows-Start** im VeliShell-Startmenü |
| App anheften | Datei, Ordner oder Verknüpfung auf das Dock ziehen |
| Aus dem Explorer anheften | Rechtsklick auf Datei oder Ordner → **Im Dock anheften**; unter Windows 11 gegebenenfalls zuerst **Weitere Optionen anzeigen** |
| Angehefteten Ordner öffnen | Ordner-Symbol anklicken; Darstellung per Rechtsklick wählen, Unterordner öffnen oder zum Explorer wechseln |
| Virtuellen App-Ordner erstellen | **Einstellungen → Apps & Symbole** oder Dock-Kontextmenü; danach Apps auf den Ordner ziehen |
| Reihenfolge ändern | Icon im Dock an die gewünschte Position ziehen |
| Eintrag aus dem Dock entfernen | Dock-Icon aus dem Dock herausziehen |
| Mehrere Fenster auswählen | Rechtsklick auf die laufende App |
| Papierkorb leeren | Rechtsklick auf den Papierkorb |
| Taskleiste im Notfall einblenden | `Strg` + `Alt` + `Umschalt` + `F11` |
| VeliShell beenden | Dock-Menü oder `Strg` + `Alt` + `Umschalt` + `Q` |

## 🔄 Updates ohne Überraschungen

VeliShell fragt GitHub ausschließlich nach der neuesten veröffentlichten Version. Das Verhalten bestimmst du selbst: nur manuell prüfen, bei neuen Versionen benachrichtigen oder das geprüfte Paket automatisch herunterladen. Das vollständige Changelog bleibt dabei sichtbar; die Installation beginnt nie von allein.

Ist das an die öffentliche IP gebundene GitHub-API-Limit ausgeschöpft, wechselt die Prüfung automatisch auf die offizielle Release-Seite und deren Feed. Release-Tag, Changelog, Dateigröße und SHA-256-Prüfsumme werden dabei weiterhin ausschließlich aus taggebundenen Quellen des offiziellen Repositories zusammengesetzt und gegengeprüft; ein eigener GitHub-Schlüssel ist dafür nicht nötig.

- automatische Downloads nur nach ausdrücklicher Auswahl in den Einstellungen;
- keine unbeaufsichtigte Installation;
- Größen- und SHA-256-Prüfung vor dem Start des Installers;
- deutlicher Hinweis, wenn ein Paket noch keine gültige Windows-Herausgebersignatur besitzt;
- nach dem Download eine eigene Bestätigung vor der Installation und automatischer VeliShell-Neustart nach dem MSI;
- jederzeit auch manuell über **Einstellungen → Info → Nach Updates suchen**.

Die Versionshistorie steht in [CHANGELOG.md](CHANGELOG.md). Jede stabile Version erhält zusätzlich einen eigenen Eintrag unter [GitHub Releases](https://github.com/ZUMBYTE-AppSolution/VeliShell/releases).

## 🖥️ Autostart

Der freiwillige Benutzer-Autostart startet das sichtbare Dock nach deiner Windows-Anmeldung. Er wird ausschließlich für dein Benutzerkonto eingerichtet, benötigt keine Administratorrechte und kann in VeliShell jederzeit wieder vollständig entfernt werden. Die Updateprüfung läuft – abhängig von deiner Auswahl – direkt in der angemeldeten Desktop-App; VeliShell installiert keinen Windows-Dienst.

## 🎨 Zwei Icon-Stile und optionale Online-Suche

Im Dock kannst du zwischen zwei Darstellungen wechseln:

- **VeliShell / Mac-inspiriert:** einheitlich skalierte und gerundete Icons, die mitgelieferten VeliShell-Grafiken und auf Wunsch ein selbst ausgewählter App-Store-Treffer – ohne künstlich erzeugte Farbfläche dahinter;
- **Windows Original:** das lokale Symbol der installierten EXE, Verknüpfung oder Shell-App – sauber skaliert und ohne zusätzlich erzeugte Farbfläche dahinter.

Unabhängig vom globalen Stil lässt sich jedes sichtbare Dock-Element in **Einstellungen → Apps** einzeln anpassen. VeliShell übernimmt lokale Bild- und Icondateien in den eigenen geschützten Datenordner, damit das Dock nicht von einer später verschobenen Quelldatei abhängt. Das VeliShell-Element und aktuell erkannte laufende Apps erhalten eigene Einträge; für den Papierkorb stehen getrennte Auswahlen für **leer** und **voll** bereit. Jede Änderung kann einzeln auf den VeliShell-Standard zurückgesetzt werden.

Wenn lokal kein passendes hochauflösendes Symbol vorhanden ist, kann VeliShell nach deiner Freigabe die offizielle [Apple iTunes Search API](https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/iTuneSearchAPI/) nach Mac-Software durchsuchen. Dafür ist **kein API-Schlüssel** nötig. Zusätzlich kann [macOSicons.com](https://macosicons.com/developers) mit deinem eigenen API-Schlüssel durchsucht werden. VeliShell sucht anhand von Programmdatei, Fenstertitel und Verknüpfungsnamen, zeigt Treffer beider Quellen mit Vorschau und Urheberangaben und lässt dich für jede App auswählen oder zur nächsten springen. Es gibt keine automatische Hintergrundsuche und keinen stillen Austausch bestehender Symbole.

Findet keine Quelle ein passendes Symbol, kannst du das vorhandene Windows-App-Icon mit dem macOSicons-Masking in Form bringen lassen. Dafür fragt VeliShell **noch einmal gesondert**, bevor das Bild hochgeladen wird. Ordner und Papierkorb sind ausdrücklich ausgenommen. Der optionale macOSicons-Schlüssel wird nur für dein Windows-Konto im Anmeldeinformationsmanager gespeichert – nicht in der Einstellungsdatei oder im Programmcode. Einen alten Brave-Schlüssel aus früheren Versionen kannst du in den Einstellungen ausdrücklich löschen.

Der Online-Katalog wird nicht mit VeliShell ausgeliefert. Suchergebnisse bleiben kurz im Arbeitsspeicher; nur das gewählte Bild wird lokal gespeichert. Quelle, Urheber und beim App Store der direkte Store-Link bleiben erreichbar. App-Store-Artwork bleibt Eigentum der jeweiligen Rechteinhaber und unterliegt Apples Bedingungen für Promo-Inhalte – es ist kein frei weiterverteilbares Icon-Paket. Weitere Angaben stehen in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## 🔐 Datenschutz und Sicherheit

- keine Telemetrie und kein Werbe-Tracking;
- Taskleiste und Desktopsymbole werden nur temporär und nach ausdrücklichem Opt-in verändert; VeliShell schreibt dafür keine dauerhafte Explorer-Konfiguration;
- Einstellungen und Icon-Cache bleiben lokal auf dem Rechner;
- bei einer von dir ausgelösten Online-Icon-Suche gehen App-, EXE- und Verknüpfungsnamen sowie passende **Fenstertitel** an Apple und mit eigenem Schlüssel auch an macOSicons; Vorschauen werden nur für die Auswahl geladen. Das Bild des Windows-App-Icons wird ausschließlich nach einer zweiten Bestätigung zum Masking hochgeladen;
- die Suche sendet beim Tippen keine Webanfragen; erst beim Anklicken einer Websuche wird dein Browser geöffnet;
- Wo eine signierte paketierte Ausgabe den Windows-Zugriff unterstützt, werden Mitteilungen nur nach der Windows-Freigabe lokal gelesen, nie übertragen und nicht in Diagnoseprotokolle geschrieben. Das Entfernen in VeliShell löscht ausschließlich die ausdrücklich angezeigte Mitteilung anhand ihrer Windows-ID. MSI und Portable besitzen aktuell keine Paketidentität und lassen den Zugriff daher deaktiviert;
- Update-Metadaten kommen ausschließlich aus dem offiziellen VeliShell-Repository;
- die Windows-Shell, Systemdateien und Sicherheitsfunktionen werden nicht ersetzt oder deaktiviert.

Lokale Daten liegen unter `%LOCALAPPDATA%\VeliShell`. Diagnoseprotokolle werden nicht automatisch übertragen.

## 🧩 Geplante Erweiterungen

Folgende Punkte stehen als Nächstes auf dem Tisch – ohne festes Veröffentlichungsdatum:

- getrennte Dock-Layouts pro Monitor;
- weitere Sprachen;
- Import und Export eigener Dock-Profile;
- zusätzliche barrierefreie Tastaturbedienung;
- signierte Windows-Releases, sobald die Herausgeberzertifizierung bereitsteht;
- mehr Feinschliff für Touch- und Tablet-Nutzung.

## 🛠️ Aus dem Quellcode bauen

Benötigt werden Windows x64 und das .NET 10 SDK.

```powershell
dotnet build VeliShell.slnx -c Release
dotnet run --project tests/VeliShell.Core.Tests/VeliShell.Core.Tests.csproj -c Release
./tools/Build.ps1 -Portable
```

Der Installer wird reproduzierbar mit WiX Toolset 4 gebaut:

```powershell
./tools/Build-Installer.ps1 -Version 0.10.0 -PublishDirectory ./out/portable
```

## 💬 Support & Kontakt

- 🌐 [Zumbyte AppSolution](https://www.zumbyte.de/)
- 🛟 [Support und Fehler melden](https://github.com/ZUMBYTE-AppSolution/VeliShell/issues)
- ✉️ [info@zumbyte.de](mailto:info@zumbyte.de)
- 🔒 [Sicherheitshinweise](SECURITY.md)

Bitte bei einer Fehlermeldung die VeliShell-Version, die Windows-Version und einen kurzen Ablauf dazuschreiben. Persönliche Dateipfade in Screenshots oder Logs vorher unkenntlich machen.

## ⚖️ Hinweise

VeliShell ist ein eigenständiges Projekt von **Zumbyte AppSolution** und steht in keiner Verbindung zu Apple. macOS, Finder, Safari und weitere Produktnamen sind Marken ihrer jeweiligen Rechteinhaber. Windows und Microsoft sind Marken der Microsoft-Unternehmensgruppe.

VeliShell ist proprietäre Software. Die Nutzungsbedingungen stehen in der
[LICENSE](LICENSE). Danke an die Projekte und Dienste, auf denen VeliShell
aufbaut. Die vollständigen Hinweise befinden sich in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); die mitgelieferten
Originaltexte liegen unter [THIRD-PARTY-LICENSES](THIRD-PARTY-LICENSES/README.md).

<p align="center"><strong>VeliShell · von Zumbyte AppSolution</strong></p>
