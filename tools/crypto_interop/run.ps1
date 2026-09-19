# Builds both halves of the bridge's crypto and checks that they agree.
#
# The plugin's crypto.cpp needs Daz to run in place and the client's BridgeCrypto.cs
# needs a headset, so neither gets exercised during ordinary work. This compiles both
# against nothing but QtCore/CNG and the .NET BCL, runs them against the published
# vectors for SHA-256, HMAC-SHA256 and HKDF, and then runs them against each other:
# records sealed on each side and opened on the other, a flipped bit, a replay, and
# the handshake's RSA leg against a key the plugin actually generated.
#
#   NEEDS      Windows. MSVC 2022 (the vcvars64.bat path below), Qt 6 at the
#              hard-coded $qt path below, the .NET SDK, and PowerShell. Both
#              paths are literals -- edit them, they are not discovered.
#   NEEDS NOT  Daz Studio, a built plugin, Unity, or a headset.
#   WHO        Whoever has the Windows build machine. This is the only test the
#              crypto has, so it is worth running whenever crypto.cpp or
#              BridgeCrypto.cs changes -- but an agent on a Linux worktree
#              cannot run it, and changing either file there means the change
#              ships unverified. Say so rather than implying a pass.

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path "$here\..\..").Path
$qt = "C:\Qt\6.10.3\msvc2022_64"
$out = Join-Path $env:TEMP "dazvrbridge-crypto"
New-Item -ItemType Directory -Force $out | Out-Null

# --- the plugin's half. Written to a batch file rather than passed through cmd /c:
# the trailing backslash in a quoted /Fo path escapes the quote otherwise.
$bat = Join-Path $out "build.bat"
@"
@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "$out"
cl /nologo /std:c++17 /Zc:__cplusplus /permissive- /EHsc /MD /W3 /DUNICODE /D_UNICODE ^
  /I"$qt\include" /I"$qt\include\QtCore" /I"$repo\plugin" ^
  /Feplugin_side.exe ^
  "$here\plugin_side.cpp" "$repo\plugin\crypto.cpp" ^
  /link /LIBPATH:"$qt\lib" Qt6Core.lib bcrypt.lib
exit /b %ERRORLEVEL%
"@ | Set-Content -Path $bat -Encoding ASCII

& $bat | Out-String | Write-Host
if ($LASTEXITCODE -ne 0) { Write-Host "plugin side did not build"; exit 1 }

# --- the client's half, compiled against the file that actually ships
$csc = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
& $csc -nologo -langversion:9 -out:"$out\client_side.exe" `
  "$here\ClientSide.cs" `
  "$repo\client\Daz VR Bridge\Assets\DazVrBridge\BridgeCrypto.cs"
if ($LASTEXITCODE -ne 0) { Write-Host "client side did not build"; exit 1 }

# --- run them against each other
$env:PATH = "$qt\bin;$env:PATH"
& "$out\client_side.exe" "$out\plugin_side.exe"
exit $LASTEXITCODE
