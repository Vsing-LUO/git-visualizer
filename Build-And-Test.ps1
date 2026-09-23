# Copyright 2026 赵泽璇
# SPDX-License-Identifier: Apache-2.0

[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & dotnet test GitVisualizer.slnx --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build or tests failed.' }
} finally { Pop-Location }
