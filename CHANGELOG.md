# Changelog

Alle sichtbaren Änderungen an VeliShell werden hier dokumentiert. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionierung
[Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

## [0.8.0] - 2026-10-09

### Neu

- Die Ersteinrichtung führt jetzt in sechs Schritten durch alle Bereiche. Ein eigener Schritt „Online-Dienste“ erklärt die persönlichen Brave- und macOSicons-API-Schlüssel, verlinkt die Anbieter und erlaubt die direkte, geschützte Eingabe. Beide Schlüssel sind freiwillig und bleiben im Windows-Anmeldeinformationsmanager des jeweiligen Benutzers.
- Die Sofortsuche berücksichtigt neben Dock-Pins auch Startmenü-Einträge, registrierte Programme und geöffnete Apps. Ein schlanker Hintergrundkatalog hält diese Treffer bereit. Mit eigenem Brave-Schlüssel erscheinen nach kurzer Tipp-Pause Live-Webtreffer; ohne Schlüssel bleibt die Websuche per Browser-Klick erhalten.
- Die manuelle Symbolsuche verwendet Programmdatei, Fenstertitel und Verknüpfungsnamen als Suchbegriffe und vereint Treffer aus dem Mac App Store und optional macOSicons. Für jede App kann ein Symbol gewählt oder zum nächsten Eintrag gesprungen werden.
- Falls keine Katalogauswahl passt, kann das vorhandene Windows-App-Symbol nach gesonderter Bestätigung über das macOSicons-Masking angepasst werden. Ordner- und Papierkorbsymbole bleiben davon ausgenommen und behalten ihre freie Form.

### Verbessert

- Der Update-Dialog zeigt Version und Changelog vor dem Herunterladen, danach einen detaillierten Fortschritt und eine eigene Nachfrage vor der Installation. Nach dem MSI-Abschluss versucht VeliShell automatisch neu zu starten; ein abgebrochener Installer lässt die bisherige Version wieder starten.
- Online-Symbol- und Webanfragen sind begrenzt und werden nur für die jeweils aktivierte Funktion ausgelöst. macOSicons-Vorschauen und -Downloads bleiben höchstens 30 Tage im lokalen Cache.

### Hinweise

- Brave- und macOSicons-Schlüssel gehören dem jeweiligen Benutzer; mögliche Tarife, Limits und Kosten legt der Anbieter fest. Apple-App-Store-Suche und lokale Programmsuche benötigen keinen Schlüssel.
- Das Mitlesen fremder Windows-Mitteilungen bleibt in MSI und Portable ohne Windows-Paketidentität nicht verfügbar. Die Store-Version benötigt dafür weiterhin die Genehmigung der deklarierten Berechtigung.

## [0.7.0] - 2026-10-09

### Neu

- Ein geführter erster Start führt in fünf Schritten durch sämtliche Einstellungsbereiche. Er gilt erst nach „Fertigstellen“ als abgeschlossen; freiwillige Windows-Eingriffe bleiben zunächst deaktiviert.
- Eine Sofortsuche findet Dock-Pins und Startmenü-Programme während der Eingabe. Webtreffer öffnen erst nach Auswahl den Browser. Win + Leertaste wird versucht; Strg + Alt + Leertaste sowie Menü und Einstellungen dienen als Alternativen.
- Ein optionales Windows-Start-Symbol öffnet das reguläre Startmenü links vor den App-Pins, mit eigener Trennlinie und individuell wählbarem Icon.
- App-Symbole im Dock hüpfen beim Start kurz und respektieren die Einstellung „Bewegung reduzieren“.
- Ein eigener reproduzierbarer MSIX-Build bereitet VeliShell für den Microsoft Store vor. Nach erfolgreicher Store-Zertifizierung signiert Microsoft das ausgelieferte Paket.
- Das Store-Paket deklariert die geschützte Windows-Berechtigung für die Mitteilungszentrale und eine optionale, vom Benutzer kontrollierte Autostart-Aufgabe.
- Die GitHub-Automatisierung baut und prüft das MSIX vor jeder Einreichung und kann spätere Versionen über die Microsoft Store Developer CLI übermitteln.

### Geändert

- Das Dock hat einen kleineren Eckradius und eine zurückhaltendere Glasfläche; Dock und Menüleiste sind in Hell und Dunkel etwas durchscheinender.
- In der Store-Version übernimmt Microsoft Store die Aktualisierung; die separate GitHub-Aktualisierung wird dort nicht parallel ausgeführt. MSI und Portable behalten die bisherige wählbare Aktualisierungssuche.
- Die klassische Explorer-Registry-Verknüpfung „Im Dock anheften“ wird nur in MSI/Portable registriert. Für MSIX wäre dafür eine eigene paketierte Explorer-COM-Erweiterung erforderlich.

### Bekannte Einschränkungen

- Die Suche verwendet derzeit Dock-Pins und Startmenü-Verknüpfungen; eine vollständige Windows-Dateiindizierung oder Live-Webtreffer sind noch nicht enthalten. Win + Leertaste kann durch Windows bereits belegt sein.
- Die Glasoptik ist eine transparente, themenabhängige WPF-Darstellung und garantiert keinen echten Hintergrund-Weichzeichner oder pixelgenaue Übereinstimmung mit macOS.
- Die erste Store-Einreichung muss im Partner Center angelegt werden. Automatische Paketaktualisierungen sind erst für eine bereits veröffentlichte und aktive Store-App verfügbar.

## [0.6.0] - 2026-10-04

### Neu

- Dateien und Ordner erhalten den statischen Explorer-Kontextmenübefehl „Im Dock anheften“. Er startet ausschließlich die normale VeliShell-EXE; in Explorer wird kein Erweiterungscode geladen.
- Angeheftete Ordner öffnen beim normalen Klick ein Hell-/Dunkel-fähiges Dock-Popover mit begrenzter asynchroner Dateiliste, sicherer Unterordnernavigation, Breadcrumbs, Zurück und „Im Explorer öffnen“.
- Desktopsymbole lassen sich ausdrücklich und nur vorübergehend für die VeliShell-Laufzeit ausblenden; ein eng gebundenes Wiederherstellungsjournal schützt den vorher sichtbaren Explorer-Zustand bei einem unterbrochenen Lauf.
- Die Mitteilungszentrale kann VeliShell- und Windows-Mitteilungen in einer gemeinsamen, begrenzten Ansicht zusammenführen, einzelne angezeigte Windows-IDs entfernen und fragt den geschützten Zugriff ausschließlich nach einem ausdrücklichen Klick an.

### Geändert

- Die manuelle Online-Symbolsuche nutzt Apples offizielle iTunes Search API für Mac-Software. Sie benötigt keinen API-Schlüssel, zeigt mehrere Treffer samt Entwickler und direktem App-Store-Link und übernimmt niemals automatisch einen Treffer.
- Die eigenständig für VeliShell gestalteten neuen Papierkorb-Grafiken werden als freie transparente Objekte ohne App-Superellipse dargestellt; Leer- und Vollzustand behalten exakt dieselbe Dock-Fläche.
- Dock und Menüleiste verwenden in Hell und Dunkel eine etwas leichtere Glas-Transparenz. Center-Panels behalten ihre bisherige Lesbarkeit.
- Das feste VeliShell-Einstellungssymbol kann aus dem Dock entfernt und über die App-Einstellungen jederzeit wieder hinzugefügt werden.
- Bereits lokal gespeicherte macOSicons.com-Dateien bleiben lesbar, aber VeliShell stellt keine neuen Verbindungen zu diesem Anbieter her und verwaltet keinen API-Schlüssel mehr.

### Behoben

- Ein abgewiesener Zweitstart kann den Wiederherstellungsmarker einer laufenden VeliShell-Instanz mit ausgeblendeten Desktopsymbolen nicht mehr löschen.
- Windows-Mitteilungen werden nach Einzel- oder Gesamtlöschung erst nach einer abschließenden Plattform-Synchronisierung aus der kombinierten Ansicht entfernt.

### Sicherheit

- Das Ordner-Popover zeigt höchstens 120 Einträge und 16 Ebenen, bleibt innerhalb des angehefteten Stammordners und folgt keinen Reparse-/Junction-Zielen.
- Das Ausblenden der Desktopsymbole verändert keine Registry-, Gruppenrichtlinien- oder persistente Explorer-Einstellung und stellt ausschließlich eine zuvor sichtbare, exakt wiedererkannte Desktopansicht wieder her.
- App-Store-Suche und Bildabruf sind auf Apples dokumentierte HTTPS-Endpunkte, öffentliche Zieladressen, begrenzte Antwortgrößen und geprüfte PNG-/JPEG-Abmessungen eingeschränkt; der lokale Cache ist in Größe, Anzahl und Alter begrenzt.
- Der Windows-Mitteilungsadapter gibt in der MSI-/Portable-Ausgabe ohne Paketidentität ausdrücklich „nicht unterstützt“ zurück, statt eine nicht gewährbare Berechtigung vorzutäuschen. Microsoft verlangt dafür eine deklarierte `userNotificationListener`-Capability in einem paketierten Build.

### Bekannte Einschränkungen

- Die aktuelle MSI-/Portable-Ausgabe besitzt noch keine signierte Paketidentität. VeliShell-Mitteilungen funktionieren, das Mitlesen fremder Windows-Mitteilungen bleibt in diesen Paketen jedoch deaktiviert.

## [0.5.0] - 2026-10-04

### Neu

- Die VeliShell-Menüleiste besitzt jetzt ein milchiges Kontrollzentrum mit Netzwerk-, Bluetooth-, Fokus-, Anzeige- und Energiezugriffen sowie echter Regelung der Windows-Masterlautstärke und Stummschaltung.
- Ein lokales Benachrichtigungscenter zeigt VeliShell-Ereignisse und gefundene Updates, zählt ungelesene Hinweise im Menüleisten-Badge und unterstützt Gelesen-Markierung, einzelnes Entfernen und vollständiges Leeren.
- Menüleiste, Center-Panels und Einstellungsnavigation verwenden einen eigenen, bei jeder DPI-Stufe scharf gezeichneten VeliShell-Vektorsymbolsatz mit einheitlichem 24-Punkt-Raster und automatischer Theme-Farbe.

### Geändert

- Kontroll- und Benachrichtigungscenter öffnen exklusiv unter dem jeweiligen Menüleistensymbol, bleiben innerhalb des verfügbaren Bildschirmbereichs und schließen sich bei Deaktivierung oder mit Escape.
- Die Updateprüfung verwendet bei einem ausgeschöpften anonymen GitHub-REST-Limit automatisch die öffentliche Release-Seite, den offiziellen Atom-Feed und taggebundene Release-Artefakte, ohne einen persönlichen GitHub-Schlüssel zu verlangen.

### Behoben

- Beim vollständigen Ausblenden der Windows-Taskleiste bleibt kein schwarzer, reservierter Streifen mehr zurück. VeliShell vermeidet den Explorer-Broadcast, der die freigegebene Kante erneut beanspruchte, überwacht die Arbeitsfläche weiter und ordnet maximierte Fenster bis zum echten Bildschirmrand neu an.
- Die obere Reservierung der VeliShell-Menüleiste bleibt bei der Taskleistenfreigabe erhalten; Monitor-, DPI- und Explorer-Neustarts werden ohne Überschreiben fremder AppBars neu abgeglichen.
- Die manuelle und automatische Aktualisierungssuche bleibt auch dann verfügbar, wenn GitHub die an eine öffentliche IP gebundenen anonymen REST-Anfragen mit HTTP 403 oder 429 begrenzt.

### Sicherheit

- Der Update-Fallback akzeptiert ausschließlich HTTPS-Ziele des offiziellen VeliShell-Repositories bzw. der erlaubten GitHub-Asset-CDNs und verknüpft Release-Seite, stabilen SemVer-Tag, Changelog, Installername, Größe und SHA-256-Prüfsumme miteinander.
- Originale Apple SF Symbols werden nicht extrahiert oder im Windows-Paket verteilt. Die neue Symbolsprache besteht vollständig aus eigenem VeliShell-Vektorcode.

## [0.4.0] - 2026-10-04

### Neu

- Zwei frei wählbare Icon-Stile: ein einheitlicher VeliShell-/Mac-inspirierter Stil und die lokalen Windows-Originalsymbole – jeweils ohne künstlich erzeugten Akzenthintergrund.
- Manuelle Icon-Suche über die dokumentierte macOSicons.com-API. Treffer werden mit Vorschau, Kategorie, Downloadzahl und Urheberangabe zur Auswahl gezeigt; nur das ausdrücklich gewählte Icon wird übernommen.
- Eigene VeliShell-Papierkorb-Illustrationen für Leer- und Vollzustand, ohne proprietäre Apple-Systemgrafiken zu verteilen.
- Erste optionale VeliShell-Menüleiste mit App-Menü, offenen Fenstern, Uhr sowie Schnellzugriffen für Netzwerk, Ton und Energie.
- Individuelle Icon-Auswahl für angeheftete und laufende Apps, das feste VeliShell-Element sowie getrennt für den leeren und vollen Papierkorb; alle Anpassungen lassen sich einzeln zurücksetzen.
- Beim Ausblenden der Windows-Taskleiste gibt VeliShell deren reservierten Arbeitsbereich frei, ordnet maximierte Fenster neu an und stellt den ursprünglichen Zustand beim Einblenden, Beenden oder nach einem unterbrochenen Lauf wieder her.
- Die optionale Menüleiste reserviert ihren Platz am oberen Bildschirmrand und hält maximierte Fenster aus diesem Bereich heraus; beim Abschalten oder Beenden wird der ursprüngliche Arbeitsbereich wiederhergestellt.

### Geändert

- Der API-Schlüssel für macOSicons.com wird ausschließlich im Windows-Anmeldeinformationsspeicher abgelegt; App-Namen werden nur bei einer bewusst gestarteten Suche übertragen.
- Das freistehende V-Signet wird in Einstellungen, Info und Menüleiste hochwertig skaliert, damit es auch bei Windows-Anzeigeskalierung scharf bleibt.
- Dock-Hinweise zeigen nur noch den Programmnamen und niemals die interne Icon-Quelle.
- Das Herausziehen eines Eintrags entfernt ihn nur aus dem Dock; VeliShell erzeugt dabei keine Desktop- oder Explorer-Verknüpfung mehr.
- Automatisch aus der Icon-Hauptfarbe erzeugte Hintergründe wurden entfernt; im Dock wird nur noch das eigentliche, einheitlich skalierte und gerundete Icon dargestellt.
- Der Installer verwendet wieder einen klaren Installationsablauf ohne optionale Dienst-Komponente.

### Behoben

- Drag-Ghost, Einfügeposition, Drop und Herausziehen verwenden exakt dieselbe sichtbare Dock-Grenze; der transparente Fensterbereich signalisiert keine Ablage mehr.
- Der Notfall-Hotkey zum Wiederherstellen der Taskleiste gewinnt auch dann sicher, wenn gleichzeitig die Menüleiste umgeschaltet wird.
- Beschädigte oder nicht unterstützte lokale Bilddateien werden als ungültiges Icon abgewiesen, ohne VeliShell zu beenden.

### Entfernt

- Der separate Windows-Update-Dienst samt Installer-Feature, ProgramData-Berechtigungen und Hintergrunddienst-Paket wurde vollständig entfernt. Benutzer-Autostart und Updateprüfung beim Start der normalen Desktop-App bleiben erhalten.

### Sicherheit

- Online-Icons werden ausschließlich über HTTPS geladen und vor der Übernahme auf öffentlichen Zielhost, Dateigröße, PNG-Struktur, Pixelmaße und Prüfsumme geprüft. Cache-Einträge werden begrenzt und nach spätestens 30 Tagen erneuert.
- Apple-App-Store-Artwork sowie originale macOS-System- und Papierkorb-Icons werden aus Lizenzgründen nicht als frei weiterverteilbare Dock-Assets verwendet.

## [0.3.1] - 2026-10-04

### Behoben

- Der optionale Hintergrunddienst wird nicht mehr mit der WPF-Laufzeit des Docks vermischt. Sein eigener vollständiger .NET-Laufzeitordner verhindert widersprüchliche Assemblies wie die zwei unterschiedlichen `WindowsBase.dll`-Varianten im selben Verzeichnis.
- Die Windows-CI installiert den MSI nun mit ausgewähltem Dienst, prüft Dienstkonto, isolierten Programmpfad, laufenden Zustand und Statusdatei und entfernt die Testinstallation danach wieder. Ein bloßer Start der EXE außerhalb der Windows-Dienstverwaltung kann diesen Fehler damit nicht mehr übersehen.

## [0.3.0] - 2026-10-04

### Neu

- Neuer Produktname **VeliShell** und neue Zumbyte.de-Produktidentität.
- Versionierte Windows-x64-Pakete: portables ZIP und systemweiter MSI-Installer mit einem standardmäßig abgewählten Dienst-Feature.
- In-App-Updates über veröffentlichte GitHub Releases mit drei wählbaren Modi: nur manuell, benachrichtigen oder geprüft automatisch herunterladen.
- Das Changelog bleibt vor und während eines automatischen Downloads sichtbar; der Installer startet ausschließlich nach einer eigenen Bestätigung.
- Strikte SHA-256-Prüfung des von GitHub gelieferten Asset-Digests; Windows-Herausgebersignaturen werden zusätzlich geprüft und ihr Status wird getrennt ausgewiesen.
- Freiwilliger Benutzer-Autostart. Die Anwendung wird nicht heimlich registriert und nicht automatisch als Dienst installiert.
- Freiwilliger LocalService-Hintergrunddienst für reine Release-Metadatenprüfungen; keine UI, Downloads oder Installationen aus Session 0.
- Sprachwahl zwischen System, Deutsch und Englisch als Grundlage für weitere Übersetzungen.
- Neues, vom Projektinhaber ausgewähltes VeliShell-Appsymbol sowie ein freistehendes V-Signet für Installer, Einstellungen und Info.

### Geändert

- Einheitliche Dock-Icon-Fläche, Skalierung und kontinuierliche Rundung für lokale, geladene und VeliShell-eigene Icons.
- Einstellungen und Update-Dialog verwenden eine gemeinsame barrierearme Hell-/Dunkel-Farbwelt mit Blau-Cyan-Violett-Verläufen, fokussierten Leuchteffekten und sichtbaren Tastaturzuständen; der MSI-Installer erhält passende Markenflächen.
- Die Auswahlkarten für Hell, Dunkel und System zeigen ihren Rahmen nun in jeder Ansicht vollständig und mit gleichmäßigem Innenabstand.
- Release-Metadaten, Dateinamen und Versionsinformationen verwenden eine gemeinsame SemVer-Version.
- Der optionale Update-Dienst schreibt seinen Status auch bei gleichzeitigen Lesezugriffen atomar und begrenzt festhängende Netzwerkantworten durch eine Inaktivitätsfrist.

### Sicherheit

- Automatische Downloads sind standardmäßig aus und müssen in den Einstellungen bewusst gewählt werden. Eine stille Installation gibt es in keinem Modus.
- Installer-Downloads sind auf das offizielle Repository `ZUMBYTE-AppSolution/VeliShell` und dessen GitHub-CDN begrenzt.
- Die vollständigen Lizenz- und Drittanbieterhinweise der mitgelieferten .NET- und WPF-Laufzeit sind offline im Installer und im portablen Paket enthalten.
- Version 0.3.0 kann noch ohne Herausgeberzertifikat veröffentlicht werden. VeliShell zeigt diesen Zustand vor dem Start des Installers ausdrücklich an; Windows SmartScreen kann zusätzlich warnen.

[Unreleased]: https://github.com/ZUMBYTE-AppSolution/VeliShell/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.6.0
[0.5.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.5.0
[0.4.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0
[0.3.1]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.3.1
[0.3.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.3.0
