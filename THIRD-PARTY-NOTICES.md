# Drittanbieter-Hinweise

VeliShell wird von **Zumbyte.de** entwickelt. Dieses Dokument nennt die Komponenten
und Inhaltsquellen, die für Build oder optionale Funktionen relevant sind. Es ersetzt
nicht die jeweils verlinkten Lizenztexte und Nutzungsbedingungen.

## Microsoft .NET

Die portable Anwendung und der Installer können Bestandteile der .NET-Laufzeit
mitliefern. .NET ist ein Microsoft-Projekt unter der MIT-Lizenz.

Die zum Self-contained-Paket gehörenden, unveränderten Originaltexte für
`.NET 10.0.12` und `Microsoft.WindowsDesktop.App/WPF 10.0.12` werden zusammen
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

## macOSicons.com API

Die optionale Online-Suche verwendet nach ausdrücklicher Aktivierung die
dokumentierte API von <https://macosicons.com/developers>. Jeder Nutzer stellt
seinen eigenen API-Schlüssel bereit. VeliShell bettet keinen gemeinsamen Schlüssel
ein und bewahrt den Nutzerschlüssel im Windows-Anmeldeinformationsspeicher auf.

Die API-Nutzungsbedingungen erlauben Integrationen das Suchen, Abrufen,
Zwischenspeichern, Anzeigen und Anwenden angebotener Icons. Die jeweiligen Icons
bleiben Eigentum ihrer Ersteller und können zusätzlichen Einzellizenzen unterliegen.
VeliShell bewahrt deshalb die von der API gelieferten Angaben zu Quelle und Urheber
und zeigt sie in der Auswahl an. Die Bibliothek wird weder gebündelt noch als eigener
Katalog weiterverkauft oder massenhaft exportiert.

- API: <https://api.macosicons.com/api/v1/search>
- API-Bedingungen: <https://beta.macosicons.com/developers/terms>
- Anbieter: <https://macosicons.com/>

## Apple App Store und macOS-Systemsymbole

VeliShell verwendet App-Store-Grafiken nicht als dauerhafte Dock-Ersatzsymbole und
liefert keine originalen macOS-System-, Finder-, Einstellungs- oder Papierkorb-Icons
mit. Apples Search-API-Richtlinien beschränken die bereitgestellten Promo-Inhalte auf
die Bewerbung des zugehörigen Store-Inhalts in unmittelbarer Nähe eines Store-Links.
Sie sind daher keine allgemeine Quelle für ein weiterverteilbares Windows-Icon-Paket.

- Apple Search API: <https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/iTuneSearchAPI/>
- Apple Design Resources License: <https://developer.apple.com/support/downloads/terms/apple-design-resources/Apple-Design-Resources-License-20230621-English.pdf>

Die mitgelieferten Leer- und Vollzustände des VeliShell-Papierkorbs sind eigens für
dieses Projekt gestaltete, Mac-inspirierte Illustrationen ohne Apple-Logo und keine
Kopien proprietärer Apple-Assets.

Auch Apple SF Symbols werden weder extrahiert noch als Font, SVG oder Bitmap in
VeliShell eingebettet. Die kleinen Symbole in Menüleiste, Kontrollcenter und
Einstellungen sind eigenständige, zur Laufzeit gezeichnete VeliShell-Vektoren auf
einem gemeinsamen 24-Punkt-Raster. Sie übernehmen keine Apple-Originalpfade und
bleiben bei jeder Windows-Skalierung scharf.

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
.NET sind Marken von Microsoft. VeliShell ist ein unabhängiges Zumbyte.de-Projekt
und weder von Apple noch Microsoft autorisiert oder unterstützt.
