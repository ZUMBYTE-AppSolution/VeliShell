# Mitgelieferte Drittanbieter-Lizenztexte

Dieser Ordner enthält die unveränderten Lizenz- und Hinweistexte der
.NET-Komponenten, die VeliShell 0.3.0 im Self-contained-Paket für Windows x64
mitliefert.

| Komponente | Version | Mitgelieferte Originaltexte |
|---|---:|---|
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.12 | `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT` aus dem offiziellen NuGet-Runtime-Paket |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 (WPF) | 10.0.12 | `LICENSE` aus dem offiziellen NuGet-Runtime-Paket sowie `THIRD-PARTY-NOTICES.TXT` aus dem zugehörigen Windows-Desktop-SDK 10.0.401 |

Die verwendeten Runtime-Binärdateien und das Windows-Desktop-Paket stammen aus
dem .NET-Commit `95017c711e6afc1085133d440e42b4bd78155701`. Das
Windows-Desktop-Runtime-Paket enthält selbst keinen separaten
`THIRD-PARTY-NOTICES`-Eintrag; deshalb liegt hier der offizielle, mit demselben
SDK installierte Windows-Desktop-Hinweistext bei.

Die Dateien werden bei einer Änderung der mitgelieferten .NET-Runtime-Version
bewusst aktualisiert. Der Build bricht ab, wenn die Runtime-Binärdateien nicht
mehr zur dokumentierten Hinweisversion passen.

Projektquellen:

- <https://github.com/dotnet/runtime>
- <https://github.com/dotnet/wpf>
