# 如许AI · iPhone 版

在 iPhone 上把截图、文章或 PDF 交给如许AI，阅读解释，接着就原文提问。适用于 iOS 17 及以上。主 App 和系统分享面板共用模型设置、API Key 和本机阅读记录。

## 在 iPhone 上使用

1. 首次打开如许AI，进「设置」，填入自己的兼容 Chat Completions 的 HTTPS 地址、模型名称和 API Key。模型服务的用量可能产生费用。手机版需要单独配置。
2. 读截图时，可先用 iPhone 截图工具裁出需要解释的部分，再点「分享 → 如许AI」。也可以在如许AI 首页点「读截图」选择照片。
3. 读网页时，在 Safari 分享文章。如有选中文字，会优先读取选区；否则提取当前网页里的文章内容。如果其他 App 只提供网址，可回 Safari 分享正文，或直接分享一段选中文字。
4. 读论文时，从「文件」分享 PDF，或在首页点「读文档」。文本 PDF 会逐页提取正文，扫描页使用 Apple Vision 在本机识字。「设置」里的「学术论文」模式会侧重研究问题、方法、证据与局限。
5. 点「开始解读」或自己输入第一问。回答逐步显示，可以中途停止。关闭分享面板后，在主 App 的「最近阅读」里可以接着问。

默认向模型发送文字。读公式、图表时，可在设置中启用「发送原图」并选择支持图片的模型；发送的是处理后的截图。系统分享列表中若没看到如许AI，可到该列表的「更多」里查找。

**想少点几下**，可以在 iPhone「快捷指令」里新建一个指令，依次加入系统的「截屏」和如许AI 的「用如许AI阅读截图」，把上一步的图像作为输入。再在「设置 → 辅助功能 → 触控 → 轻点背面」中选中这条快捷指令。支持操作按钮的 iPhone 也可以把它指定给操作按钮。

## 在 Mac 上运行

用带 iOS 17 或更高版本 SDK 的 Xcode 打开 `RuxuReader.xcodeproj`。选择 **RuxuReader** 方案和 iPhone 模拟器，按 ⌘R 启动。工程自带 App、分享扩展和 XCTest 三个目标，无需安装第三方依赖。

在模拟器 Safari 中打开一篇文章，通过系统分享菜单启动如许AI，可验证扩展入口。要测试截图，从模拟器「照片」里分享一张样图。

## 安装到自己的 iPhone

1. 在 Xcode 中登录你的 Apple 开发者账号，把 iPhone 连到 Mac，在 Xcode 里选中这部手机。按设备提示信任 Mac、启用开发者模式。
2. 在工程级别的 **Build Settings → User-Defined** 中，将 `RUXU_BUNDLE_BASE` 从 `com.ruxuai.reader` 改为你团队可用的唯一标识。主 App、分享扩展和默认的 `RUXU_APP_GROUP` 都由它派生。
3. 为 **RuxuReader** 和 **RuxuShare** 两个目标选择同一签名团队，启用自动签名。在两个目标的 **Signing & Capabilities → App Groups** 中确认已注册、选中相同的组，且与 `RUXU_APP_GROUP` 展开后的值完全一致。项目使用该 App Group 同时共享受保护文件和钥匙串条目。如果签名提示团队不支持 App Groups，需要换用有此权限的团队。
4. 选择 **RuxuReader**，按 ⌘R。安装主 App 时会一同安装分享扩展。

苹果官方说明见 [实体设备运行](https://developer.apple.com/documentation/xcode/running-your-app-on-simulated-or-physical-devices)、[App Groups 配置](https://developer.apple.com/documentation/xcode/configuring-app-groups) 和 [共享钥匙串](https://developer.apple.com/documentation/security/sharing-access-to-keychain-items-among-a-collection-of-apps)。

## 测试与范围

在 Xcode 按 ⌘U，或在本目录运行 `bash verify.sh`。脚本使用已安装的 iOS 17+ iPhone 模拟器，也可通过 `RUXU_SIMULATOR_UDID` 指定设备。XCTest 使用本地文档和内存中的模型响应，覆盖多页 PDF、文本上限、不安全的 API 地址、上下文节选、中文流式回答与传输中断。真机需另外验证相册权限流程、系统分享面板和主 App 之间的钥匙串共享。

单次导入最多 60,000 个 Unicode 标量的正文。PDF 最多处理 100 页，其中扫描页最多识别 8 页；达到限制会在来源里说明。对于超过模型请求预算的材料，会按顺序选取开头、中间和结尾并标记节选。历史最多保存 50 条，最近 20 条可保留图片。阅读记录保存在本设备上；Windows 版和手机版各自管理数据。
