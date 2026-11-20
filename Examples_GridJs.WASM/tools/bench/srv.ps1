param([int]$port)
# The reference server may be started either as its own apphost or through `dotnet foo.dll`, so the
# process is identified by the port it listens on rather than by name.
$conns = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
$seen = @{}
$ws = 0; $priv = 0; $cpu = 0; $cnt = 0
foreach ($c in $conns) {
  $id = [int]$c.OwningProcess
  if ($seen.ContainsKey($id)) { continue }
  $seen[$id] = $true
  $p = Get-Process -Id $id -ErrorAction SilentlyContinue
  if ($p) { $ws += $p.WorkingSet64; $priv += $p.PrivateMemorySize64; $cpu += $p.CPU; $cnt++ }
}
"{0}|{1}|{2}|{3}" -f [math]::Round($ws / 1MB, 1), [math]::Round($priv / 1MB, 1), $cnt, [math]::Round($cpu, 2)
