# SETBoard

面向触屏教学设备的轻量白板软件，由 **Swift Education Technology（迅教科技）** 开发。

基于 [Avalonia](https://avaloniaui.net/) 11 + .NET 9，针对电子白板、触控一体机的笔类与手指输入做了专门优化。

## 功能

**书写**

- 移动画布、书写、荧光笔、橡皮、选择、填色；在“插入”菜单中选择形状（直线 / 箭头 / 矩形 / 圆形 / 三角形）、函数图像、三维几何体或图片
- 函数图像：LaTeX 输入（`\frac{1}{1+x^2}`、`\sqrt{x}`、`\sin(a x)`）并按数学排版显示；同一个坐标系里可叠加多条曲线，每条自动分配不同颜色、各自用滑块调节参数 a；内置一次函数、二次函数、幂函数、指数函数、对数函数预设；自带数学键盘；曲线交点自动标注数学坐标，缩放时曲线粗细、交点和文字同步变化，但求解精度不受显示尺寸影响
- 三维几何体：正方体、长方体、四棱锥、圆柱、圆锥、球，按教材画法用实线画可见棱、虚线画被遮挡的棱；选中后拖动即可改变视角做三维旋转；用「填色」工具点某个面可单面填色，再点一次清除
- 填色工具对手绘同样有效：点手绘笔画围出的闭合区域即可上色（泛洪填充，未闭合则不填），填色垫在笔画下方，不影响线条
- 白、黄、红、绿、蓝、灰、橙、紫、青、黑十色快捷色板和一个自定义十六进制颜色入口，1–16 级粗细调节并带实时预览
- 压感与笔速参与粗细计算，配合半径限速、首尾收锋、向心 Catmull-Rom 曲线加密，得到自然的笔锋而不是折线拼接
- 触控笔落笔时自动屏蔽手指/手掌触摸，避免误画

**画布**

- 无限画布，滚轮缩放、中键拖动平移、双指捏合缩放与平移
- 多页白板，点阵背景可开关
- 撤销 / 重做
- 再次点击橡皮可展开清页滑块，从左端拖至右端清空当前页（可撤销）
- 单选图片、函数图像、形状或三维几何体时拖动右下角手柄可等比缩放，支持撤销 / 重做
- 导出 PNG，存档为 `.setb`（JSON）
- “更多”菜单按文件、视图、页面和应用信息分组，点阵状态可直接勾选，删除页面等危险操作带确认
- 页面切换、适应内容 / 重置视图、工具状态、浮层与提示均提供轻量动画反馈

**课堂**

- 全屏授课模式
- 最小化到桌面后保留浮动工具条，可直接在全屏透明层上批注桌面内容

## 环境要求

- Windows 10 / 11（触屏直读依赖 Win32 `WM_POINTER` / `WM_TOUCH`）
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

## 构建与运行

Windows 下可双击根目录的 `quick-build.bat` 快速构建 Debug 版，输出位于 `artifacts/quick-build/SETBoard.exe`。脚本优先使用 `%USERPROFILE%\.dotnet\dotnet.exe` 中的 .NET 9 SDK，未找到时再检查 PATH；构建结果会停留在窗口中，便于查看错误。

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
| `M` | 移动画布 |  |  |
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
  BoardCanvas.Fill.cs     填色：三维面填充与手绘闭合区域泛洪填充
  BoardCanvas.RawInput.cs Win32 直读输入的落笔通道
  RawPointerInput.cs      WM_POINTER / WM_TOUCH 子类化直读
  InkStroke.cs            笔迹几何：压感、收锋、曲线加密、胶囊拼合
  ShapeItem.cs            图形对象
  FunctionItem.cs         函数图像：多曲线采样、坐标轴、渐近线断开、交点坐标标记
  SolidItem.cs            三维几何体：正交投影、隐藏棱虚线、旋转
  MathExpression.cs       函数表达式解析与求值
  LatexMath.cs            LaTeX 转求值表达式
  ImageItem.cs            图片对象
  BoardDocument.cs        文档模型与 .setb 序列化
  Commands.cs             撤销 / 重做栈
MainWindow.axaml(.cs)     主界面与工具条
FunctionDialog.cs         函数图像对话框与数学键盘
Assets/icon.ico           应用图标（icon.svg 为矢量源文件）
DesktopInk.cs             桌面工具条与桌面批注层
version.json              版本号（发版时由 Action 自动改写）
```

## 许可

SETBoard 仅依据 [GNU General Public License v3.0](LICENSE)（`GPL-3.0-only`）开源。你可以使用、研究、修改和分发本项目；分发本项目或其修改版本时，需要继续以 GPLv3 提供对应源码，并保留版权与许可声明。

根目录 `LICENSE` 同时收录具有法律效力的英文原文和简体中文非官方译文。中文译文仅供阅读参考，如与英文原文存在差异，以英文原文为准。

项目中包含的第三方组件与资源遵循各自的许可证；相关声明请查看对应源码或资源目录。
