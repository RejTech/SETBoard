# SETBoard

面向触屏教学设备的轻量白板软件，由 **Swift Education Technology（迅教科技）** 开发。

基于 [Avalonia](https://avaloniaui.net/) 11 + .NET 9，针对电子白板、触控一体机的笔类与手指输入做了专门优化。

## 功能

**书写**

- 书写、荧光笔、橡皮、选择、形状（直线 / 箭头 / 矩形 / 圆形 / 三角形）、插入图片
- 白、黄、红、绿、蓝、灰六色快捷色板，1–16 级粗细调节并带实时预览
- 压感与笔速参与粗细计算，配合半径限速、首尾收锋、向心 Catmull-Rom 曲线加密，得到自然的笔锋而不是折线拼接
- 触控笔落笔时自动屏蔽手指/手掌触摸，避免误画

**画布**

- 无限画布，滚轮缩放、中键拖动平移、双指捏合缩放与平移
- 多页白板，点阵背景可开关
- 撤销 / 重做
- 导出 PNG，存档为 `.setb`（JSON）

**课堂**

- 全屏授课模式
- 最小化到桌面后保留浮动工具条，可直接在全屏透明层上批注桌面内容

## 环境要求

- Windows 10 / 11（触屏直读依赖 Win32 `WM_POINTER` / `WM_TOUCH`）
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## 构建与运行

```bash
dotnet build
dotnet run
```

发布单文件（可选）：

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

## 快捷键

| 按键 | 功能 | 按键 | 功能 |
| --- | --- | --- | --- |
| `P` | 书写 | `Ctrl+Z` | 撤销 |
| `H` | 荧光笔 | `Ctrl+Y` | 重做 |
| `E` | 橡皮 | `Ctrl+S` | 保存 |
| `V` | 选择 | `Ctrl+O` | 打开 |
| `L` / `A` | 直线 / 箭头 | `Ctrl+0` | 重置视图 |
| `R` / `O` / `T` | 矩形 / 圆形 / 三角形 | `Ctrl+1` | 适应内容 |
| `PageUp` / `PageDown` | 上一页 / 下一页 | `F11` | 全屏 |
| `Delete` | 删除所选 | `Esc` | 退出全屏 / 回到选择工具 |

## 目录结构

```
Board/
  BoardCanvas.cs          画布渲染、相机、离屏导出
  BoardCanvas.Input.cs    鼠标 / 触控笔 / 触摸交互与手势
  BoardCanvas.RawInput.cs Win32 直读输入的落笔通道
  RawPointerInput.cs      WM_POINTER / WM_TOUCH 子类化直读
  InkStroke.cs            笔迹几何：压感、收锋、曲线加密、胶囊拼合
  ShapeItem.cs            图形对象
  ImageItem.cs            图片对象
  BoardDocument.cs        文档模型与 .setb 序列化
  Commands.cs             撤销 / 重做栈
MainWindow.axaml(.cs)     主界面与工具条
DesktopInk.cs             桌面工具条与桌面批注层
```

## 许可

暂未指定开源许可证，如需使用请联系迅教科技。
