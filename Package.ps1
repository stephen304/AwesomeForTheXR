[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$build = Join-Path $PSScriptRoot 'artifacts/patcher/bin/AwesomePatcher/release'
$release = Join-Path $PSScriptRoot 'artifacts/release'
$patcher = Join-Path $build 'AwesomePatcher.exe'
if (!(Test-Path -LiteralPath $patcher -PathType Leaf)) {
    throw 'Release patcher missing. Run .\Build.ps1 first.'
}

$work = Join-Path $release ([Guid]::NewGuid().ToString('N'))
$merged = Join-Path $work 'AwesomePatcher.exe'
$stage = Join-Path $work 'package.tmp'
$output = Join-Path $release 'AwesomePatcher.exe'
$core = Join-Path $build 'AwesomePatcher.Core.dll'
$cecil = Join-Path $build 'Mono.Cecil.dll'
$module = $null

function Add-BundleFile([string] $name, [byte[]] $bytes) {
    $resource = [Mono.Cecil.EmbeddedResource]::new(
        "AwesomePatcher.Bundle.$name", [Mono.Cecil.ManifestResourceAttributes]::Private, $bytes)
    $module.Resources.Add($resource)
}

Push-Location $PSScriptRoot
try {
    & dotnet tool restore | Out-Host
    if ($LASTEXITCODE) { throw 'Could not restore the pinned ILRepack packaging tool.' }

    # Resolve framework references using the same targeting pack as the Release build.
    $project = 'patcher/src/AwesomePatcher/AwesomePatcher.csproj'
    $frameworkDirs = & dotnet msbuild $project -nologo -p:Configuration=Release -target:GetReferenceAssemblyPaths -getProperty:TargetFrameworkDirectory
    if ($LASTEXITCODE) { throw 'Could not locate the patcher targeting assemblies.' }
    $libraries = $frameworkDirs -split ';' | Where-Object { $_ } | ForEach-Object { '/lib:' + $_.TrimEnd('\', '/') }

    [IO.Directory]::CreateDirectory($work) | Out-Null
    & dotnet tool run ilrepack -- /ndebug /skipconfig /noRepackRes @libraries "/out:$merged" $patcher $core $cecil | Out-Host
    if ($LASTEXITCODE) { throw 'ILRepack could not merge the Release patcher.' }

    [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecil)) | Out-Null
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($merged)
    if (!$module.GetType('AwesomePatcher.PackagePayload')) {
        throw 'Rebuild the Release patcher with .\Build.ps1 before packaging.'
    }
    foreach ($name in 'AwesomeForTheXR32.dll', 'Awesome_DirectToRift.exe', 'AwesomeStartup.bin', 'openxr_loader.dll') {
        $path = Join-Path $build "patch/$name"
        $bytes = [IO.File]::ReadAllBytes($path)
        $hashBytes = [IO.File]::ReadAllBytes("$path.sha256")
        $expected = [Text.Encoding]::ASCII.GetString($hashBytes).Trim()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
        finally { $sha.Dispose() }
        if (!$bytes.Length -or $expected -ne $actual) { throw "Release payload failed its SHA-256 check: $name" }
        Add-BundleFile $name $bytes
        Add-BundleFile "$name.sha256" $hashBytes
    }

    $writer = [Mono.Cecil.WriterParameters]::new()
    $writer.DeterministicMvid = $true
    $writer.Timestamp = 0
    $module.Write($stage, $writer)
    if ([IO.File]::Exists($output)) {
        [IO.File]::Replace($stage, $output, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [IO.File]::Move($stage, $output)
    }
    Write-Output $output
} finally {
    Pop-Location
    if ($module) { $module.Dispose() }
    if ([IO.Directory]::Exists($work)) {
        [IO.File]::Delete($stage)
        [IO.File]::Delete($merged)
        [IO.Directory]::Delete($work)
    }
}
