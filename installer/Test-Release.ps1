# Use isolated data and a test-only credential prefix, never production data.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-CleanUninstall.ps1')
