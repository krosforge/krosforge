param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
# Windows PowerShell's .NET Framework compiler creates a standalone test fixture.
Add-Type -Path (Join-Path $PSScriptRoot 'UpdateFailureProbe.cs') -OutputAssembly $OutputPath -OutputType ConsoleApplication
