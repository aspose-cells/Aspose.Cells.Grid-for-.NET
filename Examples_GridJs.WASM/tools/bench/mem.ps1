param([int64]$rootPid, [string]$name = 'chrome.exe')
# Headless Chrome's renderer command lines do not carry --user-data-dir, so the run's processes are
# identified by walking the process tree from the pid we launched.
$all = @(Get-CimInstance Win32_Process -Filter "Name='$name'")
$byParent = @{}
foreach ($p in $all) {
  $pp = [string]$p.ParentProcessId
  if (-not $byParent.ContainsKey($pp)) { $byParent[$pp] = @() }
  $byParent[$pp] += $p.ProcessId
}
$keep = @{}
$q = New-Object 'System.Collections.Generic.Queue[long]'
if ($rootPid -gt 0) { $q.Enqueue($rootPid) }
while ($q.Count -gt 0) {
  $cur = $q.Dequeue()
  if ($keep.ContainsKey($cur)) { continue }
  $keep[$cur] = $true
  foreach ($c in @($byParent[[string]$cur])) { if ($c -and -not $keep.ContainsKey($c)) { $q.Enqueue([long]$c) } }
}
$ws = 0; $priv = 0; $cnt = 0; $cpu = 0
foreach ($id in $keep.Keys) {
  $i = Get-Process -Id $id -ErrorAction SilentlyContinue
  if ($i) { $ws += $i.WorkingSet64; $priv += $i.PrivateMemorySize64; $cpu += $i.CPU; $cnt++ }
}
"{0}|{1}|{2}|{3}" -f [math]::Round($ws / 1MB, 1), [math]::Round($priv / 1MB, 1), $cnt, [math]::Round($cpu, 2)
