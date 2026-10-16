# Drittanbieter-Hinweise

VeliShell wird von **Zumbyte.de** entwickelt. Dieses Dokument nennt die Komponenten
und Inhaltsquellen, die für Build oder optionale Funktionen relevant sind. Es ersetzt
nicht die jeweils verlinkten Lizenztexte und Nutzungsbedingungen.

## Microsoft .NET

Die portable Anwendung und der Installer können Bestandteile der .NET-Laufzeit
mitliefern. .NET ist ein Microsoft-Projekt unter der MIT-Lizenz.

Die zum Self-contained-Paket gehörenden, unveränderten Originaltexte für
`.NET 10.0.13` und `Microsoft.WindowsDesktop.App/WPF 10.0.13` werden zusammen
mit der Anwendung im Ordner [`THIRD-PARTY-LICENSES`](THIRD-PARTY-LICENSES/README.md)
ausgeliefert. Damit bleiben die Hinweise auch in der Portable-ZIP und nach einer
MSI-Installation offline einsehbar.

- Projekt: <https://github.com/dotnet/runtime>
- Lizenz: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>
- Drittanbieter-Hinweise: <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- WPF-Projekt: <https://github.com/dotnet/wpf>

## WiX Toolset 4.0.6

Der MSI-Installer wird reproduzierbar mit der fest gesetzten Build-Abhängigkeit
`WixToolset.Sdk 4.0.6` erstellt. WiX ist nur ein Build-Werkzeug und wird nicht als
Teil der laufenden VeliShell-Anwendung installiert.

- Projekt: <https://github.com/wixtoolset/wix>
- Paket/Lizenzangabe (Microsoft Reciprocal License): <https://www.nuget.org/packages/WixToolset.Sdk/4.0.6>

## Apple iTunes Search API und App-Store-Inhalte

Die optionale Online-Suche verwendet nach einem ausdrücklichen Klick
die dokumentierte Apple iTunes Search API. Dafür ist kein API-Schlüssel erforderlich.
Dabei können der App-Name, der Name der ausführbaren Datei, der Verknüpfungsname
und passende Fenstertitel als Suchbegriffe verwendet werden. Sie und ein aus der
Windows-Region abgeleiteter Ländercode werden an
`itunes.apple.com` übertragen; Vorschaubilder kommen von Apples `mzstatic.com`-CDN.
VeliShell zeigt mehrere Treffer mit Anbieter und direktem App-Store-Link. Es wählt
keinen Treffer automatisch aus. Nur das ausdrücklich gewählte Bild wird zusammen
mit Quelle und Store-Link in einem auf 64 Einträge, 64 MiB und 30 Tage begrenzten
lokalen Cache gespeichert. In Installer und Portable-Paket ist kein App-Store-Katalog
enthalten.

Die Search API erteilt keine allgemeine Lizenz, App-Store-Grafiken als frei
weiterverteilbares Windows-Icon-Paket zu verwenden. Die Bilder bleiben Inhalte ihrer
jeweiligen Rechteinhaber. VeliShell hält deshalb Anbieter und direkten Store-Link am
gewählten Eintrag sichtbar; Nutzer müssen selbst sicherstellen, dass ihre konkrete
Verwendung zulässig ist. Für eine unabhängige Nutzung steht die lokale Bildauswahl
zur Verfügung.

- Apple Search API: <https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/iTuneSearchAPI/>
- Apple-Bedingungen für Promo-Inhalte: <https://www.apple.com/legal/internet-services/itunes/>
- Apple Design Resources License: <https://developer.apple.com/support/downloads/terms/apple-design-resources/Apple-Design-Resources-License-20230621-English.pdf>

## Wikipedia und Wikimedia Commons

Die freiwillige Online-Symbolsuche durchsucht zusätzlich die englische Wikipedia,
bei deutscher App-Sprache auch die deutsche Wikipedia, sowie zugehörige Logos in
Wikimedia Commons. Suchbegriffe werden an `en.wikipedia.org`, `de.wikipedia.org`
und `commons.wikimedia.org` übertragen; PNG-Vorschauen kommen von
`upload.wikimedia.org` oder `thumb.wikimedia.org`. Dafür wird kein API-Schlüssel
benötigt. Eine frühere Online-Freigabe muss für diesen zusätzlichen Empfänger
einmal erneuert werden. Ohne Suche entstehen keine Wikipedia-Netzwerkanfragen.

VeliShell bietet nur Logo-Dateien mit unterstützten, in den Commons-Metadaten
ausgewiesenen freien Lizenzen oder Gemeinfreiheit an. Fair-Use-Dateien sowie
nichtkommerzielle und nicht bearbeitbare CC-Varianten werden nicht angeboten.
Die Auswahl zeigt Urheber, Lizenz, Quelldatei und Wikipedia-Artikel; eine
vorhandene Lizenz-URL ist direkt erreichbar. Diese Metadaten sind keine Garantie
für die Rechtefreiheit. Lizenzpflichten und mögliche Markenrechte sind bei der
Verwendung weiterhin zu beachten.

