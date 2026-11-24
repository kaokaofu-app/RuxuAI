# RuxuAI for iPhone

[简体中文](README.md) · **English** · [Project overview](../README.en.md)

Share a screenshot, article or PDF to RuxuAI, ask a question, and return to the same reading later. The main app and share extension use the same model settings, API key and local history. Requires iOS 17 or later.

## Run on a Mac

Open **RuxuReader.xcodeproj** with Xcode and an iOS 17 or later SDK. Choose the **RuxuReader** scheme and an iPhone simulator, then press **⌘R**. The project includes the app, share extension and XCTest target. It has no third-party package dependencies.

To try the share extension, open an article in the simulator's Safari and choose **Share → 如许AI**. To try an image, share a sample from Photos. If the extension is missing, check **More** in the share sheet.

## Install on your iPhone

1. Sign in to your Apple developer account in Xcode. Connect your iPhone, select it as the run destination, trust the Mac and enable Developer Mode when prompted.
2. In the project's **Build Settings → User-Defined**, replace `RUXU_BUNDLE_BASE` (initially `com.ruxuai.reader`) with an identifier available to your team. The app, extension and default `RUXU_APP_GROUP` identifiers derive from it.
3. Select the same signing team and automatic signing for **RuxuReader** and **RuxuShare**. Under **Signing & Capabilities → App Groups**, register and select the same group for both targets. Its identifier must match the expanded `RUXU_APP_GROUP`. The app and extension use it to share protected files and Keychain items. Your team must support this capability.
4. Select **RuxuReader** and press **⌘R**. Xcode installs the app and its share extension together. [Apple device setup](https://developer.apple.com/documentation/xcode/running-your-app-on-simulated-or-physical-devices) · [App Groups](https://developer.apple.com/documentation/xcode/configuring-app-groups)

## Start a reading

Open **设置** (Settings) and enter your Chat Completions-compatible HTTPS endpoint, model ID and API key. Configure these separately from the Windows app. Your provider may charge for requests.

- **Screenshot.** Crop the image to the passage or diagram, then choose **Share → 如许AI**. You can also choose **读截图** (Read screenshot) on the home screen. The app sends text by default. To discuss the shape of a plot, enable **发送原图** (Send image) and choose a model with image support.
- **Web article.** Share from Safari. Selected text takes priority; otherwise, the extension extracts the article text. If another app shares only a URL, open the page in Safari and share it from there, or share a text selection.
- **Paper.** Share a PDF from Files or choose **读文档** (Read document). Pages with a text layer use direct extraction; scanned pages use Apple Vision on the device. The **学术论文** (Academic paper) profile focuses on the research question, methods, evidence and limitations.

Tap **开始解读** (Start reading) or ask your own question. Answers stream in and can be stopped. To continue after closing the share sheet, open **最近阅读** (Recent readings) in the app. To request English replies, add “Answer in English” to your answer preferences.

## Read a screenshot with a shortcut

In Apple's Shortcuts app, create a shortcut with **Take Screenshot**, followed by **用如许AI阅读截图**. Pass the image from the first action to the second. In **Settings → Accessibility → Touch → Back Tap**, assign the shortcut to a tap gesture. On a supported iPhone you can also assign it to the Action button.

## Limits and tests

One import accepts up to 60,000 Unicode scalars of text. A PDF import handles up to 100 pages, including OCR for up to eight scanned pages. The source view indicates when a limit was reached. If the text exceeds the model request budget, the app marks excerpts from the beginning, middle and end. The device keeps up to 50 readings, with images retained for up to the 20 most recent.

Press **⌘U** in Xcode or run `bash verify.sh` from this directory. The script selects an installed iPhone simulator running iOS 17 or later. Set `RUXU_SIMULATOR_UDID` to choose a specific simulator. The tests use local documents and simulated model responses to cover PDF processing, input limits, endpoint validation, excerpts, Chinese text streaming and interrupted responses. Check photo permissions, the share sheet and shared Keychain access on a physical device as well.
