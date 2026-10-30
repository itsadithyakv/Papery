<#
.SYNOPSIS
    Presses Papery's buttons through Windows UI Automation and captures its window, with no mouse or display needed.

.DESCRIPTION
    Used for checks and for making Store screenshots. Each step is one of:
      click:Name       invoke the first element with that accessible name
      toggle:Name      toggle the first element with that name
      select:Name      select the first item with that name (pills, tabs, list items)
      type:Name=text   put text into the named box
      wait:seconds     wait
      shot:file.png    capture the window to a PNG (works without a visible desktop)
      list             print the names of everything on screen
      size:WxH         resize the window

    Example:
      powershell -File tools/Drive-Papery.ps1 "select:Tools" "click:Compress" "wait:4" "shot:C:\temp\compress.png"
#>
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Steps)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PaperyWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
}
'@

$process = Get-Process Papery | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $process) { throw 'Papery is not running.' }
$hwnd = $process.MainWindowHandle
$root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
$tree = [Windows.Automation.TreeScope]::Descendants

function Find-Named([string] $name) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)
    # Dialogs and flyouts live in popups that aren't always under the window, so look there too.
    $found = $root.FindFirst($tree, $condition)
    if ($found) { return $found }
    $desktop = [Windows.Automation.AutomationElement]::RootElement
    $pid2 = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    foreach ($w in $desktop.FindAll([Windows.Automation.TreeScope]::Children, $pid2)) {
        $found = $w.FindFirst($tree, $condition)
        if ($found) { return $found }
    }
    throw "Nothing named '$name'."
}

function Get-Pattern($element, $pattern) {
    $result = $null
    if ($element.TryGetCurrentPattern($pattern, [ref] $result)) { return $result }
    return $null
}

foreach ($step in $Steps) {
    $parts = $step -split ':', 2
    $verb = $parts[0]
    $arg = if ($parts.Count -gt 1) { $parts[1] } else { '' }
    switch ($verb) {
        'wait' { Start-Sleep -Milliseconds ([int]([double]$arg * 1000)) }
        'size' {
            $wh = $arg -split 'x'
            [void][PaperyWin]::MoveWindow($hwnd, 0, 0, [int]$wh[0], [int]$wh[1], $true)
        }
        'click' {
            $e = Find-Named $arg
            $invoke = Get-Pattern $e ([Windows.Automation.InvokePattern]::Pattern)
            if ($invoke) { $invoke.Invoke() }
            else {
                $toggle = Get-Pattern $e ([Windows.Automation.TogglePattern]::Pattern)
                if ($toggle) { $toggle.Toggle() }
                else { (Get-Pattern $e ([Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
            }
        }
        'toggle' { (Get-Pattern (Find-Named $arg) ([Windows.Automation.TogglePattern]::Pattern)).Toggle() }
        'select' { (Get-Pattern (Find-Named $arg) ([Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
        'type' {
            $nv = $arg -split '=', 2
            (Get-Pattern (Find-Named $nv[0]) ([Windows.Automation.ValuePattern]::Pattern)).SetValue($nv[1])
        }
        'list' {
            foreach ($e in $root.FindAll($tree, [Windows.Automation.Condition]::TrueCondition)) {
                if ($e.Current.Name) { "{0}: {1}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $e.Current.Name }
            }
        }
        'shot' {
            $r = New-Object PaperyWin+RECT
            [void][PaperyWin]::GetWindowRect($hwnd, [ref] $r)
            $bitmap = New-Object System.Drawing.Bitmap(($r.R - $r.L), ($r.B - $r.T))
            $g = [System.Drawing.Graphics]::FromImage($bitmap)
            $dc = $g.GetHdc()
            [void][PaperyWin]::PrintWindow($hwnd, $dc, 2) # 2 = full content, including what the GPU draws
            $g.ReleaseHdc($dc)
            $g.Dispose()
            $bitmap.Save($arg, [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
            "Saved $arg"
        }
        default { throw "Unknown step: $step" }
    }
}
