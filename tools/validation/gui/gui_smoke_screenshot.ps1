# gui_smoke_screenshot.ps1 — launch the GUI, drive it via UI Automation, and
# capture a window screenshot for visual verification of GUI changes.
#
# Usage:
#   pwsh tools/validation/gui/gui_smoke_screenshot.ps1 -ExePath <NeversoftMultitool.exe> -OutPng shot.png
#     [-NavItem "Meshes & Characters"]     # NavigationView item to select first
#     [-BrowsePath C:\some\folder]         # invoke "Browse folder..." and drive the picker dialog
#     [-Invoke "Collapse file list panel","Collapse side panel"]  # buttons to invoke by UIA name, in order
#     [-WaitForName "File Name"]           # poll until an element with this UIA name exists (e.g. scan done)
#     [-HoverOver "File Name"]             # park the cursor over this element before the screenshot
#     [-SettleSec 8]                       # extra wait before the screenshot (async work a selection kicked off)
#     [-KeepOpen]                          # leave the app running afterwards
#
# The app instance is launched fresh each run and closed at the end unless
# -KeepOpen is given, so it never touches an instance the user has open.

param(
    [Parameter(Mandatory)] [string]$ExePath,
    [Parameter(Mandatory)] [string]$OutPng,
    [string]$NavItem,
    [string]$BrowsePath,
    [string]$ArchivePath,
    [string[]]$Invoke,
    [string]$WaitForName,
    [string]$HoverOver,
    [int]$SettleSec = 0,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class GuiSmokeNative
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageText(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageGetText(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    // Deep search: EnumChildWindows already walks the whole subtree, which
    // GetDlgItem/FindWindowEx (direct children only) cannot.
    public static IntPtr FindDescendant(IntPtr parent, string className, int controlId)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (h, l) =>
        {
            if (GetDlgCtrlID(h) != controlId) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() != className) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Find-ByName([System.Windows.Automation.AutomationElement]$Root, [string]$Name, [int]$TimeoutSec = 10) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "UIA element named '$Name' not found within ${TimeoutSec}s"
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    # A name usually matches the TextBlock INSIDE a row, and the pattern lives on
    # the row container, so walk up until one of the two patterns is available.
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $name = $Element.Current.Name
    $current = $Element
    for ($depth = 0; $depth -lt 6 -and $current; $depth++) {
        $pattern = $null
        if ($current.TryGetCurrentPattern(
                [System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            $pattern.Invoke(); return
        }
        if ($current.TryGetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            $pattern.Select(); return
        }
        $current = $walker.GetParent($current)
    }
    throw "Element '$name' and its ancestors support neither Invoke nor SelectionItem"
}

$process = Start-Process -FilePath $ExePath -PassThru
try {
    $deadline = (Get-Date).AddSeconds(30)
    while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw "App window did not appear within 30s" }
    Start-Sleep -Seconds 3   # let the first tab finish loading

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    [void][GuiSmokeNative]::SetForegroundWindow($process.MainWindowHandle)

    if ($NavItem) {
        Invoke-Element (Find-ByName $window $NavItem)
        Start-Sleep -Seconds 2
    }

    if ($BrowsePath) {
        Invoke-Element (Find-ByName $window 'Browse folder...')
        # The folder picker is a modal Win32 dialog (#32770) parented under the
        # app window itself, not a desktop top-level.
        $dialogCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
        $dialog = $null
        $deadline = (Get-Date).AddSeconds(10)
        while (-not $dialog -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $dialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Children, $dialogCondition)
        }
        if (-not $dialog) { throw "Folder picker dialog did not appear" }

        # No global input (SendKeys would land wherever the user's focus is):
        # set the Folder edit's text via WM_SETTEXT, click Select Folder to
        # NAVIGATE into it, verify the breadcrumb, then click again to accept
        # the now-open folder. The commit button is IDOK (control id 1); it is
        # not exposed through UIA, so click it with BM_CLICK.
        $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
        $edit = [GuiSmokeNative]::FindDescendant($dialogHandle, 'Edit', 1152)
        if ($edit -eq [IntPtr]::Zero) { throw "Folder-name edit (id 1152) not found in picker dialog" }
        $okHandle = [GuiSmokeNative]::FindDescendant($dialogHandle, 'Button', 1)
        if ($okHandle -eq [IntPtr]::Zero) { throw "Select Folder button (IDOK) not found" }

        [void][GuiSmokeNative]::SendMessageText($edit, 0x000C, [IntPtr]::Zero, $BrowsePath) # WM_SETTEXT

        # NEVER commit an unverified folder (committing whatever the dialog
        # opened on would scan the wrong tree). GetWindowText cannot read a
        # cross-process edit control, so read back with WM_GETTEXT.
        $readBack = New-Object System.Text.StringBuilder 1024
        [void][GuiSmokeNative]::SendMessageGetText($edit, 0x000D, [IntPtr]1024, $readBack) # WM_GETTEXT
        if ($readBack.ToString() -ne $BrowsePath) {
            throw "Folder edit reads '$($readBack.ToString())' instead of '$BrowsePath'; not committing"
        }

        # Select Folder with a full path in the box accepts that path directly
        # and closes the dialog; with a relative/partial value it may only
        # navigate, leaving the dialog open — click again in that case.
        [void][GuiSmokeNative]::SendMessage($okHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) # BM_CLICK
        Start-Sleep -Milliseconds 1200
        if ([GuiSmokeNative]::IsWindow($dialogHandle)) {
            [void][GuiSmokeNative]::SendMessage($okHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 1200
            if ([GuiSmokeNative]::IsWindow($dialogHandle)) { throw "Folder picker did not accept '$BrowsePath'" }
        }
        Start-Sleep -Seconds 1
    }

    if ($ArchivePath) {
        # Same modal #32770 as the folder picker, but the file dialog's name box
        # is a ComboBoxEx32 > Edit rather than a bare Edit, so the control is
        # located by class walk rather than by id.
        Invoke-Element (Find-ByName $window 'Select archive...')
        $dialogCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
        $dialog = $null
        $deadline = (Get-Date).AddSeconds(10)
        while (-not $dialog -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 400
            $dialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Children, $dialogCondition)
        }
        if (-not $dialog) { throw "File picker dialog did not appear" }

        $dialogHandle = [IntPtr]$dialog.Current.NativeWindowHandle
        $edit = [GuiSmokeNative]::FindDescendant($dialogHandle, 'Edit', 1148)
        if ($edit -eq [IntPtr]::Zero) {
            $edit = [GuiSmokeNative]::FindDescendant($dialogHandle, 'Edit', 1152)
        }
        if ($edit -eq [IntPtr]::Zero) { throw "File-name edit not found in picker dialog" }
        $okHandle = [GuiSmokeNative]::FindDescendant($dialogHandle, 'Button', 1)
        if ($okHandle -eq [IntPtr]::Zero) { throw "Open button (IDOK) not found" }

        [void][GuiSmokeNative]::SendMessageText($edit, 0x000C, [IntPtr]::Zero, $ArchivePath)
        $readBack = New-Object System.Text.StringBuilder 1024
        [void][GuiSmokeNative]::SendMessageGetText($edit, 0x000D, [IntPtr]1024, $readBack)
        if ($readBack.ToString() -ne $ArchivePath) {
            throw "File edit reads '$($readBack.ToString())' instead of '$ArchivePath'; not committing"
        }

        [void][GuiSmokeNative]::SendMessage($okHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 1500
        if ([GuiSmokeNative]::IsWindow($dialogHandle)) { throw "File picker did not accept '$ArchivePath'" }
        Start-Sleep -Seconds 1
    }

    if ($WaitForName) {
        [void](Find-ByName $window $WaitForName -TimeoutSec 60)
        Start-Sleep -Seconds 1
    }

    foreach ($name in ($Invoke ?? @())) {
        Invoke-Element (Find-ByName $window $name)
        Start-Sleep -Seconds 1
    }

    # Work a selection kicks off asynchronously (a background scan, a preview
    # build) finishes after the invoke returns; give it time before capturing.
    if ($SettleSec -gt 0) { Start-Sleep -Seconds $SettleSec }

    if ($HoverOver) {
        $target = Find-ByName $window $HoverOver
        $rect = $target.Current.BoundingRectangle
        [void][GuiSmokeNative]::SetCursorPos([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
        Start-Sleep -Milliseconds 800
    }

    $bounds = $window.Current.BoundingRectangle
    $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    $bitmap.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Saved $OutPng"
}
finally {
    if (-not $KeepOpen -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
