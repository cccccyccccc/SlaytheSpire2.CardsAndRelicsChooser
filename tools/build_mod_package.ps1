param(
    [string]$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch]$BuildDll,
    [switch]$DeployToMods
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Align([uint64]$value, [uint64]$alignment) {
    if ($alignment -eq 0) { return $value }
    $r = $value % $alignment
    if ($r -eq 0) { return $value }
    return $value + ($alignment - $r)
}

function Write-Zeros([System.IO.BinaryWriter]$bw, [uint64]$count) {
    if ($count -eq 0) { return }
    $chunk = New-Object byte[] 4096
    $remaining = [uint64]$count
    while ($remaining -gt 0) {
        $toWrite = [int][Math]::Min([uint64]$chunk.Length, $remaining)
        $bw.Write($chunk, 0, $toWrite)
        $remaining -= [uint64]$toWrite
    }
}

$manifestPath = Join-Path $ProjectDir 'mod_manifest.json'
if (-not (Test-Path $manifestPath)) {
    throw "mod_manifest.json not found: $manifestPath"
}

$manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
$pckName = [string]$manifest.pck_name
if ([string]::IsNullOrWhiteSpace($pckName)) {
    throw "pck_name missing in mod_manifest.json"
}

if ($BuildDll) {
    Push-Location $ProjectDir
    try {
        dotnet build .\StartHandPickerMod.csproj -c Release
    }
    finally {
        Pop-Location
    }
}

$entries = New-Object System.Collections.Generic.List[object]

function Add-Entry([string]$virtualPath, [string]$sourcePath) {
    $bytes = [System.IO.File]::ReadAllBytes($sourcePath)

    $md5Alg = [System.Security.Cryptography.MD5]::Create()
    try {
        $md5 = $md5Alg.ComputeHash($bytes)
    }
    finally {
        $md5Alg.Dispose()
    }

    $entries.Add([PSCustomObject]@{
        Name   = $virtualPath
        Data   = $bytes
        Size   = [uint64]$bytes.Length
        Md5    = $md5
        Offset = [uint64]0
    }) | Out-Null
}

Add-Entry 'mod_manifest.json' $manifestPath

$imagePath = Join-Path $ProjectDir 'mod_image.png'
if (Test-Path $imagePath) {
    Add-Entry 'mod_image.png' $imagePath
    Add-Entry "$pckName/mod_image.png" $imagePath
}

$dataOffset = [uint64]0x80
$cursor = [uint64]0
foreach ($e in $entries) {
    $e.Offset = $cursor
    $cursor = Align ($cursor + $e.Size) 16
}

$tableOffset = $dataOffset + $cursor

$distDir = Join-Path $ProjectDir 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$pckPath = Join-Path $distDir ("{0}.pck" -f $pckName)

$fs = [System.IO.File]::Open($pckPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $bw = New-Object System.IO.BinaryWriter($fs)

    # Header
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes('GDPC'))
    $bw.Write([uint32]3)  # pack format
    $bw.Write([uint32]4)  # engine major
    $bw.Write([uint32]5)  # engine minor
    $bw.Write([uint32]1)  # engine patch
    $bw.Write([uint32]2)  # flags
    $bw.Write([uint64]$dataOffset)
    $bw.Write([uint64]$tableOffset)

    $headerRemain = $dataOffset - [uint64]$fs.Position
    Write-Zeros $bw $headerRemain

    # Data block
    foreach ($e in $entries) {
        $targetPos = $dataOffset + $e.Offset
        if ([uint64]$fs.Position -lt $targetPos) {
            Write-Zeros $bw ($targetPos - [uint64]$fs.Position)
        }

        $bw.Write([byte[]]$e.Data)
        $nextPos = $dataOffset + (Align ($e.Offset + $e.Size) 16)
        if ([uint64]$fs.Position -lt $nextPos) {
            Write-Zeros $bw ($nextPos - [uint64]$fs.Position)
        }
    }

    if ([uint64]$fs.Position -lt $tableOffset) {
        Write-Zeros $bw ($tableOffset - [uint64]$fs.Position)
    }

    # File table
    $bw.Write([uint32]$entries.Count)
    foreach ($e in $entries) {
        $nameBytes = [System.Text.Encoding]::UTF8.GetBytes([string]$e.Name)
        $nameLenPadded = [uint32](Align ([uint64]$nameBytes.Length) 4)

        $bw.Write([uint32]$nameLenPadded)
        $bw.Write($nameBytes)
        $namePad = [uint64]$nameLenPadded - [uint64]$nameBytes.Length
        Write-Zeros $bw $namePad

        $bw.Write([uint64]$e.Offset)
        $bw.Write([uint64]$e.Size)
        $bw.Write([byte[]]$e.Md5)
        $bw.Write([uint32]0)
    }

    $bw.Flush()
}
finally {
    $fs.Dispose()
}

Write-Host "PCK built:" $pckPath

if ($DeployToMods) {
    $modsRoot = Join-Path (Split-Path $ProjectDir -Parent -Resolve) '..\\mods'
    $modsRoot = (Resolve-Path $modsRoot).Path
    $modOutDir = Join-Path $modsRoot $pckName
    New-Item -ItemType Directory -Force -Path $modOutDir | Out-Null

    Copy-Item -Force $pckPath (Join-Path $modOutDir ("{0}.pck" -f $pckName))

    $dllPath = Join-Path $ProjectDir 'bin\Release\net9.0\StartHandPickerMod.dll'
    if (Test-Path $dllPath) {
        Copy-Item -Force $dllPath (Join-Path $modOutDir 'StartHandPickerMod.dll')
    }

    Write-Host "Deployed to:" $modOutDir
}

