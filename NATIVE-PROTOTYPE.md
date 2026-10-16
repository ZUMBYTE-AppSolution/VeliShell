# VeliShell Test: nativer Fensterrahmen

Dieser Vorabstand enthält die vollständige VeliShell-App und ein separat gestartetes
eigenes Testfenster. **Nur dieses Fenster** lädt `VeliShell.NativeFrame.dll`.
Andere Apps und Windows-Dateien werden nicht verändert. Es gibt keine Injektion
in fremde Prozesse, keinen System-DLL-Austausch und keinen neuen Dienst.

## In einer VM starten

1. Vorher einen VM-Snapshot anlegen. Für den vollständigen Glaseffekt Windows 11 ab
   Build 22621, aktivierte Windows-Transparenzeffekte und funktionierende virtuelle
   Grafikbeschleunigung verwenden. Windows und VM-Treiber bestimmen die Effektqualität.
2. Das ausdrücklich als **Test / Pre-release** markierte MSI oder die portable ZIP
   herunterladen. SHA-256-Prüfsummen stehen beim Release. Der Vorabstand kann
   unsigniert sein; Sicherheitsfunktionen müssen dafür nicht abgeschaltet werden.
3. Bei Portable das ganze ZIP entpacken und `VeliShell.exe` starten. Zum Ausführen
   sind weder SDK noch Visual Studio oder eine zusätzliche C++-Runtime nötig.
4. Die stabile App vorher normal beenden: Beide Ausgaben dürfen nicht gleichzeitig
   den Desktop verwalten. Die bekannte Einrichtungsführung bleibt vorhanden.
5. **Einstellungen → Windows & Start → Native Fenstergestaltung → Testfenster öffnen**.
   Alternativ `VeliShell.exe --native-frame-demo`: Diese direkte Testansicht startet
   weder Dock noch Menüleiste und verändert keine Taskleisten-Einstellung.

Titelleiste, linke Buttons und Inhalt liegen innerhalb desselben Fensters. Es gibt
keinen außen angesetzten Aufsatz. Der Live-Hintergrund verwendet den Windows-
Composition-Renderer des Docks; Titel und Buttons werden mit Direct2D/DirectWrite
gezeichnet. Ohne diese Grafikunterstützung bleibt native Acrylic beziehungsweise
eine lesbare deckende Ersatzfläche. Die Glaswerte werden beim Öffnen aus VeliShell
übernommen. Hell/Dunkel kann direkt im Testfenster gewechselt werden.

Bitte Verschieben, Rand-/Eckenskalierung, Minimieren, Maximieren, Wiederherstellen
und die linke Schließen-Taste prüfen. Eingaben im Testfeld sind nur temporärer Testinhalt.

## Rollback und Notfall

- **Windows-Rahmen** im Testfenster stellt den Originalrahmen wieder her, ohne den
  Testinhalt zu verlieren. Danach kann der Glasrahmen erneut aktiviert werden.
- **Strg + Alt + Umschalt + F12** wird von einem unabhängigen Hilfsprozess empfangen,
  auch wenn die normale VeliShell-App nicht reagiert oder nicht läuft. Ist das
  Kürzel belegt, bleibt die native Gestaltung aus.
- `WINDOW-RECOVERY.cmd` oder `VeliShell.exe --restore-native-prototype` fordert
  denselben Rückweg an. Der Test-MSI enthält außerdem einen Notfall-Startmenüeintrag.
- Nach einem Notfall-Rollback bleibt die Gestaltung für diese Sitzung deaktiviert.
  Für einen erneuten Test das Testfenster schließen und neu öffnen.
- **Grenze:** Ein vollständig blockiertes Testfenster kann erst nach Wiederaufnahme
  seiner Fensterbehandlung zurückkehren. Es gibt dafür keine falsche Erfolgsmeldung
  und kein automatisches Beenden fremder Prozesse. Notfalls ausschließlich den
  eigenen Testprozess im Task-Manager schließen. Mit dessen Ende verschwinden
  seine Fenster und alle lokalen Rahmenänderungen; fremde Fenster bleiben unverändert.

## Stable und Test wechseln

**Einstellungen → Info & Lizenzen → Update-Kanal** bietet Stable und Test.
Stable lehnt Vorabstände ab. Test verwendet nur veröffentlichte, nummerierte
`-test.N`-Pakete vom Branch `test`, niemals EXEs direkt aus einem beweglichen Branch.
Changelog, Downloadfortschritt, SHA-256-Prüfung und Installationsbestätigung gelten
auch beim Kanalwechsel. Die Veröffentlichung verändert GitHubs „Latest“ nicht.

Test installiert sich als **VeliShell Test** nach `Programme\Zumbyte\VeliShell Test`
und speichert unter `%LOCALAPPDATA%\VeliShell-Test`. Stable bleibt mit seinem Profil
unter `Programme\Zumbyte\VeliShell`. Test überschreibt weder stabilen Autostart noch
Explorer-Anheft-Befehl. Autostart bleibt im Teststand deaktiviert.

Beim Rückwechsel wird auch eine ältere stabile Version angeboten und anschließend
die **stabile** App gestartet. Das ist kein MSI-Downgrade derselben Installation,
sondern ein Wechsel zwischen getrennten Produkten. Test bleibt installiert, bis
du es in Windows deinstallierst. Profile werden nicht automatisch kopiert.
Bereits veröffentlichte Stable-Versionen ohne Kanalauswahl bekommen diese Funktion
nicht rückwirkend; für den ersten Test daher den direkten Test-Download verwenden.
Store-Ausgaben bleiben Store-verwaltet; dieser Prototyp wird nicht dort eingereicht.

## Grenzen

Das ist ein Machbarkeitsnachweis an unserer kooperierenden eigenen App, **kein**
bereits getesteter Skin für beliebige Win32-, Browser-, WinUI-, System- oder
Administratorfenster. Vollständige Wiederherstellung bei einem dauerhaft blockierten
Zielprozess wird nicht versprochen.
