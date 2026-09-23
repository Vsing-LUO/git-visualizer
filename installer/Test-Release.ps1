# Copyright 2026 赵泽璇
# SPDX-License-Identifier: Apache-2.0

# Use isolated data and a test-only credential prefix, never production data.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-CleanUninstall.ps1')
