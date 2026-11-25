#Requires -Version 5.1
<#
Windows 桌面 UI 自动化辅助库
定位用 UIAutomation，触发用 Win32 物理输入（mouse_event / keybd_event）。
用法：
    . "…\scripts\ui-automation.ps1"
    Enable-DpiAwareness
    $hwnd = Wait-Window -Process $proc -TimeoutSec 60
    Invoke-PhysicalClick -Element (Find-UiElement -Root (Get-UiRoot -Hwnd $hwnd) -Name '详情' -ControlTypeName Button)
#>

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

if (-not ('PwUi.Native' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PwUi
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        const uint MouseLeftDown = 0x0002;
        const uint MouseLeftUp = 0x0004;
        const uint MouseWheel = 0x0800;

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(120);
            mouse_event(MouseLeftDown, 0, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            mouse_event(MouseLeftUp, 0, 0, 0, IntPtr.Zero);
        }

        public static void Wheel(int x, int y, int notches)
        {
            SetCursorPos(x, y);
            int step = notches < 0 ? -120 : 120;
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                mouse_event(MouseWheel, 0, 0, step, IntPtr.Zero);
                System.Threading.Thread.Sleep(80);
            }
        }

        public static void Key(byte vk)
        {
            keybd_event(vk, 0, 0, IntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            keybd_event(vk, 0, 2, IntPtr.Zero);
        }

        public static string Title(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, 512);
            return sb.ToString();
        }

        public static List<IntPtr> Windows(uint processId)
        {
            var list = new List<IntPtr>();
            EnumWindows((h, p) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == processId && IsWindowVisible(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
"@
}

function Enable-DpiAwareness {
    <# 让 UIA 矩形坐标等于物理像素；旧系统回退到 SetProcessDPIAware #>
    try {
        [void][PwUi.Native]::SetProcessDpiAwarenessContext([IntPtr]::new([Int64]-4))
    }
    catch {
        try { [void][PwUi.Native]::SetProcessDPIAware() } catch { }
    }
}

function Get-WindowList {
    param([int]$ProcessId)
    $result = @()
    foreach ($h in [PwUi.Native]::Windows([uint32]$ProcessId)) {
        $result += [pscustomobject]@{ Hwnd = $h; Title = [PwUi.Native]::Title($h) }
    }
    # 逗号返回：即使没有窗口也返回空数组而不是 $null，调用方可以安全 foreach
    return ,$result
}

function Wait-Window {
    <# 等进程出现目标窗口：不给 TitleLike 时等主窗口，给了就按标题模糊匹配 #>
    param(
        $Process,
        [string]$TitleLike,
        [int]$TimeoutSec = 60
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        $Process.Refresh()
        if ($Process.HasExited) { throw "进程已退出，pid=$($Process.Id)" }
        if ([string]::IsNullOrEmpty($TitleLike)) {
            if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return $Process.MainWindowHandle }
            continue
        }
        foreach ($item in (Get-WindowList -ProcessId $Process.Id)) {
            if ($item.Title -like "*$TitleLike*") { return $item.Hwnd }
        }
    }
    throw "等待窗口超时（${TimeoutSec}s），标题包含：$TitleLike"
}

function Get-UiRoot {
    param([IntPtr]$Hwnd)
    [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
}

function Find-UiElement {
    <# 条件全部给出时取交集；找不到返回 $null。同名 TextBlock 很常见，务必带 ControlType #>
    param(
        $Root,
        [string]$Name,
        [string]$ControlTypeName,
        [string]$AutomationId
    )
    $conds = New-Object 'System.Collections.Generic.List[System.Windows.Automation.Condition]'
    if ($AutomationId) {
        $conds.Add([System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId))
    }
    if ($ControlTypeName) {
        $ct = [System.Windows.Automation.ControlType]::$ControlTypeName
        $conds.Add([System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct))
    }
    if ($Name) {
        $conds.Add([System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    }
    if ($conds.Count -eq 0) { throw 'Find-UiElement 至少需要一个查找条件' }

    $cond = if ($conds.Count -eq 1) { $conds[0] }
            else { [System.Windows.Automation.AndCondition]::new($conds.ToArray()) }
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-PhysicalClickAt {
    param([int]$X, [int]$Y)
    [PwUi.Native]::Click($X, $Y)
    Start-Sleep -Milliseconds 400
}

function Invoke-PhysicalClick {
    <# 点元素中心：UIA 只提供矩形，真正的点击用 Win32 #>
    param($Element)
    if ($null -eq $Element) { throw 'Invoke-PhysicalClick 收到空元素' }
    $rc = $Element.Current.BoundingRectangle
    Invoke-PhysicalClickAt -X ([int]($rc.X + $rc.Width / 2)) -Y ([int]($rc.Y + $rc.Height / 2))
}

function Set-UiText {
    <# 优先 ValuePattern，失败回退到「点击 + Ctrl+A + 输入」 #>
    param($Root, [string]$AutomationId, [string]$Value)
    $el = Find-UiElement -Root $Root -AutomationId $AutomationId -ControlTypeName Edit
    if ($null -eq $el) { throw "找不到输入框：$AutomationId" }
    try {
        $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
    }
    catch {
        Invoke-PhysicalClick -Element $el
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        [System.Windows.Forms.SendKeys]::SendWait('{DEL}')
        [System.Windows.Forms.SendKeys]::SendWait($Value)
    }
    Start-Sleep -Milliseconds 200
}

function Send-PhysicalKey {
    param([string]$Key = 'Escape')
    $map = @{
        Escape = 0x1B; Esc = 0x1B; Enter = 0x0D; Return = 0x0D; Tab = 0x09; Space = 0x20
        Back = 0x08; Delete = 0x2E; Up = 0x26; Down = 0x28; Left = 0x25; Right = 0x27; F5 = 0x74
    }
    if ($map.ContainsKey($Key)) {
        $vk = [byte]$map[$Key]
    }
    elseif ($Key.Length -eq 1) {
        $vk = [byte][char]([char]::ToUpperInvariant($Key))
    }
    else { throw "未支持的按键：$Key" }
    [PwUi.Native]::Key($vk)
    Start-Sleep -Milliseconds 250
}

function Send-Wheel {
    param([int]$X, [int]$Y, [int]$Notches)
    [PwUi.Native]::Wheel($X, $Y, $Notches)
    Start-Sleep -Milliseconds 600
}

function Save-WindowShot {
    <# PrintWindow 抓整窗；高 DPI 下若发现缩放失真，改用 CopyFromScreen #>
    param([IntPtr]$Hwnd, [string]$Path)
    $r = New-Object 'PwUi.Native+RECT'
    if (-not [PwUi.Native]::GetWindowRect($Hwnd, [ref]$r)) { throw 'GetWindowRect 失败' }
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { throw "窗口尺寸异常：${w}x${h}" }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        [void][PwUi.Native]::PrintWindow($Hwnd, $hdc, 2)
    }
    finally {
        $g.ReleaseHdc($hdc)
        $g.Dispose()
    }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $Path
}

function Get-ShotPixel {
    <# 采样像素颜色，返回 #RRGGBB；越界直接报错而不是静默返回黑色 #>
    param([string]$Path, [int]$X, [int]$Y)
    $bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Path).Path)
    try {
        if ($X -lt 0 -or $Y -lt 0 -or $X -ge $bmp.Width -or $Y -ge $bmp.Height) {
            throw "坐标超出截图范围：${X},${Y}，截图 ${($bmp.Width)}x${($bmp.Height)}"
        }
        $c = $bmp.GetPixel($X, $Y)
        return ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B)
    }
    finally { $bmp.Dispose() }
}

function Close-MessageBox {
    <# 按标题匹配 MessageBox，用 UIA 点“确定”（普通按钮，无滚动副作用） #>
    param([int]$ProcessId, [string[]]$Titles)
    foreach ($item in (Get-WindowList -ProcessId $ProcessId)) {
        if ($Titles -notcontains $item.Title) { continue }
        $root = Get-UiRoot -Hwnd $item.Hwnd
        $btn = Find-UiElement -Root $root -Name '确定' -ControlTypeName Button
        if ($null -ne $btn) {
            $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        }
        else {
            [PwUi.Native]::Key(0x0D)
        }
        Start-Sleep -Milliseconds 400
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Write-Output 'ui-automation.ps1 已加载（直接运行只做自检，不操作任何窗口）'
    Write-Output '可用函数：Enable-DpiAwareness, Get-WindowList, Wait-Window, Get-UiRoot, Find-UiElement,'
    Write-Output '          Invoke-PhysicalClick, Invoke-PhysicalClickAt, Set-UiText, Send-PhysicalKey,'
    Write-Output '          Send-Wheel, Save-WindowShot, Get-ShotPixel, Close-MessageBox'
    Write-Output '请用「. 本文件路径」dot-source 后再调用。'
}
