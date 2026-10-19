# Sicherheit

Sicherheitsprobleme bitte nicht zuerst als öffentliches Issue mit vollständigen
Exploit-Details melden. Nutze den Kontaktweg unter <https://zumbyte.de/> und nenne
Produktversion, Windows-Version sowie eine kurze, reproduzierbare Beschreibung.

## Update-Vertrauen

VeliShell liest ausschließlich veröffentlichte Releases des gewählten Kanals aus
`ZUMBYTE-AppSolution/VeliShell`. Vor einem Download zeigt die Anwendung Version und
Changelog. Je nach bewusst gewähltem Modus wartet VeliShell auf einen Download-Klick
oder lädt das Paket automatisch. In beiden Fällen wird es bytegenau gegen den von
GitHub gelieferten SHA-256-Digest geprüft. Der Installer wird niemals automatisch
gestartet, sondern erst nach einer separaten, aktuellen Bestätigung.

SHA-256 schützt vor einem beschädigten oder nachträglich veränderten Download, ist
aber kein Ersatz für eine unabhängige Herausgebersignatur. Solange VeliShell noch
keinen fest hinterlegten Zumbyte-Zertifikatfingerabdruck besitzt, bedeutet „Signatur gültig“
in diesem Stand deshalb ausschließlich, dass Windows der Signaturkette vertraut; der
angezeigte Herausgeber ist nicht zusätzlich durch VeliShell auf Zumbyte festgelegt.
Bevor VeliShell eine signierte Release-Reihe als Zumbyte-identifiziert bezeichnet,
wird der veröffentlichte Zertifikatfingerabdruck in der Anwendung hinterlegt. Bei
unsignierten Paketen zeigen VeliShell, die Release Notes und gegebenenfalls Windows
SmartScreen diesen Zustand ausdrücklich an.

## Unterstützte Versionen

Der freiwillige Testkanal akzeptiert ausschließlich nummerierte `-test.N`-Vorabstände
mit Release-Ziel `test` aus demselben Repository. Stable lehnt alle Vorabstände ab.
Ein Kanalwechsel benötigt Zustimmung; Download, Prüfsumme und Installationsfreigabe
werden nicht umgangen. Test besitzt eine separate MSI-Identität und ein getrenntes
Einstellungsprofil. Es überschreibt weder die stabile Installation noch deren
Autostart- oder Explorer-Kontextmenüregistrierung. Store-Pakete bleiben Store-verwaltet.

Das freiwillige Modul Fenstergestaltung darf erst nach ausdrücklicher Zustimmung
geeignete klassische 32-/64-Bit-Fenster und WPF-Standardrahmen automatisch erfassen. Eine Ausnahmeliste ist
vorher und währenddessen verfügbar. Ein threadgebundener DLL-Hook dient nur zum
Bootstrap pro Ziel-UI-Thread und wird danach entfernt. PID, Startzeit, EXE-Hash,
HWND, Thread und temporärer Zustimmungsmarker werden geprüft; keine globalen Hooks.
System-/erhöhte Apps und eigene Titelleisten werden vom vollständigen Rahmenersatz abgewiesen. Eine getrennte, freiwillige Materialanpassung kann bei geeigneten Explorer-/WinUI-/App-Fenstern lesbare DWM-Werte verändern, ohne dort eine DLL zu laden; Apps können diese Effekte überzeichnen. Sie verändert weder Tabs noch Suchfelder oder eigene Buttons. Es gibt
keinen Austausch von Systemdateien und keine automatische Rechteerhöhung.
Ein unabhängiger Helfer empfängt vor der Aktivierung das Notfallkürzel. Ein lokaler
Heartbeat löst bei dessen Ausfall das Rücksetzen auf dem Ziel-UI-Thread aus. Bei
einem vollständig blockierten Fenster bleibt die Wiederherstellung ausdrücklich
ausstehend, bis es wieder reagiert. Die DLL wird nicht gewaltsam entladen; nach
Rücksetzen bleibt sie inaktiv bis zum Zielprozess-Ende. Der Updater deaktiviert vor
der Installation das Modul und wartet auf die bestätigte Rücksetzung und eigene Helfer.
Neue native Laufzeitkopien liegen in unveränderlichen, inhaltsadressierten Verzeichnissen
außerhalb des MSI-Pakets. Hashvergleich mit den Paketdateien und Lesesperren während
des Ladens verhindern das Verwenden veränderter Kopien. Fremde DLLs werden nicht entladen.
Alte, noch im Installationsordner geladene Module, unlesbare Prozesszustände oder eine
ausstehende Rücksetzung blockieren weiterhin; eine weiterlaufende App mit inaktiver
Cache-DLL allein blockiert nicht mehr. Der Installer schließt keine Apps automatisch.
Ein lokaler Notfall-Latch verhindert unbewusstes Wiederaktivieren nach
einem Ausfall. Keine allgemeine Verträglichkeitszusage für Spiele oder Sonderrahmen;
solche Programme müssen vor Aktivierung ausgeschlossen werden. Siehe
[Test- und Wiederherstellungshinweise](NATIVE-PROTOTYPE.md).

Ab 0.14.1 verbindet ein pointerfreies, pro Sitzung benanntes Kernelobjekt den
x64-Wächter mit den nativen x86-Helfern. Prozessidentität und Fenster werden auf
beiden Seiten geprüft. Der Verlust eines Kompatibilitätshelfers stoppt die Aufnahme;
Materialwerte liegen zusätzlich im gemeinsamen Speicher für den Rückweg durch den
Wächter. Ein weiterlebender Material-Helfer setzt auch bei Wächterausfall zurück.
Eine zeitgleich von der Ziel-App selbst geänderte DWM-Eigenschaft wird beim Rollback
nicht mit einem älteren Snapshot überschrieben. Der isolierte QA-Modus lässt
ausschließlich eigene, verifizierte Beispielfenster zu und registriert kein globales
Notfallkürzel einer parallel laufenden Installation um.

Sicherheitskorrekturen werden grundsätzlich für die neueste veröffentlichte stabile
Version bereitgestellt. Vorabstände und selbst kompilierte Änderungen können davon
abweichen.
