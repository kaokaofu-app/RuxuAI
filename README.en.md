# RuxuAI · 如许AI

[简体中文](README.md) · **English**

RuxuAI helps you read papers, web articles and technical documentation using a model you choose. Select a formula on Windows or share a paper from your iPhone, then ask a question about that exact passage. The conversation keeps the source in context for follow-up questions.

## Downloads

| Platform | Download | Getting started |
| --- | --- | --- |
| Windows x64 | [Portable app](https://github.com/kaokaofu-app/RuxuAI/releases/download/v1.0.0/RuxuAI-Windows-x64.zip) | Extract the entire ZIP and open **如许AI.exe**. Keep the bundled fonts and icons alongside it. |
| iPhone, iOS 17 or later | [Xcode project](https://github.com/kaokaofu-app/RuxuAI/releases/download/v1.0.0/RuxuAI-iOS-Xcode.zip) | On a Mac, open **iOS/RuxuReader.xcodeproj** in Xcode. Run it in an iPhone simulator or sign it for your device. [iPhone setup guide](iOS/README.en.md) |

Version 1.0 ships with a Chinese interface. This guide includes the Chinese labels you will see on screen. For English responses, enter “Answer in English” under **回答偏好** (answer preferences). [Release notes](https://github.com/kaokaofu-app/RuxuAI/releases)

## Connect a model

Open **设置** (Settings), then **模型 API** (Model API). On Windows, you can start with a provider preset. Check these three fields against your provider's dashboard:

| On-screen field | What to enter |
| --- | --- |
| API 地址 | Your provider's Chat Completions-compatible base URL. For example, DeepSeek uses `https://api.deepseek.com/v1`. The iPhone app requires HTTPS. |
| 模型名称 | The exact model ID available to your account. Choose a model that accepts images if you plan to send diagrams or screenshots. |
| API Key | A key issued by the same provider. On Windows, a local service that allows unauthenticated requests can use an empty key. |

Choose **保存** (Save) and try a short paragraph. On Windows, **测试连接** (Test connection) sends a small request to your provider. Tests, explanations and follow-up questions may incur usage charges.

## Read on Windows

- **Explain a selection.** Bring the paper or browser to the front and press **Ctrl + Alt + Q**. Move the pointer to highlight a region and click to select it, or drag to draw your own. Hold **Shift** as you release the mouse to enter your own question first, such as “What does the denominator measure?” In the conversation, **Enter** sends and **Shift + Enter** inserts a line break.
- **Read an article.** Focus its window and press **Ctrl + Alt + E**. Under **阅读选项** (Reading options), select **提取正文** (Extract text) for selectable text. For a long article, the first overview draws on excerpts from the start, middle and end. Read individual sections, each about 4,000 estimated tokens, to check a particular claim.
- **Capture a scrolling page.** Select **滚轮长截图** (Scrolling capture) for scans or complex layouts. Frame the article body and scroll down, leaving a few lines of overlap each time. Press **Enter** to finish or **Esc** to cancel. A capture covers up to about eight screens and may reach the image limit sooner on high-resolution displays.
- **Resume a conversation.** Open RuxuAI from the system tray and choose **阅读记录** (Reading history). For a screenshot, click its thumbnail to check which passage you captured.

## Read on iPhone

Share an article from Safari, a screenshot from Photos or a PDF from Files to **如许AI**. Safari gives priority to selected text. For PDFs, the app extracts the text layer where available and uses on-device Apple Vision OCR for scanned pages. Tap **开始解读** (Start reading) or enter your first question. Later, reopen **最近阅读** (Recent readings) in the main app to continue. The phone has its own model settings. [Installation, Shortcuts and import limits](iOS/README.en.md)

## Adjust the answer

Choose **学术论文** (Academic paper) to focus on the research question, methods and evidence, or **技术文档** (Technical documentation) for an interface's purpose, parameters and requirements. **快速** (Fast) asks for a one- or two-sentence answer first. Windows also offers provider-specific thinking settings. Response time depends on the model, input size and network.

Use **回答偏好** (Answer preferences) to set instructions for initial explanations and follow-ups. For example: “Answer in English. Define each symbol before walking through the equation.”

## Privacy and troubleshooting

Settings and reading history are stored separately on each device. Windows protects keys and reading history with the current Windows account. iOS stores the key in Keychain. With **本机识字** (On-device OCR), the extracted text and relevant conversation context go to your model service when you request an explanation. With **发送原图** (Send image), the service also receives the image.

| Symptom | What to check |
| --- | --- |
| Text works but screenshots fail | Select on-device OCR for a text-only model. For questions about colors, plots or layout, use an image-capable model and enable **发送原图**. |
| A scrolling capture ends early | Read the captured portion or continue from where it stopped. Scroll in smaller steps and leave fixed navigation bars outside the frame. |
| A shortcut does nothing | Check that RuxuAI is running in the system tray. Open **设置 → 快捷键** (Settings → Shortcuts) to check for conflicts and reassign keys. |
| The API returns 401, 403 or 404 | For 401 or 403, check the key and your account's model access. For 404, check the endpoint and model ID. |

## Build and contribute

On 64-bit Windows with .NET Framework 4.8 and Windows metadata installed, run the following in PowerShell. The build writes **如许AI.exe** to the project root.

```powershell
git clone https://github.com/kaokaofu-app/RuxuAI.git
cd RuxuAI
.\build.ps1
```

The iOS app, share extension and tests are in `iOS/`. On a Mac, select the **RuxuReader** scheme and press **⌘U**, or run `bash verify.sh` from that directory. When reporting a problem, include your platform, version, steps and expected result. Remove API keys and private reading material from logs and screenshots. [Report an issue](https://github.com/kaokaofu-app/RuxuAI/issues)

## License

The project's own code is [MIT licensed](LICENSE). When redistributing it, retain the copyright and license notices. Bundled resources carry their own licenses:

| Resource | Source | Bundled license |
| --- | --- | --- |
| Fluent UI System Icons | [Microsoft](https://github.com/microsoft/fluentui-system-icons) | [MIT](FluentIcons/LICENSE) |
| Source Han Sans | [Adobe](https://github.com/adobe-fonts/source-han-sans) | [SIL OFL 1.1](Fonts/LICENSE-SourceHanSans.txt) |
| Source Sans 3 | [Adobe](https://github.com/adobe-fonts/source-sans) | [SIL OFL 1.1](Fonts/LICENSE-SourceSans.md) |
