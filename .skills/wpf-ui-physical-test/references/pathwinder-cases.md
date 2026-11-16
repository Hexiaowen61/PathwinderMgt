# Pathwinder 管理器实测案例

本仓库的具体用法与断言，配合 `scripts/ui-automation.ps1` 使用。程序发布目录：`dotnet-ui/publish`（`dotnet publish -c Release -r win-x64 --self-contained true`）。

## 通用前置

```powershell
. "…\.skills\wpf-ui-physical-test\scripts\ui-automation.ps1"
Enable-DpiAwareness

# 隔离配置：30 条假程序，避免污染用户真实 config.json
$json = @{ globalRules = @(); programs = $programs; settings = @{ theme = 'Dark' } } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $pub 'config.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

Get-Process PathwinderManager -ErrorAction SilentlyContinue | Stop-Process -Force
$proc = Start-Process (Join-Path $pub 'PathwinderManager.exe') -PassThru
$hwnd = Wait-Window -Process $proc -TimeoutSec 60
Close-MessageBox -ProcessId $proc.Id -Titles '启动提示','提示','完成','同步失败'
```

测试需要「已添加指令」的程序时，用真实 exe：复制 `notepad.exe` 到临时目录，再用 `WScript.Shell` 建 `.lnk`，写进 `config.json`，然后点「添加指令」生成 `runtime\<程序id>\Pathwinder.ini`。

## 案例 1：滚动位置在刷新/详情关闭后保持

- 入口：主窗口列表 `LvPrograms`，滚动到第 13 行左右，点「刷新」图标与某行「详情」。
- 关键：**必须用物理滚轮与物理点击**。UIA `InvokePattern.Invoke` 会先把目标行滚入视图，`ScrollViewer.VerticalOffset` 在虚拟化列表上也不可信（实测恒为 0），两者都会给出错误结论。
- 断言：滚动后截图记录首行程序名；操作后再次截图，首行必须仍是同一程序（可对比 `PrintWindow` 截图或读 UIA 可见项列表）。
- 修复方向（UI 代码）：结构未变时不要 `Rows.Clear()` 重建，改为就地更新（`INotifyPropertyChanged`）。

## 案例 2：详情窗口 Esc 等同右上角叉叉

- 入口：主窗口点「详情」→ `ProgramWindow`。
- 步骤：`Send-PhysicalKey -Key Escape`，然后 `Wait-Window -TitleLike '详情' -TimeoutSec 3` 必须超时。
- 注意：若窗口用 `IsCancel="True"`，Esc 在 ComboBox 展开时可能先收起下拉；本项目最终改为窗口级 `KeyDown`（未处理时 `Close()`）。

## 案例 3：新建规则未保存不得落盘

- 入口：详情窗口 →「新建规则」→ 填 `TxtOrigin` / `TxtTarget` → 不点「保存」，Esc 关闭。
- 断言两项，缺一不可：
  - `config.json` 中不出现草稿的路径（如 `OriginB`），且已保存的规则仍在；
  - `runtime\app01\Pathwinder.ini` 中不出现草稿的 `[FilesystemRule:...]` 段。
- 反面样例：旧实现「新建规则」直接 `_rules.Add()` + 触发保存，导致 ini 里出现空规则：

```ini
[FilesystemRule:新规则2]
OriginDirectory = 
TargetDirectory = 
RedirectMode = Simple Pro
```

- 复查：重新打开详情，列表中不应存在该草稿。

## 结果输出约定

每个检查打印 `PASS: …` / `FAIL: …`，最后汇总 `RESULT: failed=N`，`N > 0` 时 `exit 1`；不要只看截图下结论。
