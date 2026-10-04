# Changelog

Alle sichtbaren Änderungen an VeliShell werden hier dokumentiert. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionierung
[Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

### Geplant

- Signierte Windows-Pakete, sobald ein dauerhaftes Zumbyte.de-Codesigning-Zertifikat bereitsteht.

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

[Unreleased]: https://github.com/ZUMBYTE-AppSolution/VeliShell/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.4.0
[0.3.1]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.3.1
[0.3.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.3.0
