param([Parameter(Mandatory = $true)][string]$AdbPath)
# Do not send a blanket kill-server to the shared adb server on 5037.
# Only an executable inside this installation may be stopped before replacing it.
$expected = [IO.Path]::GetFullPath($AdbPath)
$failed = $false
Get-Process -Name adb -ErrorAction SilentlyContinue | ForEach-Object {
    $candidate = $null
    try { $candidate = $_.Path } catch { } # Unknown ownership: leave it alone.
    if ($candidate -and [string]::Equals($candidate, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        try {
            $idToStop = $_.Id
            Stop-Process -Id $idToStop -Force -ErrorAction Stop
            Wait-Process -Id $idToStop -Timeout 5 -ErrorAction SilentlyContinue
        } catch {
            $failed = $true
            Write-Warning 'Не удалось остановить компонент USB этой установки.'
        }
    }
}
if ($failed) { exit 1 }
exit 0
