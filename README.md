# PathwinderMgt

Pathwinder 的文件重定向能力 + 图形化管理界面。让指定程序在访问 A 目录时，实际读写落到 B 目录，且对程序本身完全透明。

基于上游 [Pathwinder](https://github.com/samuelgr/Pathwinder)（路径重定向内核）和 [Hookshot](https://github.com/samuelgr/Hookshot)（DLL 注入框架），在其基础上做了内核修复与桌面 UI 封装。

---

## ✨ 本 fork 的增强

### 内核增强（master-enhanced 分支）

上游 Pathwinder 只在文件 I/O 层面做重定向。Chromium/Electron 系程序在子进程创建、路径规范化、卷查询等环节会"泄漏"真实路径，导致程序静默失败。
本 fork 已修复为**规则驱动的通用实现**，不针对具体程序硬编码，同类场景（任意程序、任意重定向规则）都能受益。

### 桌面 UI（dotnet-ui 分支）

原版使用方式是"手工逐个配置"：每个程序都要单独写 INI、把核心 DLL 复制到 Hookshot 目录、改命令行参数；程序一多就无法维护。UI 把这套流程全部图形化：

- **一键登记**：导入 `.lnk` 快捷方式或 `.exe`，自动硬链接 Hookshot/Pathwinder 组件、生成该程序的配置与启动指令
- **规则编辑**：界面上添加/删除/修改重定向规则（源目录 → 目标目录，可选文件模式），保存后点"更新指令"立即生效
- **批量操作**：浏览目录一键全部添加、全部移除、全部删除
- **状态监控**：首页实时显示每个已登记程序的运行状态、重定向目标、是否为管理员实例
- **多开支持**：同一程序可以登记多份，每份指向独立的目标目录，实现互不干扰的无限多开（微信、QQ、游戏存档等）
- **设置面板**：全局默认路径、快捷方式前缀/恢复、日志目录统一管理
- **打包发布**：`dotnet_publish.ps1` 一键清理 → 自包含发布 → 编译 Inno Setup 安装包；安装包内已携带 Hookshot 和 Pathwinder 内核（`resources/` 目录），安装后即可使用

---

## 截图

![首页.png](dotnet-ui/docs/%E9%A6%96%E9%A1%B5.png)

---

## 分支说明

| 分支 | 用途 |
|---|---|
| `master` | 同步上游 Pathwinder 原作，不做额外改动 |
| `master-enhanced` | 在 Pathwinder 内核上修复问题、增加功能（见上文"内核增强"） |
| `dotnet-ui` | 基于 `master-enhanced` 构建的 UI 项目；发布时会把 `master-enhanced` 的核心产物（Hookshot + Pathwinder 压缩包）打包进 `resources/` 目录随安装包分发 |

## 使用

- **安装**：运行 `PathwinderManager_*.exe` 安装包，启动后点「浏览」导入程序，或「全部添加」批量登记。
- **从源码构建**：在 `dotnet-ui` 目录执行 `dotnet_publish.ps1`（清理 → 自包含发布 → Inno Setup 编译）。

## 许可证

内核部分沿用上游 [BSD 3-Clause License](LICENSE)，版权归原作者 Samuel Grossman 所有。UI 部分同许可证。
