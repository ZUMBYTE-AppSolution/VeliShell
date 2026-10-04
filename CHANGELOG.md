# Changelog

Alle sichtbaren Änderungen an VeliShell werden hier dokumentiert. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionierung
[Semantic Versioning](https://semver.org/lang/de/).

## [Unreleased]

### Geplant

- Signierte Windows-Pakete, sobald ein dauerhaftes Zumbyte.de-Codesigning-Zertifikat bereitsteht.

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

[Unreleased]: https://github.com/ZUMBYTE-AppSolution/VeliShell/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/ZUMBYTE-AppSolution/VeliShell/releases/tag/v0.3.0
