---
name: wpf-ui-physical-test
description: Test Windows WPF desktop UI with UIAutomation lookup plus real Win32 mouse and keyboard input, then assert file side effects. Use for dialog flows, scroll position, theme rendering, or unsaved-edit leaks. Not for web pages.
---

# WPF 桌面 UI 物理点击测试

用「UIAutomation 定位 + Win32 物理输入」驱动本机 WPF 程序，再用文件与像素断言结果。适用于需要验证真实交互（滚动位置、焦点、快捷键、对话框保存语义）的场景。

## 核心原则

**UIA 只用来定位，一切输入走 Win32。** UIA 的 `InvokePattern.Invoke` 和 `ScrollPattern.SetScrollPercent` 会先把目标滚入视图再触发，在虚拟化列表上会制造假象（例如「关闭详情后列表回到顶部」的误判）。凡是要验证滚动位置、真实点击热区、键盘行为的，必须 `SetCursorPos` + `mouse_event` / `keybd_event`。

**断言优先于截图。** 截图用于人工确认视觉（主题色、布局裁切），功能正确性一律读产物：`config.json`、`*.ini`、`*.log`、进程是否退出、窗口标题是否消失。

## 使用流程

1. **准备隔离环境**：把待测程序 `dotnet publish` 到独立目录，自己写测试用 `config.json`（用 `[IO.File]::WriteAllText` + `UTF8Encoding($false)`，避免 BOM 干扰）。不要在用户真实配置上测试。
2. **启用 DPI 感知**：脚本进程先调 `SetProcessDpiAwarenessContext(-4)`（失败回退 `SetProcessDPIAware`），保证 UIA 的 `BoundingRectangle` 与物理像素一致。
3. **拉起进程并等主窗口**：`Start-Process -PassThru` 后轮询 `MainWindowHandle`，同时处理启动弹窗（运行库缺失、权限提示等）。
4. **定位控件**：`AutomationElement.FromHandle(hwnd)` 取根节点，用 `ControlTypeButton` + `Name` 组合条件查找；输入框用 `ControlTypeEdit` + `AutomationId`（同名 TextBlock 会干扰，必须限定类型）。
5. **物理触发**：`SetForegroundWindow` → `SetCursorPos(矩形中心)` → `mouse_event(0x0002)` + `mouse_event(0x0004)`；键盘用 `keybd_event`。
6. **断言**：读文件/标题/进程，输出 PASS/FAIL，不要只截图。
7. **清理**：结束进程，删除临时配置与测试目录。

## 脚本

`scripts/ui-automation.ps1` 是可直接 dot-source 的辅助库，已封装上述能力：

```powershell
. "D:\APP\JetBrains\workCAll\PathwinderMgt\.skills\wpf-ui-physical-test\scripts\ui-automation.ps1"

Enable-DpiAwareness
$proc = Start-Process $exe -PassThru
$hwnd = Wait-Window -Process $proc -TimeoutSec 60
Close-MessageBox -ProcessId $proc.Id -Titles '启动提示','提示'

$root = Get-UiRoot -Hwnd $hwnd
$btn  = Find-UiElement -Root $root -Name '详情' -ControlTypeName Button
Invoke-PhysicalClick -Element $btn

$dlg = Wait-Window -Process $proc -TitleLike '详情' -TimeoutSec 20
$dlgRoot = Get-UiRoot -Hwnd $dlg
Set-UiText -Root $dlgRoot -AutomationId 'TxtOrigin' -Value 'D:\PwTest\Origin'
Send-PhysicalKey -Key Escape
```

可用函数：`Enable-DpiAwareness`、`Wait-Window`、`Get-WindowList`、`Get-UiRoot`、`Find-UiElement`、`Invoke-PhysicalClick`、`Invoke-PhysicalClickAt`、`Set-UiText`、`Send-PhysicalKey`、`Send-Wheel`、`Save-WindowShot`、`Get-ShotPixel`、`Close-MessageBox`。

完整案例（含三段断言）见 `references/pathwinder-cases.md`。

## 关键细节

- **点击坐标**：用元素的 `BoundingRectangle` 中心；窗口必须先 `SetForegroundWindow`，否则点到被遮挡的窗口。
- **滚轮**：`mouse_event(0x0800, 0, 0, ±120)`，一次一格，每格间隔 80ms。滚动后不要用 UIA 读 `ScrollPercent` 判断结果，改用截图或后续业务断言。
- **Esc 等按键**：`keybd_event(VK, scan=0, 0)` 按下 + `keybd_event(VK, 0, 2)` 抬起。VK：Esc 0x1B、Enter 0x0D、Tab 0x09、Ctrl 0x11、A 0x41。
- **文本框输入**：优先 `ValuePattern.SetValue`；抛异常时改「点击 + `^a` + `{DEL}` + `SendKeys`」。
- **MessageBox**：没有 AutomationId，按标题匹配后用 UIA `InvokePattern` 点「确定」（这里用 UIA 是安全的，按钮无滚动副作用）。
- **截图**：`PrintWindow(hwnd, hdc, 2)` 抓整窗；高 DPI 下 `PrintWindow` 可能缩放失真，必要时改用 `Graphics.CopyFromScreen` 抓窗口矩形（前提是窗口在前台）。
- **像素采样**：`Bitmap.GetPixel` 前先校验坐标 `< Width/Height`，否则抛 `ArgumentException`。

## 坑

- **PowerShell 5 读无 BOM 的 UTF-8 脚本会乱码**，导致中文字符串匹配失败。新建 `.ps1` 后先转 UTF-8 with BOM 再执行。
- **同名元素冲突**：WPF 里 `x:Name` 只保证窗口内唯一，但同名字符串可能同时出现在 TextBlock 与 TextBox 的 `AutomationId` 上，条件必须带 `ControlType`。
- **UIA 查找慢**：`TreeScope.Descendants` 在大列表上开销高，缓存根节点、缩小条件。
- **物理输入抢占鼠标键盘**：脚本运行期间不要动鼠标；坐标点击要求目标窗口可见且未被锁屏。
- **进程残留**：脚本开头与结尾都 `Stop-Process`，否则下次启动会命中单实例逻辑或占用 publish 目录。
- **自包含发布目录不可写**：测试配置写到 publish 目录时，确认程序有写权限；工作区 `ini` 通常在 `runtime\<程序id>\` 下。
