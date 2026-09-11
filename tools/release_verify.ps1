# Pre-release gate: run each published exe ISOLATED (copied alone to a new dir)
# and confirm a window appears with rows and no error dialog. ASCII source.
param(
    # Directory holding the published exe files to verify.
    [Parameter(Mandatory = $true)][string]$ReleaseDir,

    # Minimum rows that must be rendered. A window with zero rows is NOT a pass:
    # a crash while drawing rows was missed exactly that way once.
    [int]$MinRows = 1
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Cond($p, $v) { New-Object System.Windows.Automation.PropertyCondition($p, $v) }

$rel = (Resolve-Path $ReleaseDir).Path
$log = "$env:LOCALAPPDATA\SteamChecker\crash.log"

function Verify([string]$name) {
    $iso = Join-Path $rel ("iso_" + [IO.Path]::GetFileNameWithoutExtension($name))
    if (Test-Path $iso) { cmd /c rd /s /q "$iso" | Out-Null }
    New-Item -ItemType Directory -Force $iso | Out-Null
    Copy-Item (Join-Path $rel $name) (Join-Path $iso $name)

    if (Test-Path $log) { Remove-Item $log -Force }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process (Join-Path $iso $name) -PassThru
    while ($sw.Elapsed.TotalSeconds -lt 120 -and -not $p.HasExited -and $p.MainWindowHandle -eq 0) {
        Start-Sleep -Milliseconds 200; $p.Refresh()
    }
    $shown = $sw.Elapsed.TotalMilliseconds
    Start-Sleep -Seconds 6

    $rows = 0
    $windows = 0
    try {
        $all = $AE::RootElement.FindAll($TS::Children, (Cond $AE::ProcessIdProperty $p.Id))
        $windows = $all.Count
        $win = $all | Select-Object -First 1
        $list = $win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty "ResultList"))
        $icp = $list.GetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern)
        $item = $null
        for ($i = 0; $i -lt 200; $i++) {
            $item = $icp.FindItemByProperty($item, $null, $null)
            if (-not $item) { break }
            $rows++
        }
    } catch { }

    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }

    $crashed = Test-Path $log
    $crash = if ($crashed) { "CRASH LOGGED" } else { "no crash" }
    $verdict = if ($windows -ge 1 -and $rows -ge $MinRows -and -not $crashed) { "PASS" } else { "FAIL" }
    "{0,-36} {1,7:N0} ms  windows={2}  rows={3}  {4}  {5}" -f $name, $shown, $windows, $rows, $crash, $verdict

    cmd /c rd /s /q "$iso" | Out-Null
}

"=== isolated release verification ==="
Verify "SteamChecker.App.exe"
Verify "SteamChecker.App-selfcontained.exe"
"=== done: PASS needs windows>=1, rows>=$MinRows, no crash ==="
