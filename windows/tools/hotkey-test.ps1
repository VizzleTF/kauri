# Usage: hotkey-test.ps1 -Exe dist\Kauri.exe -Target notepad|edge [-Rounds 5] [-Out result.txt]
# Presses the menu hot key while another app is in front, as a person does, and records for how
# long the menu stays on screen, whether keys reach it, and whether a pick pastes into that app.
# Exits 1 if any check fails.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $Target = "notepad",
    [int] $Rounds = 5,
    [string] $Out = "hotkey-test.txt"
)
$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class W
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int i);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }

    /// visible tool windows (the menu) of a process
    public static int MenuCount(uint pid)
    {
        int n = 0;
        EnumWindows((h, l) =>
        {
            if (Pid(h) == pid && IsWindowVisible(h) && (GetWindowLongW(h, -20) & 0x80) != 0) n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    public static IntPtr MainWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            RECT r;
            if (Pid(h) == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && r.Right - r.Left > 200) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Click(IntPtr h)
    {
        RECT r; GetWindowRect(h, out r);
        SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        System.Threading.Thread.Sleep(50);
        mouse_event(0x2, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x4, 0, 0, 0, UIntPtr.Zero);
    }

    const uint UP = 2;
    /// Ctrl+Shift+V the way a person types it: modifiers first, the key, then the modifiers
    /// let go a little later.
    public static void HotKey(int releaseDelayMs)
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x10, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(0x56, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(0x56, 0, UP, UIntPtr.Zero);
        System.Threading.Thread.Sleep(releaseDelayMs);
        keybd_event(0x10, 0, UP, UIntPtr.Zero);
        keybd_event(0x11, 0, UP, UIntPtr.Zero);
    }

    public static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, UP, UIntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, StringBuilder l);

    /// text of Notepad's edit control, or null if it has none we know
    public static string EditText(IntPtr top)
    {
        foreach (var cls in new[] { "Edit", "RichEditD2DPT" })
        {
            IntPtr e = FindWindowExW(top, IntPtr.Zero, cls, null);
            if (e == IntPtr.Zero)
            {
                IntPtr host = FindWindowExW(top, IntPtr.Zero, "NotepadTextBox", null);
                if (host != IntPtr.Zero) e = FindWindowExW(host, IntPtr.Zero, cls, null);
            }
            if (e != IntPtr.Zero)
            {
                var sb = new StringBuilder(4096);
                SendMessageW(e, 0x000D, (IntPtr)sb.Capacity, sb);
                return sb.ToString();
            }
        }
        return null;
    }
}
"@

$log = New-Object System.Collections.Generic.List[string]
$data = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }) "kauri-hotkey-data"
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
New-Item -ItemType Directory $data | Out-Null
'{"SuppressLoginItemAlert": true}' | Set-Content (Join-Path $data "settings.json")
$trace = Join-Path $data "trace.txt"
$env:KAURI_DATA_DIR = $data
$env:KAURI_TRACE = $trace

$kauri = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 3

switch ($Target) {
    "notepad" { $app = Start-Process notepad -PassThru }
    "edge" {
        $edgeProfile = Join-Path $data "edge"
        $app = Start-Process "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" -ArgumentList "--no-first-run","--no-default-browser-check","--user-data-dir=$edgeProfile","--new-window","about:blank" -PassThru
    }
}
Start-Sleep -Seconds 4
$appWindow = [IntPtr]::Zero
foreach ($p in @($app.Id) + @(Get-Process -Name ($app.ProcessName) -ErrorAction SilentlyContinue | ForEach-Object Id)) {
    $appWindow = [W]::MainWindow([uint32]$p)
    if ($appWindow -ne [IntPtr]::Zero) { break }
}
if ($appWindow -eq [IntPtr]::Zero) { throw "no window for $Target" }
$appPid = [W]::Pid($appWindow)
$log.Add("target $Target pid $appPid")

# Copy two clips the way a person does, so Kauri records them.
Add-Type -AssemblyName System.Windows.Forms
[W]::Click($appWindow)
Start-Sleep -Milliseconds 400
foreach ($clip in "first clip", "second clip") {
    foreach ($keys in $clip, "^a", "^c", "{DELETE}") {
        [System.Windows.Forms.SendKeys]::SendWait($keys)
        Start-Sleep -Milliseconds 150
    }
    Start-Sleep -Milliseconds 400
}

$failed = 0
foreach ($r in 1..$Rounds) {
    [W]::Click($appWindow)
    Start-Sleep -Milliseconds 500
    $fgPid = [W]::Pid([W]::GetForegroundWindow())
    $release = @(60, 150, 400)[($r - 1) % 3]
    [W]::HotKey($release)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $timeline = New-Object System.Collections.Generic.List[string]
    $last = ""; $appeared = $false; $flashed = $false
    while ($sw.ElapsedMilliseconds -lt 2000) {
        $n = [W]::MenuCount([uint32]$kauri.Id)
        if ($n -gt 0) { $appeared = $true } elseif ($appeared) { $flashed = $true }
        $fg = [W]::Pid([W]::GetForegroundWindow())
        $front = if ($fg -eq $kauri.Id) { "kauri" } elseif ($fg -eq $appPid) { "target" } else { "$fg" }
        $state = "menus=$n foreground=$front"
        if ($state -ne $last) { $timeline.Add("$($sw.ElapsedMilliseconds)ms $state"); $last = $state }
        Start-Sleep -Milliseconds 10
    }
    $open = [W]::MenuCount([uint32]$kauri.Id) -gt 0
    [W]::Tap(0x1B) # Escape
    Start-Sleep -Milliseconds 400
    $closedByEscape = [W]::MenuCount([uint32]$kauri.Id) -eq 0
    $ok = $open -and -not $flashed -and $closedByEscape
    if (-not $ok) { $failed++ }
    $log.Add("round $r (modifiers released after $release ms, foreground before: $(if ($fgPid -eq $appPid) {'target'} else {$fgPid})): open=$open flashed=$flashed closedByEscape=$closedByEscape $(if ($ok) {'PASS'} else {'FAIL'})")
    $timeline | ForEach-Object { $log.Add("    $_") }
    if (-not $closedByEscape) { [W]::Click($appWindow); Start-Sleep -Milliseconds 400 }
}

# A pick pastes into the app that was in front: Down to the second item, Enter.
if ($Target -eq "notepad") {
    [W]::Click($appWindow)
    Start-Sleep -Milliseconds 400
    [W]::HotKey(100)
    Start-Sleep -Milliseconds 800
    [W]::Tap(0x28) # Down
    Start-Sleep -Milliseconds 150
    [W]::Tap(0x0D) # Enter
    Start-Sleep -Milliseconds 1000
    $text = [W]::EditText($appWindow)
    if ($null -eq $text) { $failed++; $log.Add("paste: FAIL, Notepad's text control not found") }
    elseif ($text -like "*first clip*") { $log.Add("paste: PASS ('$text')") }
    else { $failed++; $log.Add("paste: FAIL, Notepad holds '$text'") }
}
$log.Add("$failed failed check(s)")
$log.Add("--- Kauri trace ---")
if (Test-Path $trace) { Get-Content $trace | ForEach-Object { $log.Add($_) } }
Stop-Process -Id $kauri.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
$log | Set-Content $Out
$log | Write-Output
if ($failed -gt 0) { exit 1 }
