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
    if ($LASTEXITCODE) { throw 'Native patch configuration failed.' }
    & $cmakeExe --build artifacts/runtime/build --config $Configuration --target AwesomeForTheXR32 AwesomeDirectForwarder AwesomeStartup
    if ($LASTEXITCODE) { throw 'Native patch build failed.' }
    & dotnet build patcher/AwesomeForTheXR.Patcher.sln --configuration $Configuration -p:RestoreLockedMode=true
    if ($LASTEXITCODE) { throw 'Managed patcher build failed.' }

    $native = Join-Path $root "artifacts/runtime/build/plugin/$Configuration/AwesomeForTheXR32.dll"
    $forwarder = Join-Path $root "artifacts/runtime/build/forwarder/$Configuration/Awesome_DirectToRift.exe"
    $startupDir = Join-Path $root "artifacts/runtime/build/startup/$Configuration"
    $startup = Join-Path $startupDir 'AwesomeStartup.bin'
    & (Join-Path $root 'runtime/startup/Export-Startup.ps1') -Image (Join-Path $startupDir 'AwesomeStartup.exe') -Output $startup
    $executables = [ordered]@{
        WinForms = 'AwesomeForTheXR-Patcher.exe'
        Cli = 'AwesomeForTheXR-Patcher.Cli.exe'
    }
    foreach ($frontend in $executables.Keys) {
        $output = Join-Path $root ("artifacts/patcher/bin/AwesomeForTheXR.Patcher.$frontend/" + $Configuration.ToLowerInvariant())
        $payload = Join-Path $output 'patch'
        [IO.Directory]::CreateDirectory($payload) | Out-Null
        foreach ($source in @($native, $forwarder, $startup)) {
            $destination = Join-Path $payload ([IO.Path]::GetFileName($source))
            Copy-Item -LiteralPath $source -Destination $destination -Force
            $digest = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            [IO.File]::WriteAllText("$destination.sha256", $digest, [Text.Encoding]::ASCII)
        }
        Write-Output (Join-Path $output $executables[$frontend])
    }
} finally {
    Pop-Location
}
