# 如许AI · RuxuAI

读论文、网页或技术文档时，圈出难懂的段落、公式或图表，就能在原文旁看解释并继续追问。Windows 版支持框选、正文提取和滚动长截图。iPhone 版可以通过系统分享菜单接收 Safari 文章、截图和 PDF。

## Windows

1. 在 64 位 Windows 上下载或克隆整个仓库。构建脚本使用 Windows 自带的 .NET Framework 编译器、WPF 和 Windows 元数据。
2. 在项目根目录打开 PowerShell，运行 `./build.ps1`，然后双击生成的 **如许AI.exe**。请将程序与 `Fonts`、`FluentIcons` 目录放在一起。
3. 在「设置 → 模型 API」中填写兼容 Chat Completions 的服务地址、模型名称和自己的 API Key。支持图片的模型可接收截图，纯文本模型可搭配本机文字识别。
4. 切回要读的窗口，默认按 **Ctrl + Alt + Q** 框选内容，或按 **Ctrl + Alt + E** 读取整篇。阅读选项中可以选择正文提取或滚轮长截图。

## iPhone

在 Mac 上用带 iOS 17 或更高版本 SDK 的 Xcode 打开 `iOS/RuxuReader.xcodeproj`，选中 **RuxuReader** 和一台 iPhone 模拟器，按 **⌘R**。真机签名、分享扩展配置和测试步骤见 [iPhone 版说明](iOS/README.md)。

## 数据与模型

两个平台分别保存自己的模型设置和阅读记录。发起解读或追问时，相关正文、所选图片或对话上下文会发送到你配置的模型服务。模型用量和费用取决于服务商。

## 开源许可

项目自有代码使用 [MIT 许可证](LICENSE)，允许使用、修改和商用。再分发时请保留版权和许可声明。仓库随附的第三方资源使用各自的许可。

| 资源 | 来源 | 随附许可 |
| --- | --- | --- |
| Fluent UI System Icons | [Microsoft](https://github.com/microsoft/fluentui-system-icons) | [MIT](FluentIcons/LICENSE) |
| 思源黑体 Source Han Sans | [Adobe](https://github.com/adobe-fonts/source-han-sans) | [SIL OFL 1.1](Fonts/LICENSE-SourceHanSans.txt) |
| Source Sans 3 | [Adobe](https://github.com/adobe-fonts/source-sans) | [SIL OFL 1.1](Fonts/LICENSE-SourceSans.md) |
