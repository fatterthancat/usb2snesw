$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot '..\usb2snesmemoryviewer\usb2snesmemoryviewer.cs'
$source = Get-Content -Raw -LiteralPath $sourcePath

$failures = @()

if ($source -match 'ws://localhost:8080/') {
    $failures += 'MemoryViewer still targets legacy websocket port 8080.'
}

if ($source -notmatch 'ws://localhost:23074/') {
    $failures += 'MemoryViewer does not target SNI usb2snes websocket port 23074.'
}

if ($source -match 'OpcodeType\.PutAddress') {
    $failures += 'MemoryViewer still contains PutAddress write paths.'
}

if ($source -match '_provider\.Changed\s*\+=') {
    $failures += 'MemoryViewer still subscribes the hex editor to write-back events.'
}

if ($source -notmatch 'hexBox\.ReadOnly\s*=\s*true\s*;') {
    $failures += 'MemoryViewer does not force the hex editor read-only.'
}

if ($failures.Count -gt 0) {
    Write-Host 'MemoryViewer read-only safety contract FAILED:' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'MemoryViewer read-only safety contract passed.' -ForegroundColor Green
