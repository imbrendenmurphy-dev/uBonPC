# uBonPC regression sweep: boots each game disk listed in test\sweep-list.txt, runs its program for 75 seconds, and
# records the display rate, the CRTC geometry and any crash in test\sweep\sweep.txt (with a screenshot of each).
# sweep-list.txt is local (not in git): a "root=FOLDER" line, then one "DISK|PROGRAM" per line, DISK relative to root.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$listFile = Join-Path $here 'sweep-list.txt'
if (-not (Test-Path $listFile)) { Write-Error "No test\sweep-list.txt: see the comment at the top of sweep.ps1"; exit 1 }
$root = ''; $list = @()
foreach ($line in Get-Content $listFile) {
  $l = $line.Trim()
  if ($l -eq '' -or $l.StartsWith('#')) { continue }
  if ($l.StartsWith('root=')) { $root = $l.Substring(5); continue }
  $list += $l
}
$out = Join-Path $here 'sweep'; New-Item -ItemType Directory -Force $out | Out-Null
cd $out
foreach ($e in $list) {
  $p = $e.Split('|'); $d = Join-Path $root $p[0]; $n = $p[1]
  $o = & ..\..\ubemu.exe -e "crash; disk $d; typeat A> $($n.ToLower())\r; seconds 75; screen $n.png; crtc; fdc; regs" 2>&1 | Out-String
  $crash = if ($o -cmatch 'CRASH\?') { 'CRASH' } else { '' }
  $hz = ([regex]::Match($o, '\(([\d\.]+) Hz\)')).Groups[1].Value
  $geo = ([regex]::Match($o, 'R0=\S+ R1=\S+')).Value
  "$n $hz Hz $geo $crash" | Tee-Object -Append sweep.txt
}