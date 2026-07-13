# PowerShellPlus - core automation
# Focuses the Windows Terminal window, then for each tab presses Enter and moves
# to the next tab with Ctrl+Tab. Settings come from config.json next to this file.

$ErrorActionPreference = 'Stop'
$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $root 'config.json'
$logPath    = Join-Path $root 'log.txt'

function Write-Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Add-Content -Path $logPath -Value $line
}

# ---- load config -----------------------------------------------------------
$config = @{ tabCount = 5; delayMs = 400; processName = 'WindowsTerminal' }
if (Test-Path $configPath) {
    $json = Get-Content $configPath -Raw | ConvertFrom-Json
    if ($json.tabCount)    { $config.tabCount    = [int]$json.tabCount }
    if ($json.delayMs)     { $config.delayMs     = [int]$json.delayMs }
    if ($json.processName) { $config.processName = [string]$json.processName }
}

# ---- find and focus the terminal window ------------------------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    // Robust focus: try the plain call first; if the foreground lock blocks it,
    // escalate to the AttachThreadInput + Alt-nudge technique. If Alt was used,
    // send Esc so the target window is not left in menu mode (which eats Enter).
    public static bool ForceForeground(IntPtr hWnd) {
        if (IsIconic(hWnd)) ShowWindow(hWnd, 9); // SW_RESTORE
        SetForegroundWindow(hWnd);
        System.Threading.Thread.Sleep(150);
        if (GetForegroundWindow() == hWnd) return true;

        keybd_event(0x12, 0, 0, UIntPtr.Zero);   // Alt down
        keybd_event(0x12, 0, 2, UIntPtr.Zero);   // Alt up
        uint targetThread = GetWindowThreadProcessId(hWnd, IntPtr.Zero);
        uint ourThread    = GetCurrentThreadId();
        AttachThreadInput(ourThread, targetThread, true);
        SetForegroundWindow(hWnd);
        ShowWindow(hWnd, 5); // SW_SHOW
        AttachThreadInput(ourThread, targetThread, false);
        System.Threading.Thread.Sleep(150);
        if (GetForegroundWindow() != hWnd) return false;
        keybd_event(0x1B, 0, 0, UIntPtr.Zero);   // Esc down - cancel any menu mode
        keybd_event(0x1B, 0, 2, UIntPtr.Zero);   // Esc up
        return true;
    }
}
"@

$proc = Get-Process -Name $config.processName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1

if (-not $proc) {
    Write-Log "FAILED: no running '$($config.processName)' window found. Nothing done."
    exit 1
}

$hwnd = $proc.MainWindowHandle
$focused = $false
for ($try = 1; $try -le 3 -and -not $focused; $try++) {
    $focused = [Win32]::ForceForeground($hwnd)
    Start-Sleep -Milliseconds 400
    if (-not $focused) { $focused = ([Win32]::GetForegroundWindow() -eq $hwnd) }
}

if (-not $focused) {
    Write-Log "FAILED: could not bring '$($proc.MainWindowTitle)' to the foreground. No keys sent."
    exit 1
}
Start-Sleep -Milliseconds 400

Write-Log "Focused '$($proc.MainWindowTitle)' (pid $($proc.Id)). Pressing Enter in $($config.tabCount) tab(s)..."

# ---- press Enter in each tab, Ctrl+Tab to advance ---------------------------
for ($i = 1; $i -le $config.tabCount; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds $config.delayMs
    if ($i -lt $config.tabCount) {
        [System.Windows.Forms.SendKeys]::SendWait('^{TAB}')
        Start-Sleep -Milliseconds $config.delayMs
    }
}

Write-Log "Done. Sent Enter to $($config.tabCount) tab(s)."
exit 0
