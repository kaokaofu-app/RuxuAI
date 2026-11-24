# 如许AI · RuxuAI

如许AI 是一个可以接入自己模型的阅读助手。读论文遇到公式、看官方文档没弄懂参数时，可以在 Windows 上圈出原文，或在 iPhone 上把文章分享给如许AI。它会结合这份材料给出解释，你可以接着问「这个符号是什么意思？」或「文中的哪条证据支持这个结论？」。

## 下载

| 你想做什么 | 下载 | 打开方式 |
| --- | --- | --- |
| 在 Windows 电脑上使用 | [Windows x64 便携版](https://github.com/kaokaofu-app/RuxuAI/releases/download/v1.0.0/RuxuAI-Windows-x64.zip) | 解压整个文件夹，双击 **如许AI.exe**。将程序与随包的字体和图标放在一起。 |
| 在 Mac 上运行或开发 iPhone 版 | [iOS Xcode 工程](https://github.com/kaokaofu-app/RuxuAI/releases/download/v1.0.0/RuxuAI-iOS-Xcode.zip) | 解压后打开 **iOS/RuxuReader.xcodeproj**。目标系统为 iOS 17 或更高版本。 |

iPhone 版目前以源码工程发布。可以在 Mac 的 iPhone 模拟器中运行，装到自己的 iPhone 则需要在 Xcode 中签名。[版本记录](https://github.com/kaokaofu-app/RuxuAI/releases)

## 第一次连接模型

打开设置里的「模型 API」。Windows 版可以先选服务商预设，再按自己账号的实际配置核对以下三项。

| 字段 | 怎么填 |
| --- | --- |
| API 地址 | 填服务商提供的、兼容 Chat Completions 的接口地址，例如 `https://api.deepseek.com/v1`。iPhone 版要求 HTTPS。 |
| 模型名称 | 从服务商控制台复制当前账号可用的模型 ID。准备直接发截图时，选支持图像输入的模型。 |
| API Key | 填同一服务商为你签发的密钥。Windows 接入本机免密服务时可留空。 |

保存后，先用一小段文字试读。Windows 设置页也可以点「测试连接」，它会向所选服务发送一次简短请求。使用云模型时，测试、解读和追问都可能按服务商规则计费。

## 在 Windows 上阅读

**解释一个局部。** 切到论文、浏览器或其他想读的窗口，按 **Ctrl + Alt + Q**。移动鼠标时会显示候选区域，单击选中，也可以按住拖动自由框选。想先写问题，就按住 **Shift** 再松开鼠标，例如先问「只解释公式中的分母」。对话框里按 **Enter** 发送追问，**Shift + Enter** 换行。

**阅读整篇文章。** 打开文章并切回正文窗口，按 **Ctrl + Alt + E**。在「设置 → 阅读选项 → 读取整篇时」里可以选择两种方式。

| 方式 | 什么时候用 | 阅读过程 |
| --- | --- | --- |
| 提取正文 | 网页文章、能选中文字的技术文档 | 长文先从开头、中间和结尾节选生成速览，再按需解读每段约 4,000 个估算 token 的正文。需要核对结论时，继续读对应段落。 |
| 滚轮长截图 | 扫描资料、复杂排版，或软件无法提取正文的页面 | 框住正文后向下滚动，每次保留几行与上一屏重叠。按 **Enter** 完成，**Esc** 取消。单次最多约 8 屏，高分辨率下可能提前达到图像上限。 |

**回到之前的问题。** 从托盘重新打开如许AI，在「阅读记录」中找到对应来源。对截图对话，可以点缩略图核对当时采集的内容，再继续问。

## 在 iPhone 上阅读

1. 在 Mac 上用带 iOS 17 或更高版本 SDK 的 Xcode 打开工程，选择 **RuxuReader** 和一台 iPhone 模拟器，按 **⌘R**。要用真机，为主 App 和分享扩展选择同一签名团队，并配置共用的 App Group。[真机安装步骤](iOS/README.md#安装到自己的-iphone)
2. 首次运行时，在 iPhone 版设置里单独填写模型服务。读网页可在 Safari 点「分享 → 如许AI」；如果已经选中一段文字，会优先读这一段。
3. 读论文可从「文件」分享 PDF。带文字层的页面直接提取正文，扫描页使用 Apple Vision 本机识字。读公式或图表也可以先截图裁剪，再通过分享面板发送。
4. 在分享面板里点「开始解读」或先输入自己的问题。关闭面板后，可以从主 App 的「最近阅读」继续这段对话。[快捷指令、导入上限和测试说明](iOS/README.md)

## 让回答适合手上的材料

- **学术论文**适合先看研究问题、方法和主要发现。要核对依据，可以追问「作者在哪张表里比较了这两种方法？」。
- **技术文档**适合看接口用途、参数和使用条件。例如圈出函数签名，再问「哪个参数必填？给我一个最小用法」。
- **快速回答**会让模型优先用一两句话给结论。Windows 版还可以按服务商支持情况调整「思考模式」。实际等待时间也受模型、材料长度和网络影响。
- **回答偏好**可以填写自己的提示词，例如「中文回答，保留英文术语；遇到公式先逐个解释符号」。它会用于首次解释和后续追问。

## 数据与模型

阅读记录和配置保存在各自设备上。Windows 使用当前账户加密保存密钥和阅读历史，iOS 使用钥匙串保存密钥。「本机识字」会在设备上将图片转为文字，解读时相关文字和对话上下文仍会发往你配置的模型服务；选择「发送原图」时，服务商还会收到相应图片。

## 常见问题

| 遇到的情况 | 可以怎么做 |
| --- | --- |
| 能发文字，发截图却报错 | 在阅读选项里切换到「本机识字」。如果问题依赖曲线、颜色或公式布局，改用支持图像输入的模型并选「发送原图」。 |
| 长截图显示「已采集部分内容」 | 两屏之间可能缺少可匹配的重叠区域。可以先解读已采集部分，再从断开处继续；重新采集时缩小每次滚动距离，把固定导航栏留在选区外。 |
| 快捷键没有反应 | 先确认程序在系统托盘运行，再到「设置 → 快捷键」检查是否冲突。框选、读取整篇、滚轮长截图和打开主窗口均可单独绑定。 |
| API 返回 401、403 或 404 | 401、403 优先检查密钥和模型访问权限；404 优先核对接口地址和模型 ID。不要在公开 Issue 中粘贴密钥。 |

## 从源码构建与反馈

**Windows** 需要 64 位 Windows、.NET Framework 4.8 和系统 Windows 元数据。在 PowerShell 中运行以下命令，构建成功后会在项目根目录生成可执行文件。

```powershell
git clone https://github.com/kaokaofu-app/RuxuAI.git
cd RuxuAI
.\build.ps1
```

**iOS** 的 Swift 源码、分享扩展和 XCTest 均在 `iOS/`。在 Mac 上选中 **RuxuReader** 方案，按 **⌘U** 运行测试，或在该目录运行 `bash verify.sh`。

反馈问题时请说明平台、版本、从哪个入口触发，以及预期和实际结果。涉及模型连接时可以附 HTTP 状态码，并遮住密钥和阅读材料中的个人信息。[提交问题](https://github.com/kaokaofu-app/RuxuAI/issues)

## 开源许可

项目自有代码使用 [MIT 许可证](LICENSE)，允许使用、修改和商用。再分发时请保留版权和许可声明。仓库随附的第三方资源使用各自的许可。

| 资源 | 来源 | 随附许可 |
| --- | --- | --- |
| Fluent UI System Icons | [Microsoft](https://github.com/microsoft/fluentui-system-icons) | [MIT](FluentIcons/LICENSE) |
| 思源黑体 Source Han Sans | [Adobe](https://github.com/adobe-fonts/source-han-sans) | [SIL OFL 1.1](Fonts/LICENSE-SourceHanSans.txt) |
| Source Sans 3 | [Adobe](https://github.com/adobe-fonts/source-sans) | [SIL OFL 1.1](Fonts/LICENSE-SourceSans.md) |
