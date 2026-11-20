# SETBoard 使用手册

面向触屏教学设备的轻量白板软件，由 **Swift Education Technology（迅教科技）** 开发。
基于 Avalonia 11 + .NET 9，针对电子白板、触控一体机（如希沃）的笔类与手指输入专门优化。

## 文档导航

| 文档 | 内容 |
|------|------|
| [[Installation]] | 一键安装、手动安装、开机自启、卸载 |
| [[Whiteboard]] | 白板主界面：工具、页面、视图、文件 |
| [[Desktop-Annotation]] | 桌面批注、悬浮窗、进程守护 |
| [[Shortcuts]] | 全部快捷键 |
| [[Auto-Update]] | 自动更新机制与镜像加速 |
| [[FAQ]] | 常见问题与排查 |
| [[Build-from-Source]] | 从源码构建、CI 流水线 |

## 快速开始

在 Windows 上打开 PowerShell，粘贴回车：

```powershell
irm https://rejtech.github.io/SETBoard/install.ps1 | iex
```

安装完成后自动进入**悬浮窗**状态：

- 点浮窗的「白板」图标 → 进入全屏白板
- 点浮窗的「批注」图标 → 在任意桌面内容上书写批注

## 系统要求

- Windows 10 / 11（64 位）
- 安装脚本会自动安装 .NET 9 运行时，无需手动准备
- 触屏、电磁笔、鼠标均可使用

## 项目链接

- 官网与一键安装：https://rejtech.github.io/SETBoard/
- 源码与下载：https://github.com/RejTech/SETBoard
- 问题反馈：https://github.com/RejTech/SETBoard/issues
