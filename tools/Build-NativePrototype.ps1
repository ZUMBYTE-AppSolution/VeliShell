[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath, [string]$FixturePath, [ValidateSet('x64','x86')][string]$Architecture = 'x64', [string]$BridgePath, [string]$MaterialCheckPath)
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
$env:INCLUDE = (Join-Path $taskVc.FullName 'include') + ';' + ((@('ucrt','shared','um','winrt','cppwinrt') | ForEach-Object { Join-Path $taskSdk.FullName $_ }) -join ';')
$env:LIB = (Join-Path $taskVc.FullName "lib\$Architecture") + ';' + (Join-Path $taskSdkRoot "Lib\$($taskSdk.Name)\ucrt\$Architecture") + ';' + (Join-Path $taskSdkRoot "Lib\$($taskSdk.Name)\um\$Architecture")
$taskCompilerDirectory = Join-Path $taskVc.FullName "bin\Hostx64\$Architecture"
$env:PATH = $taskCompilerDirectory + ';' + $env:PATH
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$taskOutputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $taskOutputDirectory -Force | Out-Null
$taskObjectDirectory = Join-Path $taskOutputDirectory "native-build-$Architecture"
New-Item -ItemType Directory -Path $taskObjectDirectory -Force | Out-Null
$taskSource = Join-Path $taskRoot 'native\VeliShell.NativeFrame\NativeFrame.cpp'
& (Join-Path $taskCompilerDirectory 'cl.exe') /nologo /std:c++20 /O2 /LD /MT /W4 /WX /EHsc /guard:cf /DUNICODE /D_UNICODE /Brepro $taskSource (Join-Path $taskRoot 'native\VeliShell.NativeFrame\ClassicFrame.cpp') "/Fo$taskObjectDirectory\" /link "/OUT:$OutputPath" "/IMPLIB:$taskObjectDirectory\NativeFrame.lib" /DYNAMICBASE /NXCOMPAT /CETCOMPAT user32.lib gdi32.lib uxtheme.lib advapi32.lib comctl32.lib dwmapi.lib d3d11.lib d2d1.lib dwrite.lib dxguid.lib CoreMessaging.lib windowsapp.lib
if ($LASTEXITCODE -ne 0) { throw 'Native prototype build failed.' }
if ($FixturePath) {
    $FixturePath = [IO.Path]::GetFullPath($FixturePath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $FixturePath) -Force | Out-Null
    & (Join-Path $taskCompilerDirectory 'cl.exe') /nologo /O2 /MT /W4 /WX /EHsc /guard:cf (Join-Path $taskRoot 'tests\VeliShell.InteractionQa\ClassicFixture.cpp') "/Fo$taskObjectDirectory\ClassicFixture.obj" /link "/OUT:$FixturePath" /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /CETCOMPAT user32.lib gdi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Independent classic QA fixture build failed.' }
}
if ($BridgePath) {
    $BridgePath = [IO.Path]::GetFullPath($BridgePath)
    & (Join-Path $taskCompilerDirectory 'cl.exe') /nologo /std:c++20 /O2 /MT /W4 /WX /EHsc /guard:cf /DUNICODE /D_UNICODE (Join-Path $taskRoot 'native\VeliShell.NativeFrame\FrameBridge.cpp') "/Fo$taskObjectDirectory\FrameBridge.obj" /link "/OUT:$BridgePath" /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT /CETCOMPAT "$taskObjectDirectory\NativeFrame.lib" user32.lib dwmapi.lib shell32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native compatibility bridge build failed.' }
}
if ($MaterialCheckPath) {
    $MaterialCheckPath = [IO.Path]::GetFullPath($MaterialCheckPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $MaterialCheckPath) -Force | Out-Null
    & (Join-Path $taskCompilerDirectory 'cl.exe') /nologo /std:c++20 /O2 /MT /W4 /WX /EHsc /guard:cf /DUNICODE /D_UNICODE (Join-Path $taskRoot 'tests\VeliShell.InteractionQa\FrameMaterialChecks.cpp') "/Fo$taskObjectDirectory\FrameMaterialChecks.obj" /link "/OUT:$MaterialCheckPath" /SUBSYSTEM:CONSOLE /DYNAMICBASE /NXCOMPAT /CETCOMPAT "$taskObjectDirectory\NativeFrame.lib" user32.lib gdi32.lib dwmapi.lib d3d11.lib d2d1.lib dxguid.lib CoreMessaging.lib windowsapp.lib
    if ($LASTEXITCODE -ne 0) { throw 'Background-only native material check build failed.' }
}
