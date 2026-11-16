# Offline tests for Ledger's pure logic (license parsing/matching, pagination, binary KeyValues).
# No Playnite needed: loads the built Ledger.dll and calls internals via reflection.
#   powershell -ExecutionPolicy Bypass -File extensions/Ledger/tests/run-tests.ps1
# Exit code = number of failures.
$ErrorActionPreference = "Stop"
$dll = Join-Path $PSScriptRoot "..\bin\Release\net462\Ledger.dll"
$sdk = Join-Path $PSScriptRoot "..\bin\Release\net462\Playnite.SDK.dll"
[Reflection.Assembly]::LoadFrom($sdk) | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($dll)
$bf = [Reflection.BindingFlags]"Static,Public,NonPublic"
$LP = $asm.GetType("Ledger.LicenseParsing")
$KV = $asm.GetType("Ledger.SteamLocalStats")
$fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS $name" } else { Write-Host "FAIL $name" -ForegroundColor Red; $script:fail++ }
}
function Call($type, $method, $argz) { return ,($type.GetMethod($method, $bf).Invoke($null, $argz)) }

# --- parsing ---
$html = [string][IO.File]::ReadAllText((Join-Path $PSScriptRoot "licenses-sample.html"))
$lic = Call $LP "Parse" @($html)
Check "parses all rows" ($lic.Count -eq 10)
Check "remove-link div stripped" (($lic | ? { $_.Name -eq "Guns 'n Goblins Demo" }).Count -eq 1)
Check "html entities decoded" (($lic | ? { $_.Name -eq "Hades & Friends" }).Count -eq 1)
Check "both date orders" ((($lic | ? Name -eq "Portal 2").Date.ToString("yyyy-MM-dd")) -eq "2019-03-03")
Check "next page url" ((Call $LP "NextPageUrl" @($html, "https://store.steampowered.com/account/licenses/")) -eq "https://store.steampowered.com/account/licenses/?continuationToken=1:2&offset=100")
Check "last page has no next" ($null -eq (Call $LP "NextPageUrl" @("<html></html>", "https://x/")))

# --- matching ---
$names = [Collections.Generic.List[string]]@("Gunfire Reborn", "Portal", "Portal 2", "Borderlands 3", "Dota 2",
    "Hades", "Celeste", "Fallout", "BioShock Infinite", "Guns 'n Goblins Demo", "Fallout 3 - Game of the Year Edition")
$m = Call $LP "Match" @($names, $lic)
function Matched($g) { if ($m.ContainsKey($g)) { $m[$g].Name } else { $null } }
Check "exact"                       ((Matched "Gunfire Reborn") -eq "Gunfire Reborn")
Check "edition suffix"              ((Matched "Borderlands 3") -eq "Borderlands 3: Deluxe Edition")
Check "promo package noise"         ((Matched "Dota 2") -eq "Dota 2 - Limited Free Promotional Package")
Check "Portal not stealing Portal 2" ((Matched "Portal") -eq "Portal")
Check "digit guard (Fallout !76)"   ((Matched "Fallout") -ne "Fallout 76 Free Weekend")
Check "collection franchise"        ((Matched "BioShock Infinite") -eq "BioShock The Collection")
Check "demo exact"                  ((Matched "Guns 'n Goblins Demo") -eq "Guns 'n Goblins Demo")
Check "game longer than license"    ((Matched "Fallout 3 - Game of the Year Edition") -eq "Fallout 3 - Game of the Year")
Check "short names exact only"      ($null -eq (Matched "Hades"))
Check "no false positive"           ($null -eq (Matched "Celeste"))

# --- binary KeyValues ---
# { "cache" { "1" { "AchievementTimes" { "0" int 1600000000 , "1" int 1700000000 } } } }
$ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ms)
function K($t, $k) { $w.Write([byte]$t); $w.Write([Text.Encoding]::UTF8.GetBytes($k)); $w.Write([byte]0) }
K 0 "cache"; K 0 "1"; K 0 "AchievementTimes"; K 2 "0"; $w.Write([int]1600000000); K 2 "1"; $w.Write([int]1700000000)
$w.Write([byte]8); $w.Write([byte]8); $w.Write([byte]8); $w.Write([byte]8)
$tree = Call $KV "ReadKv" @(,$ms.ToArray())
$times = $tree["cache"]["1"]["AchievementTimes"]
Check "kv nested read" ($times["0"] -eq 1600000000 -and $times["1"] -eq 1700000000)

# --- snapshot compaction: full + delta overlay ---
$SN = $asm.GetType("Ledger.Snapshots")
$tmp = [string](Join-Path ([IO.Path]::GetTempPath()) ("ledger-snap-" + [guid]::NewGuid()))
New-Item -ItemType Directory $tmp | Out-Null
$a = "11111111-1111-1111-1111-111111111111"; $b = "22222222-2222-2222-2222-222222222222"
Set-Content -Encoding UTF8 (Join-Path $tmp "2026-11-16.jsonl") @(
  "{`"takenAt`":`"x`",`"id`":`"$a`",`"playtimeSec`":10}",
  "{`"takenAt`":`"x`",`"id`":`"$b`",`"playtimeSec`":20}")
Set-Content -Encoding UTF8 (Join-Path $tmp "2026-11-17.d.jsonl") @("{`"takenAt`":`"y`",`"id`":`"$b`",`"playtimeSec`":99}")
$state = Call $SN "ReconstructLatest" @($tmp, $null)
Check "delta overlays changed row" ($state[$b] -like '*"playtimeSec":99*')
Check "delta keeps unchanged row" ($state[$a] -like '*"playtimeSec":10*')
$prev = Call $SN "ReconstructLatest" @($tmp, "2026-11-17")
Check "exclude day = previous state" ($prev[$b] -like '*"playtimeSec":20*')
Remove-Item -Recurse -Force $tmp

# --- catalogue names → titles ---
$TN = $asm.GetType("Ledger.TidyNames")
Check "article moved"     ((Call $TN "CatalogueToTitle" @("Simpsons, The - Hit & Run")) -eq "The Simpsons: Hit & Run")
Check "plain article"     ((Call $TN "CatalogueToTitle" @("Simpsons Skateboarding, The")) -eq "The Simpsons Skateboarding")
Check "subtitle colon"    ((Call $TN "CatalogueToTitle" @("Sly 2 - Band of Thieves")) -eq "Sly 2: Band of Thieves")
Check "untouched"         ((Call $TN "CatalogueToTitle" @("Shrek 2")) -eq "Shrek 2")

Write-Host "`n$fail failure(s)"
exit $fail