Nur ein ausdrücklich ausgewähltes Bild wird zusammen mit Quellen-, Urheber- und
Lizenzangaben lokal gespeichert, begrenzt auf 30 Tage, 64 Einträge und 64 MiB.
Ein Katalog oder Wikimedia-Bilder werden nicht mit VeliShell ausgeliefert.

- API: <https://www.mediawiki.org/wiki/API:Imageinfo>
- Metadaten: <https://www.mediawiki.org/wiki/Extension:CommonsMetadata>
- Wiederverwendung: <https://commons.wikimedia.org/wiki/Commons:Reusing_content_outside_Wikimedia>

## macOSicons.com und optionales Masking

Mit einem eigenen, im Windows-Anmeldeinformationsmanager gespeicherten API-Schlüssel
zeigt VeliShell zusätzlich Treffer von macOSicons.com. Ein gemeinsamer Schlüssel wird
weder eingebettet noch im Repository gespeichert. Quelle und zurückgegebene
Urheberangaben bleiben bei den Treffern und übernommenen Symbolen sichtbar.
Ausgewählte Katalogbilder werden höchstens 30 Tage lokal vorgehalten. Schon mit einer
älteren VeliShell-Version heruntergeladene Bilder bleiben über den alten Cache lesbar.

Wenn keine der Suchen einen Treffer liefert, kann der Nutzer das vorhandene Windows-
App-Symbol nach einer **gesonderten Bestätigung** an die Editor-Masking-API senden.
Ohne diese Bestätigung findet kein Bild-Upload statt. Für Ordner und Papierkorb wird
kein App-Masking angeboten. Nutzer müssen die Rechte am Ausgangsbild beachten.

- API und Editor: <https://macosicons.com/developers>
- API-Bedingungen: <https://beta.macosicons.com/developers/terms>
- Allgemeine Bedingungen: <https://macosicons.com/terms>

## macOS-Systemsymbole und Papierkorb-Grafiken

Die mitgelieferten Leer- und Vollgrafiken des Papierkorbs wurden ohne fremde
Bildvorlage eigenständig für VeliShell gestaltet. Sie verwenden eine eigene
Geometrie und VeliShell-Farbwelt, enthalten keine Apple-Logos oder extrahierten
Apple-Assets und werden unter der Projektlizenz ausgeliefert.

Apple SF Symbols werden weder extrahiert noch als Font, SVG oder Bitmap in
VeliShell eingebettet. Die kleinen UI-Symbole verwenden stattdessen lokal
gespeicherte **Material Symbols Rounded**-Vektorpfade von Google. Sie werden
offline auf einem gemeinsamen 24-Punkt-Raster gezeichnet und übernehmen die
VeliShell-Themefarbe. Die Auswahl und WPF-Einbindung der Pfade wurde für
VeliShell angepasst; App-Icons und Nutzerbilder bleiben davon unberührt.

- Katalog: <https://icon-sets.iconify.design/material-symbols/>
- Originalprojekt: <https://github.com/google/material-design-icons>
- Lizenz: Apache License 2.0, vollständiger Text unter
  [`THIRD-PARTY-LICENSES/Material-Symbols-APACHE-2.0.txt`](THIRD-PARTY-LICENSES/Material-Symbols-APACHE-2.0.txt)

## VeliShell-Appsymbol

Das VeliShell-Appsymbol und das freistehende V-Signet basieren auf vom
Projektinhaber bereitgestellten Bildvorlagen. Das Appsymbol wurde für die
einheitliche VeliShell-Superellipse technisch normalisiert; das V-Signet bleibt
als transparente Marke ohne künstliche Icon-Fläche erhalten. Beide stammen nicht
aus macOSicons.com. Die darin stilisiert angedeuteten Betriebssystem-, App-
und Produktmerkmale übertragen keine Rechte an den zugrunde liegenden Marken oder
Originalsymbolen auf VeliShell.

## Marken und Zugehörigkeit

Apple, macOS und zugehörige Produktnamen sind Marken von Apple Inc. Windows und
.NET sind Marken von Microsoft. Steam ist eine Marke von Valve. Das Steam-
Statussymbol wird aus der lokal installierten `steam.exe` gelesen und für die
Menüleiste entsättigt; kein Valve-Bildasset wird mitgeliefert. VeliShell ist ein
unabhängiges Zumbyte.de-Projekt und weder von Apple, Microsoft noch Valve
autorisiert oder unterstützt.
