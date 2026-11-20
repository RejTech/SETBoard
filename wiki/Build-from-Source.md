# 从源码构建

## 环境准备

- Windows 10/11（开发）
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## 本地运行

```powershell
git clone https://github.com/RejTech/SETBoard.git
cd SETBoard
dotnet run
```

## 发布单文件 exe

```powershell
dotnet publish SETBoard.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none `
  -o publish
```

产物：`publish\SETBoard.exe`（图标等 Avalonia 资源已内嵌，单文件即可运行；目标机器需 .NET 9 运行时，自包含包则无需）。

## 版本号

`version.json` 嵌入程序集，关于对话框与自动更新读取其中的 `version` 字段。本地构建可手动修改；CI 会在编译前自动写入。

## CI 流水线

### GitHub Actions（`.github/workflows/release.yml`）

- 触发：push 到 `main` / `master`，或手动运行
- windows-latest → 写入版本号 `1.0.<run_number>` → 发布单文件 → 打 zip → 创建 GitHub Release（附 exe + zip）

### Gitee Go（`.workflow/build.yml`）

- 触发：push 到 `master` / `main`
- Linux 构建机交叉发布 win-x64（自动安装 .NET 9 SDK）→ 打 zip → 输出制品路径
- 版本号 `1.0.<提交数>`；在流水线「制品管理」添加 `publish/SETBoard.exe` 与 `SETBoard-v*-win-x64.zip` 收集产物
- 若构建机交叉 R2R 报错，publish 追加 `-p:PublishReadyToRun=false`

## 官网与安装脚本

- `Page/` 目录即 GitHub Pages 站点（https://rejtech.github.io/SETBoard/）
- `Page/install.ps1` / `Page/uninstall.ps1`：一键安装 / 卸载脚本，输出统一英文（避免 PowerShell 5.1 管道中文乱码）
- 发布安装脚本更新后无需发版，Pages 更新即生效
