# VeliShell 0.14.1-test.2: Prüfung echter Apps in der VM

**Lokaler, noch nicht freigegebener Prüfstand.** Die neuen Rahmenadapter für
Explorer, Paint und Editor sind noch nicht an echten Apps abgenommen.
Build und unsichtbare Layout-/Logiktests allein bestätigen ihre Funktion nicht.

## So bleibt dein Haupt-PC frei

1. Das komplette ZIP **in deiner Windows-VM** in einen neuen Ordner entpacken.
   Ein .NET SDK oder eine Installation sind nicht nötig. Das Paket ist unsigniert;
   die Windows-Sicherheitsfunktionen nicht abschalten.
2. Einen VM-Snapshot bereithalten und Arbeit speichern. In bereits laufendem
   VeliShell die Fenstergestaltung deaktivieren und die Rücksetzung abwarten.
   Bei noch geladenen älteren Adapter-DLLs gegebenenfalls selbst ab-/anmelden.
3. In der VM Explorer, Paint und Editor mit leeren bzw. ungefährlichen Testinhalten
   öffnen. Auf deinem Haupt-PC muss nichts geschlossen werden.
4. **VM-APP-CHECK.cmd** doppelklicken. Nicht die normale VeliShell.exe starten,
   wenn du nur die isolierte Prüfung möchtest. Das Prüfprogramm startet kein Dock,
   keine Menüleiste und keine allgemeine Automatik. Es übernimmt keine Maus/Tastatur.
5. Genau ein Fenster auswählen, **Ausgewähltes Fenster prüfen …** drücken und
   den Hinweis zur DLL-Einbindung bestätigen. Explorer wird nur am ausgewählten
   Fenster gestaltet; die DLL bleibt dennoch bis zum Ende des Explorer-Prozesses
   geladen. Die anderen Fenster werden nicht automatisch gestaltet.

## In jedem der drei echten Programme prüfen

- Glas und Rundungen liegen bündig innerhalb des Fensters, linke Buttons sichtbar.
- Tabs, Suche und Menüs sind vollständig vorhanden und benutzbar.
- Minimieren, Maximieren und Wiederherstellen funktionieren. Schließen nur mit
  einer leeren Testinstanz prüfen; keine ungespeicherten Daten riskieren.
- Mit **Hell / Dunkel wechseln** beide Darstellungen ansehen.
- Die Häkchen im Prüfprogramm dokumentieren deine Beobachtung. Sie sind keine
  automatische Erfolgsbestätigung.

## Rückweg prüfen

Zum Geometrievergleich zunächst die ursprüngliche Fenstergröße, DPI und den
ursprünglichen Maximierungszustand wiederherstellen. Drei getrennte Durchläufe:

1. Aktivieren, danach **Normal zurücksetzen**.
2. Erneut aktivieren, danach **Unabhängiges Rücksetzsignal**.
3. Erneut aktivieren, danach optional **Wächterausfall prüfen …**. Nur der eigene,
   genau zu dieser Prüfung gehörende VeliShell-Helfer wird beendet. Die native DLL
   muss den fehlenden Heartbeat erkennen und selbst zurücksetzen. Die Ziel-App
   wird nicht beendet.

Eine Rücksetzung wird nur dann als verglichen gemeldet, wenn zuvor eine technische
Aktivierung bestätigt war, die Ziel-App noch läuft und die überprüfbaren
Geometrie-/DWM-Werte übereinstimmen. Hängende Apps, geschlossene Fenster und
nicht vergleichbare Größen bleiben offen bzw. nicht bestanden. Bei einer
blockierten App muss diese erst wieder Fensternachrichten verarbeiten.

Das globale Notfallkürzel wird im isolierten Prüfmodus nicht registriert, damit
keine laufende Installation gestört wird. Es bleibt im normalen optionalen Modul
verfügbar und muss dort separat geprüft werden. **WINDOW-RECOVERY.cmd** bleibt
ebenfalls enthalten. Es fordert die Rücksetzung aller entdeckten VeliShell-Sitzungen
an; nicht als gezielten Einzelfenstertest verwenden.

## Bericht zurückgeben

**Prüfbericht speichern** zeigt den Speicherort. Die JSON-Datei liegt unter
`%LOCALAPPDATA%\VeliShell-Test\native-vm-checks` und kann hier angehängt werden.
Sie enthält keine Dokumenttitel, Screenshots, vollständigen Zielpfade, API-Schlüssel
oder Einstellungen. Es wird nichts automatisch hochgeladen. Für die visuelle
Bewertung bitte zusätzlich einen Screenshot aus der VM mitsenden.

Die inaktive DLL bleibt nach dem Rücksetzen bis zum Prozessende geladen. Vor dem
Wechsel zu einem anderen Adapterpaket die betroffenen Apps selbst schließen, beim
Explorer gegebenenfalls ab-/anmelden. Es gibt kein automatisches Abschießen,
erzwungenes Entladen, Ändern von Windows-Dateien oder automatische Rechteerhöhung.
