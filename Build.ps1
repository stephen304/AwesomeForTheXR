[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cmake = Get-Command cmake -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cmake) {
    $cmakeExe = $cmake.Source
} else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $cmakeExe = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.CMake.Project -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
    }
}
if (!$cmakeExe) { throw 'Install CMake and the Visual Studio 2022 Desktop development with C++ tools for Win32.' }

Push-Location $root
try {
    & $cmakeExe -S runtime -B artifacts/runtime/build -G 'Visual Studio 17 2022' -A Win32
    if ($LASTEXITCODE) { throw 'Native marker configuration failed.' }
    & $cmakeExe --build artifacts/runtime/build --config $Configuration --target AwesomeForTheXR32
    if ($LASTEXITCODE) { throw 'Native marker build failed.' }
    & dotnet build patcher/AwesomeForTheXR.Patcher.sln --configuration $Configuration -p:RestoreLockedMode=true
    if ($LASTEXITCODE) { throw 'Managed patcher build failed.' }

    $native = Join-Path $root "artifacts/runtime/build/plugin/$Configuration/AwesomeForTheXR32.dll"
    $gui = Join-Path $root ("artifacts/patcher/bin/AwesomeForTheXR.Patcher.WinForms/" + $Configuration.ToLowerInvariant())
    $payload = Join-Path $gui 'patch'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    $plugin = Join-Path $payload 'AwesomeForTheXR32.dll'
    Copy-Item -LiteralPath $native -Destination $plugin -Force
    $digest = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
    [IO.File]::WriteAllText("$plugin.sha256", $digest, [Text.Encoding]::ASCII)
    Write-Output (Join-Path $gui 'AwesomeForTheXR-Patcher.exe')
} finally {
    Pop-Location
}
