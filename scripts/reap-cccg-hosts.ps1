# Reap orphaned CCCG dispatch processes.
#
# An MCP Host (cccg-dispatch.exe) is spawned per Claude session connection and
# should die with it; when the parent process is gone the Host is an orphan
# holding file locks for nothing. One-shot RPC workers should live milliseconds.
#
# Deliberately long-lived processes are NEVER reaped:
#   - cccg-dispatch-worker run-job <id>   (detached background job, survives Host)
#   - cccg-dispatch-worker run-owner ...  (PATH A owner daemon, holds session lease)
#
# Usage: powershell -File scripts\reap-cccg-hosts.ps1 [-DryRun]

param([switch]$DryRun)

$minAgeMinutes = 5
$now = Get-Date
$all = Get-CimInstance Win32_Process -Filter "Name = 'cccg-dispatch.exe' OR Name = 'cccg-dispatch-worker.exe'"
$alivePids = @{}
Get-Process | ForEach-Object { $alivePids[$_.Id] = $true }

$reaped = 0
foreach ($p in $all) {
    $cmd = if ($p.CommandLine) { $p.CommandLine } else { '' }
    if ($cmd -match 'run-job|run-owner') { continue }              # protected long-lived modes
    if ($alivePids.ContainsKey([int]$p.ParentProcessId)) { continue } # parent alive = legit
    $age = $now - $p.CreationDate
    if ($age.TotalMinutes -lt $minAgeMinutes) { continue }        # grace period for races
    $label = "{0} pid={1} parent={2}(dead) age={3:N0}m" -f $p.Name, $p.ProcessId, $p.ParentProcessId, $age.TotalMinutes
    if ($DryRun) { Write-Output "would reap: $label" }
    else {
        try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; Write-Output "reaped: $label"; $reaped++ }
        catch { Write-Output "failed: $label ($($_.Exception.Message))" }
    }
}
Write-Output ("done: {0} reaped" -f $reaped)
