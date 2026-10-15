# Mitgelieferte Drittanbieter-Lizenztexte

Dieser Ordner enthält die unveränderten Lizenz- und Hinweistexte der
.NET-Komponenten, die VeliShell im Self-contained-Paket für Windows x64
mitliefert.

| Komponente | Version | Mitgelieferte Originaltexte |
|---|---:|---|
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 | `LICENSE.TXT`, `THIRD-PARTY-NOTICES.TXT` aus dem offiziellen NuGet-Runtime-Paket |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 (WPF) | 10.0.13 | `LICENSE` aus dem offiziellen NuGet-Runtime-Paket sowie `THIRD-PARTY-NOTICES.TXT` aus dem WPF-Tag `v10.0.13` |
| Google Material Symbols Rounded | Apache 2.0 | `Material-Symbols-APACHE-2.0.txt` aus dem offiziellen Material-Design-Icons-Projekt |

Die verwendeten Runtime-Binärdateien und das Windows-Desktop-Paket stammen aus
den offiziellen NuGet-Paketen 10.0.13. Das Windows-Desktop-Runtime-Paket enthält
selbst keinen separaten `THIRD-PARTY-NOTICES`-Eintrag; deshalb liegt hier der
inhaltlich gleiche offizielle Hinweistext aus dem WPF-Tag `v10.0.13` bei.

Die Dateien werden bei einer Änderung der mitgelieferten .NET-Runtime-Version
bewusst aktualisiert. Der Build bricht ab, wenn die Runtime-Binärdateien nicht
mehr zur dokumentierten Hinweisversion passen.

Projektquellen:

- <https://github.com/dotnet/runtime>
- <https://github.com/dotnet/wpf>
- <https://github.com/google/material-design-icons>
