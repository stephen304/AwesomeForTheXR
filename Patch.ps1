[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $GameFolder,

    [switch] $y,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'artifacts/patcher/bin/AwesomePatcher'
$patcher = Join-Path $output ($Configuration.ToLowerInvariant() + '/AwesomePatcher.exe')
if (!(Test-Path -LiteralPath $patcher -PathType Leaf)) {
    throw "Build the patcher first with .\Build.ps1 -Configuration $Configuration."
}

$arguments = @('patch')
if ($PSBoundParameters.ContainsKey('GameFolder')) {
    $arguments += '--game-dir', $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($GameFolder)
}
if ($y) { $arguments += '-y' }

& $patcher @arguments
exit $LASTEXITCODE
