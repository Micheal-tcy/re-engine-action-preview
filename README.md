# RE Action Preview

面向 Windows x64 的 RE Engine 模型与动画预览工具，基于 [PieceMetal/ReExtractor](https://github.com/PieceMetal/ReExtractor) 改进。

[下载 EXE / 便携包](https://github.com/Micheal-tcy/re-engine-action-preview/releases/latest)

## 功能

- 浏览本机 PAK 或已解包资源，预览模型、骨架、贴图与动画，支持 FBX 导出。
- 通过本机动作目录选择游戏、角色或怪物、武器与动作集，显示中文说明。
- 动作筛选：攻击、Idle、Locomotion、受击、交互等；支持本机分类修正。
- 按住右键观察，WASD 移动、QE 升降、Shift 加速；右键加滚轮调整相机速度。
- 左键环绕、中键平移、普通滚轮缩放；F 取景、空格播放、F11 全屏。
- 10 cm 小格、1 m 大格的连续地面网格；随当前动画姿态更新远近裁剪。

## 使用

1. 从 Releases 下载 `ReExtractor-ActionPreview.exe`，或解压 Windows x64 ZIP 后运行同名 EXE。运行不需要单独安装 .NET。
2. 在“文件”菜单打开本机 PAK、游戏目录或已解包资源目录。按需导入相应的资源路径 list。
3. 如已有自建动作目录，在“文件 → 打开动作预览目录（JSON）…”选择它。也可将 `catalog.json` 放在 EXE 旁，或用 `--quick-preview <目录JSON路径>` 指定外部文件。
4. FBX 导出需要自行安装 Blender，并在工具设置中选择其可执行文件。

本仓库和发行包只提供工具，不包含游戏安装包、模型、贴图、动画、私人动作目录、用户设置或运行日志。游戏资源需由使用者自行提供。预览是离线查看，不复现完整游戏的材质、物理、特效、连招状态机和所有附件行为。

默认会在程序旁创建 `ReExtractor-tools` 与输出目录。反馈按钮仅由用户点击后在浏览器打开本仓库 Issues，不自动提交日志、设备信息或截图。

## 更新

“检查更新”及启动检查使用本仓库的最新稳定 Release。更新器下载并校验同版本 `SHA256SUMS.txt` 中的 ZIP 哈希，再校验 EXE 文件版本。更新只替换工具 EXE，保留原启动参数和本机数据。离线使用不受更新检查影响。

## 从源码构建

需要 .NET 10 SDK。第三方解析库源码已随仓库提供，无须额外克隆子模块。

```powershell
dotnet build src/ReExtractor.Gui/ReExtractor.Gui.csproj -c Release
./scripts/package.ps1
```

打包输出在忽略的 `dist/` 目录，包括独立 EXE、便携 ZIP 和 SHA256SUMS.txt。版本由 GUI 项目文件的 Version / AssemblyVersion / FileVersion 一起维护。发布 `vX.Y.Z` 标签会触发 GitHub Actions 构建并生成 Release。

## 来源与许可

主程序基于 PieceMetal/ReExtractor，解析核心使用 RE-Engine-Lib，贴图解码使用 GDeflateNet。保留上游版权与各依赖的许可证；本项目不把上游未声明的整体许可证重新标为 MIT。详见 [第三方声明](THIRD_PARTY_NOTICES.md) 与各依赖目录内 LICENSE。
