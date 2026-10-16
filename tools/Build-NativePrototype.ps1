[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $taskVswhere)) { throw 'Visual C++ Build Tools with Windows SDK are required for the native test module.' }
$taskVs = & $taskVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $taskVs) { throw 'C++ x64 build tools are missing.' }
$taskVc = Get-ChildItem -LiteralPath (Join-Path $taskVs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$taskSdkRoot = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$taskSdk = Get-ChildItem -LiteralPath (Join-Path $taskSdkRoot 'Include') -Directory | Where-Object Name -Match '^10\.0\.' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $taskVc -or -not $taskSdk) { throw 'The x64 compiler or Windows SDK could not be found.' }
# Configure this build subprocess only; no shell nesting or machine settings.
$env:INCLUDE = (Join-Path $taskVc.FullName 'include') + ';' + ((@('ucrt','shared','um','winrt') | ForEach-Object { Join-Path $taskSdk.FullName $_ }) -join ';')
$env:LIB = (Join-Path $taskVc.FullName 'lib\x64') + ';' + (Join-Path $taskSdkRoot "Lib\$($taskSdk.Name)\ucrt\x64") + ';' + (Join-Path $taskSdkRoot "Lib\$($taskSdk.Name)\um\x64")
$taskCompilerDirectory = Join-Path $taskVc.FullName 'bin\Hostx64\x64'
$env:PATH = $taskCompilerDirectory + ';' + $env:PATH
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$taskOutputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $taskOutputDirectory -Force | Out-Null
$taskSource = Join-Path $taskRoot 'native\VeliShell.NativeFrame\NativeFrame.cpp'
& (Join-Path $taskCompilerDirectory 'cl.exe') /nologo /std:c++20 /O2 /LD /MT /W4 /WX /EHsc /guard:cf /DUNICODE /D_UNICODE /Brepro $taskSource "/Fo$taskOutputDirectory\NativeFrame.obj" /link "/OUT:$OutputPath" "/IMPLIB:$taskOutputDirectory\NativeFrame.lib" /DYNAMICBASE /NXCOMPAT /CETCOMPAT user32.lib comctl32.lib dwmapi.lib d3d11.lib d2d1.lib dwrite.lib
if ($LASTEXITCODE -ne 0) { throw 'Native prototype build failed.' }
