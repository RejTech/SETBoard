# 自动更新

## 检查时机

- **自动**：启动后约 30 秒后台检查一次，发现新版本弹出更新提示（不打扰当前操作）
- **手动**：白板「更多 → 关于 SETBoard → 检查更新」

## 更新流程

1. 从 GitHub Releases API 获取最新版本号与更新说明
2. 提示「发现新版本」，可查看更新说明后选择更新
3. 点更新后：下载 `Updater.ps1`（Pages 优先，raw 回退），写入临时目录
4. 启动**独立 PowerShell 窗口**运行更新脚本，SETBoard 主进程立即退出
5. 外部脚本完成全部工作（进度可见）：
   - 停止 SETBoard 与守护进程
   - 下载预构建 zip（gh-proxy 优先 → jsdelivr 回退 → GitHub 直连），无需本地编译
   - 覆盖安装目录 → 启动新版 → 清理临时文件

> gh-proxy 在国内最快；jsdelivr CDN 次之；均不可用时自动回退 GitHub 直连。

## 常见问题

**更新后没有自动重启？**
可能是杀毒软件拦截了 PowerShell 的隐藏编码命令。允许该操作，或到 [Releases](https://github.com/RejTech/SETBoard/releases/latest) 手动下载覆盖安装。

**更新时提示正在运行？**
更新流程会先通知守护进程允许退出，再结束进程，不会被进程守护复活。若手动结束进程更新，请先使用卸载脚本中的退出逻辑或「更多 → 退出」。

**一直提示检查失败？**
检查更新走 GitHub API，需能访问 `api.github.com`。直连受限时，可稍后再试或手动下载新版。
