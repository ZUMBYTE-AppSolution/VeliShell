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

## macOS Icon Gallery

Die optionale Online-Suche greift nach Zustimmung des Nutzers auf öffentlich
abrufbare Metadaten von <https://www.macosicongallery.com/> zu. VeliShell bündelt
keine Gallery-Bilder. Rechte an geladenen Symbolen verbleiben bei den jeweiligen
Urhebern, Designern und Markeninhabern. Die öffentliche Abrufbarkeit ist keine
pauschale Lizenz zur Weiterverteilung oder kommerziellen Nutzung.

## VeliShell-Appsymbol

Das VeliShell-Appsymbol und das freistehende V-Signet basieren auf vom
Projektinhaber bereitgestellten Bildvorlagen. Das Appsymbol wurde für die
einheitliche VeliShell-Superellipse technisch normalisiert; das V-Signet bleibt
als transparente Marke ohne künstliche Icon-Fläche erhalten. Beide stammen nicht
aus macosicongallery.com. Die darin stilisiert angedeuteten Betriebssystem-, App-
und Produktmerkmale übertragen keine Rechte an den zugrunde liegenden Marken oder
Originalsymbolen auf VeliShell.

## Marken und Zugehörigkeit

Apple, macOS und zugehörige Produktnamen sind Marken von Apple Inc. Windows und
.NET sind Marken von Microsoft. VeliShell ist ein unabhängiges Zumbyte.de-Projekt
und weder von Apple noch Microsoft autorisiert oder unterstützt.
