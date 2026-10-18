# SoundSync 简体中文汉化版

基于 [sugumar247/SoundSync](https://github.com/sugumar247/SoundSync) 的 `v2.0.0` 程序源码制作，保留原项目的音频转发功能和 MIT 许可。

## 汉化内容

- 桌面主界面、深浅色主题按钮、托盘和任务栏菜单。
- 音量、延时、均衡器、设备连接类型及系统设置的操作说明。
- 运行状态、常见音频错误、自动恢复及开机启动提示。
- 手机和浏览器使用的网络收听网页及控制提示。
- 中文字体适配，以及原版更新入口的说明。

当前界面固定使用简体中文。品牌名、Windows 提供的设备名、技术单位、协议字段、快捷键及底层系统异常原文保持原样。构建标识为 `2.0.0-zh-CN.1`。

## 使用方法

1. 运行编译输出中的 `SoundSync.exe`。Windows x64 自包含构建无需另外安装 .NET 运行时。
2. Windows 默认输出设备会自动作为音频来源。勾选需要同时播放的其他输出设备。
3. 点击“连接并开始音频转发”。再次点击可断开；窗口位于前台时，也可按 `C` 切换连接，按 `M` 切换静音。
4. 如需手机收听，在同一局域网中打开程序显示的完整收听链接，并点击网页上的“连接并收听”。

程序沿用原版的 `%LOCALAPPDATA%\SoundSync` 配置。请先退出原版再运行中文版，避免两个实例争用音频或网络端口。回退时退出中文版，再运行原版即可。

“下载原版更新”会打开上游发布页，下载内容可能不包含本汉化。

## 从源码编译

需要 Windows 和 .NET 10 SDK。在仓库根目录运行：

```powershell
dotnet publish SoundSync/SoundSync.csproj -c Release -o publish -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

产物位于 `publish/SoundSync.exe`，为 Windows x64 自包含单文件程序。

## 验证情况

- Release 构建及自包含打包成功。
- 已实际启动并检查桌面深色、浅色界面；已检查网络收听网页的初始界面及 JavaScript 语法。
- 临时排除上游存在编译问题的 `MainViewModelTests.cs` 后，其余 9 项原有测试通过。该文件使用了不存在的 `MMDevice` 无参构造函数；本仓库保留原测试代码。
- 尚未验证真实多设备播放或手机音频串流。

## 原项目与许可

原作者及其他贡献者的版权和 MIT 许可见 [LICENSE](LICENSE)。英文功能说明、工作原理及上游文档见 [README.md](README.md)。
