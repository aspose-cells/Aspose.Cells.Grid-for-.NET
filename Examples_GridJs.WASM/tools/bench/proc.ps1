param([string]$name)
$procs = Get-Process -Name $name -ErrorAction SilentlyContinue
$ws = 0; $priv = 0; $cpu = 0; $cnt = 0
foreach ($i in $procs) { $ws += $i.WorkingSet64; $priv += $i.PrivateMemorySize64; $cpu += $i.CPU; $cnt++ }
"{0}|{1}|{2}|{3}" -f [math]::Round($ws / 1MB, 1), [math]::Round($priv / 1MB, 1), $cnt, [math]::Round($cpu, 2)
