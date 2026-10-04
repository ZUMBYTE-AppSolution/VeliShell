# Sicherheit

Sicherheitsprobleme bitte nicht zuerst als öffentliches Issue mit vollständigen
Exploit-Details melden. Nutze den Kontaktweg unter <https://zumbyte.de/> und nenne
Produktversion, Windows-Version sowie eine kurze, reproduzierbare Beschreibung.

## Update-Vertrauen

VeliShell liest ausschließlich veröffentlichte stabile Releases aus
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

Sicherheitskorrekturen werden grundsätzlich für die neueste veröffentlichte stabile
Version bereitgestellt. Vorabstände und selbst kompilierte Änderungen können davon
abweichen.
