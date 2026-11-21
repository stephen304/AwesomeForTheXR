param([Parameter(Mandatory)] [string] $Image, [Parameter(Mandatory)] [string] $Output)
$ErrorActionPreference = 'Stop'
$bytes = [IO.File]::ReadAllBytes($Image)
function U32([int] $offset) { [BitConverter]::ToUInt32($bytes, $offset) }
function U16([int] $offset) { [BitConverter]::ToUInt16($bytes, $offset) }
$pe = U32 60
$opt = $pe + 24
$section = $opt + (U16 ($pe + 20))
if ((U32 $pe) -ne 0x4550 -or (U16 ($pe + 4)) -ne 0x14c -or
    (U16 ($pe + 6)) -ne 1 -or (U16 $opt) -ne 0x10b -or
    [Text.Encoding]::ASCII.GetString($bytes, $section, 5) -ne '.text') {
    throw 'Expected a single Win32 code section in the startup build.'
}
# Imports, relocations, load configuration and other loader work would make
# the section unsafe to transplant. Reject such builds before packaging.
for ($i = 0; $i -lt 16; $i++) {
    if ($i -eq 6) { continue } # Inert linker debug metadata, scrubbed below.
    if ((U32 ($opt + 96 + 8 * $i)) -or (U32 ($opt + 100 + 8 * $i))) {
        throw "Unexpected PE data directory $i in startup build."
    }
}
$rva = U32 ($section + 12)
$size = U32 ($section + 8)
$raw = U32 ($section + 20)
$table = $raw + (U32 ($opt + 16)) - $rva
$engine = (U32 $table) - $rva
$mono = (U32 ($table + 4)) - $rva
$graphics = (U32 ($table + 8)) - $rva
if ($graphics -lt 0 -or $graphics -ge $size -or $engine -ge $size -or $mono -ge $size -or $engine -lt 0 -or $mono -lt 0) {
    throw 'Invalid startup entry table.'
}
$debugRva = U32 ($opt + 96 + 6 * 8)
$debugSize = U32 ($opt + 100 + 6 * 8)
if ($debugSize) {
    $debugStart = $raw + $debugRva - $rva
    if ($debugSize % 28 -or $debugStart -lt $raw -or $debugStart + $debugSize -gt $raw + $size) {
        throw 'Invalid linker debug directory.'
    }
    for ($i = $debugStart; $i -lt $debugStart + $debugSize; $i += 28) {
        $count = U32 ($i + 16)
        $at = U32 ($i + 24)
        if ($count -and $at -ge $raw -and $at + $count -le $raw + $size) {
            [Array]::Clear($bytes, $at, $count)
        }
    }
    [Array]::Clear($bytes, $debugStart, $debugSize)
}
$stream = [IO.File]::Create($Output)
try {
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('AFX2'))
    $writer.Write([int]$engine)
    $writer.Write([int]$mono)
    $writer.Write([int]$graphics)
    $writer.Write($bytes, [int]$raw, [int]$size)
} finally { $stream.Dispose() }
