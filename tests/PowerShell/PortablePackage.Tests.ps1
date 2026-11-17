param([Parameter(Mandatory=$true)][string]$PackageRoot, [switch]$WithWindow)
$ErrorActionPreference = 'Stop'
$Files = @(Get-ChildItem $PackageRoot -File -Recurse)
if ($Files.Count -ne 1 -or $Files[0].Name -ne 'Krosforge.exe') { throw 'Conteudo inesperado no pacote portatil.' }
$TestRoot = Join-Path $env:TEMP ('krosforge-portable-' + [Guid]::NewGuid().ToString('N'))
$Owned = [Collections.Generic.List[Diagnostics.Process]]::new()
Copy-Item $PackageRoot $TestRoot -Recurse
function Read-Exact($Stream, [int]$Length) {
    $Buffer = New-Object byte[] $Length
    $Offset = 0
    while ($Offset -lt $Length) {
        $Task = $Stream.ReadAsync($Buffer, $Offset, $Length - $Offset)
        if (-not $Task.Wait(15000)) { throw 'Timeout lendo resposta.' }
        $Count = $Task.Result
        if ($Count -eq 0) { throw 'Conexao encerrada sem resposta.' }
        $Offset += $Count
    }
    return ,$Buffer
}
function Request($Pipe, $Message) {
    $Bytes = [Text.Encoding]::UTF8.GetBytes(($Message | ConvertTo-Json -Compress))
    $Header = [BitConverter]::GetBytes([int]$Bytes.Length)
    $Pipe.Write($Header, 0, 4); $Pipe.Write($Bytes, 0, $Bytes.Length); $Pipe.Flush()
    $Length = [BitConverter]::ToInt32((Read-Exact $Pipe 4), 0)
    if ($Length -le 0 -or $Length -gt 65536) { throw 'Resposta invalida.' }
    $Response = [Text.Encoding]::UTF8.GetString((Read-Exact $Pipe $Length)) | ConvertFrom-Json
    if (-not $Response.Success) { throw $Response.Error }
    return $Response.Data
}
function Check-Runtime([bool]$Create) {
    $Directory = [IO.Path]::GetFullPath((Join-Path $TestRoot 'Krosforge-data'))
    $Hasher = [Security.Cryptography.SHA256]::Create()
    try { $Hex = ([BitConverter]::ToString($Hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($Directory)))).Replace('-', '') } finally { $Hasher.Dispose() }
    $Start = [Diagnostics.ProcessStartInfo]::new((Join-Path $TestRoot 'Krosforge.exe'))
    $Start.UseShellExecute = $false; $Start.CreateNoWindow = $true
    $Start.Arguments = '--runtime'
    $Process = [Diagnostics.Process]::Start($Start); $Owned.Add($Process)
    $Pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('krosforge-' + $Hex.Substring(0, 24)), [IO.Pipes.PipeDirection]::InOut)
    try {
        $Pipe.Connect(15000)
        if ($Create) { $null = Request $Pipe @{Version=1;Operation='create';Value='Portable test'} }
        $Snapshot = Request $Pipe @{Version=1;Operation='snapshot'}
        if ($Snapshot.Profiles.Count -ne 1 -or $Snapshot.Profiles[0].Name -ne 'Portable test') { throw 'Perfil nao persistiu.' }
        $null = Request $Pipe @{Version=1;Operation='shutdown'}
        if (-not $Process.WaitForExit(15000) -or $Process.ExitCode -ne 0) { throw 'Runtime nao encerrou corretamente.' }
    } finally { $Pipe.Dispose() }
}
try {
    Check-Runtime $true
    $Moved = $TestRoot + '-moved'
    Move-Item $TestRoot $Moved
    $TestRoot = $Moved
    Check-Runtime $false
    if ($WithWindow) {
        $Process = Start-Process (Join-Path $TestRoot 'Krosforge.exe') -WorkingDirectory $TestRoot -PassThru
        $Owned.Add($Process)
        $Deadline = [DateTime]::UtcNow.AddSeconds(20)
        do { Start-Sleep -Milliseconds 300; $Process.Refresh() } while (-not $Process.HasExited -and $Process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $Deadline)
        if ($Process.HasExited -or $Process.MainWindowHandle -eq 0) { throw 'Janela nao abriu.' }
        Start-Sleep -Seconds 3
        $null = $Process.CloseMainWindow()
        if (-not $Process.WaitForExit(15000)) { throw 'Janela nao encerrou.' }
    }
    if (Get-ChildItem $TestRoot -File | Where-Object Extension -in @('.dll', '.pdb')) { throw 'Bibliotecas soltas ao lado do executavel.' }
    Write-Host 'PASS: executavel unico, SQLite nativo, perfil persistente e pasta portatil movida.'
} finally {
    foreach ($Process in $Owned) { if (-not $Process.HasExited) { $Process.Kill(); $Process.WaitForExit(5000) | Out-Null }; $Process.Dispose() }
    if (Test-Path $TestRoot) { Remove-Item $TestRoot -Recurse -Force }
}
