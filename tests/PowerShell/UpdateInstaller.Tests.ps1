param(
    [Parameter(Mandatory=$true)][string]$CurrentPackage,
    [Parameter(Mandatory=$true)][string]$CandidatePackage
)
$ErrorActionPreference = 'Stop'
$Owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$Root = Join-Path $env:TEMP ('krosforge-update-test-' + [Guid]::NewGuid().ToString('N'))
$Stages = [Collections.Generic.List[string]]::new()
New-Item $Root -ItemType Directory | Out-Null
function Wait-Window($Process) {
    $Deadline = [DateTime]::UtcNow.AddSeconds(30)
    do { Start-Sleep -Milliseconds 100; $Process.Refresh() } while (-not $Process.HasExited -and $Process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $Deadline)
    if ($Process.HasExited -or $Process.MainWindowHandle -eq 0) { throw 'Launcher window did not open.' }
}
function Check-Update([bool]$FailRestart, [bool]$Cancel = $false) {
    Write-Host ('Testing update; expected rollback: ' + $FailRestart + '; cancel: ' + $Cancel)
    $Install = Join-Path $Root ([Guid]::NewGuid().ToString('N'))
    New-Item $Install -ItemType Directory | Out-Null
    $Target = Join-Path $Install 'Krosforge.exe'
    Copy-Item (Join-Path $CurrentPackage 'Krosforge.exe') $Target
    $Data = Join-Path $Install 'Krosforge-data'
    New-Item $Data -ItemType Directory | Out-Null
    $Sentinel = Join-Path $Data 'preserve.txt'
    Set-Content $Sentinel 'profile-state'
    $BeforeData = (Get-FileHash $Sentinel).Hash
    $Original = (Get-FileHash $Target).Hash.ToLowerInvariant()
    $Parent = Start-Process $Target -WorkingDirectory $Install -PassThru
    $Owned.Add($Parent)
    Wait-Window $Parent
    Start-Sleep -Seconds 2
    $Stage = Join-Path (Join-Path $env:TEMP 'KrosforgeUpdates') ([Guid]::NewGuid().ToString('N'))
    New-Item $Stage -ItemType Directory -Force | Out-Null
    $Stages.Add($Stage)
    $Candidate = Join-Path $Stage 'Krosforge.exe'
    if ($FailRestart) { [IO.File]::WriteAllBytes($Candidate, [Text.Encoding]::UTF8.GetBytes('not-an-executable')) }
    else { Copy-Item (Join-Path $CandidatePackage 'Krosforge.exe') $Candidate }
    $Expected = (Get-FileHash $Candidate).Hash.ToLowerInvariant()
    Copy-Item $Target (Join-Path $Stage 'UpdateHost.exe')
    $Plan = @{
        Target=$Target; CandidateHash=$Expected; CurrentHash=$Original;
        ParentId=$Parent.Id; ParentStartedUtcTicks=$Parent.StartTime.ToUniversalTime().Ticks; Installed=$false
    }
    $Plan | ConvertTo-Json | Set-Content (Join-Path $Stage 'update.json') -Encoding UTF8
    $Helper = Start-Process (Join-Path $Stage 'UpdateHost.exe') -ArgumentList @('--apply-update', ('"' + $Stage + '"')) -PassThru
    $Owned.Add($Helper)
    $Deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path (Join-Path $Stage 'ready'))) {
        if ($Helper.HasExited -or [DateTime]::UtcNow -ge $Deadline) { throw 'Update helper did not become ready.' }
        Start-Sleep -Milliseconds 100
    }
    if ($Cancel) {
        Set-Content (Join-Path $Stage 'cancel') ''
        if (-not $Helper.WaitForExit(10000) -or $Helper.ExitCode -ne 3) { throw 'Helper did not cancel.' }
        if ($Parent.HasExited -or (Get-FileHash $Target).Hash.ToLowerInvariant() -ne $Original) { throw 'Canceled update affected the running installation.' }
        $null = $Parent.CloseMainWindow()
        if (-not $Parent.WaitForExit(40000)) { throw 'Original launcher did not close.' }
        return
    }
    $null = $Parent.CloseMainWindow()
    if (-not $Parent.WaitForExit(40000)) { throw 'Launcher did not close for the update.' }
    if (-not $Helper.WaitForExit(60000)) { throw ('Update helper did not finish. State: ' + (Get-Content (Join-Path $Stage 'state') -ErrorAction SilentlyContinue)) }
    if ($FailRestart) {
        if ($Helper.ExitCode -ne 4) { throw ('Expected rollback, got exit code ' + $Helper.ExitCode) }
        if ((Get-FileHash $Target).Hash.ToLowerInvariant() -ne $Original) { throw 'Previous executable was not restored.' }
    } else {
        if ($Helper.ExitCode -ne 0) { throw ('Update failed: ' + $Helper.ExitCode) }
        if ((Get-FileHash $Target).Hash.ToLowerInvariant() -ne $Expected) { throw 'Updated executable does not match.' }
    }
    if ((Get-FileHash $Sentinel).Hash -ne $BeforeData) { throw 'User data changed.' }
    $Deadline = [DateTime]::UtcNow.AddSeconds(20)
    $Restarted = $null
    while ($null -eq $Restarted -and [DateTime]::UtcNow -lt $Deadline) {
        foreach ($Info in @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $Target })) {
            $CandidateProcess = Get-Process -Id $Info.ProcessId
            if ($CandidateProcess.MainWindowHandle -ne 0) { $Restarted = $CandidateProcess; break }
            $CandidateProcess.Dispose()
        }
        if ($null -eq $Restarted) { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $Restarted) { throw 'Updated or restored launcher did not reopen.' }
    $Owned.Add($Restarted)
    $null = $Restarted.CloseMainWindow()
    if (-not $Restarted.WaitForExit(20000)) { throw 'Restarted launcher did not close.' }
    if (-not $FailRestart -and (Get-ChildItem $Install -Filter '.krosforge-backup-*.exe')) { throw 'Backup was not cleaned after acknowledged startup.' }
}
try {
    Check-Update $false $true
    Check-Update $false
    Check-Update $true
    Write-Host 'PASS: Windows helper cancels, installs, waits for window acknowledgement, preserves data, and rolls back a failed restart.'
} finally {
    foreach ($Process in $Owned) {
        if (-not $Process.HasExited) { $Process.Kill(); $Process.WaitForExit(5000) | Out-Null }
        $Process.Dispose()
    }
    # Runtime children are limited to exact executable paths under this test's owned directory.
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
    foreach ($Stage in $Stages) { if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force } }
}
