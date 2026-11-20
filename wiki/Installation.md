# 安装与卸载

## 一键安装（推荐）

以普通用户身份打开 PowerShell，执行：

```powershell
irm https://rejtech.github.io/SETBoard/install.ps1 | iex
```

脚本会自动完成（**从源码构建**）：

1. 检测并安装 **.NET 9 SDK**（优先 winget，失败则用官方安装脚本）
2. 下载**源码 zip**：优先 Gitee 镜像，失败自动回退 GitHub（main → master），带下载进度条
3. `dotnet publish` 本地构建 win-x64 单文件 exe（需几分钟）
4. 安装到 `%LOCALAPPDATA%\SETBoard`
5. 创建**桌面快捷方式**（浮窗模式 `--float`）
6. 在**启动文件夹**创建快捷方式，实现开机浮窗自启
7. 启动 SETBoard（浮窗态）

> 全程无需管理员权限；若 .NET 安装环节请求管理员授权，允许即可。
> 已运行的 SETBoard 会先被安全停止（含守护进程），装完自动启动。
> Gitee 镜像为 [gitee.com/rejstudios/SETBoard](https://gitee.com/rejstudios/SETBoard)，不可用时自动回退 GitHub。

## 手动安装

1. 安装 [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0)（.NET Desktop Runtime 9.x）
2. 下载最新版 [SETBoard.exe](https://github.com/RejTech/SETBoard/releases/latest/download/SETBoard.exe)
3. 放到任意目录运行

国内网络可用加速地址：

```text
https://v4.gh-proxy.org/https://github.com/RejTech/SETBoard/releases/latest/download/SETBoard.exe
```

## 启动参数

| 参数 | 行为 |
|------|------|
| （无参数） | 打开白板主界面 |
| `--float` 或 `--mini` | 只显示桌面悬浮窗 |

**单实例规则**：已有 SETBoard 在运行时——

- 正常启动 → 唤醒已有进程的白板主界面，新进程自动退出
- `--float` 启动 → 直接忽略，已有进程不受影响

## 卸载

```powershell
irm https://rejtech.github.io/SETBoard/uninstall.ps1 | iex
```

自动完成：通知守护进程允许退出 → 结束主进程与守护进程 → 删除桌面/启动快捷方式 → 删除安装目录。

**保留不动**：.NET 9 运行时、你自己保存的 `.setb` 白板文件。

## 开机自启管理

自启项是启动文件夹中的 `SETBoard.lnk`（带 `--float` 参数）。手动管理：

- 按 `Win+R` 输入 `shell:startup` 回车
- 删除该快捷方式即关闭自启；从安装目录重新创建即恢复
