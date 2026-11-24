import SwiftUI
import UniformTypeIdentifiers
import UIKit

@MainActor
final class ShareViewController: UIViewController {
    override func viewDidLoad() {
        super.viewDidLoad()
        let input = extensionContext?.inputItems.compactMap { $0 as? NSExtensionItem } ?? []
        let panel = SharePanel(items: input) { [weak self] finished in
            guard let context = self?.extensionContext else { return }
            if finished {
                context.completeRequest(returningItems: nil, completionHandler: nil)
            } else {
                context.cancelRequest(withError: NSError(domain: NSCocoaErrorDomain, code: NSUserCancelledError))
            }
        }
        let host = UIHostingController(rootView: panel)
        addChild(host)
        host.view.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(host.view)
        NSLayoutConstraint.activate([
            host.view.topAnchor.constraint(equalTo: view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            host.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: view.trailingAnchor)
        ])
        host.didMove(toParent: self)
    }
}

private struct SharePanel: View {
    let items: [NSExtensionItem]
    let close: (Bool) -> Void
    @State private var source: ReadingSource?
    @State private var issue: String?
    @AppStorage("reader.appearance", store: ReadingAppearance.defaults) private var appearance = "system"

    var body: some View {
        NavigationStack {
            Group {
                if let source {
                    ReadingView(source: source)
                } else if let issue {
                    ContentUnavailableView("暂时无法导入", systemImage: "doc.text.magnifyingglass",
                                           description: Text(issue))
                } else {
                    ProgressView("正在读取分享内容…")
                }
            }
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(source == nil ? "取消" : "完成") { close(source != nil) }
                }
            }
        }
        .preferredColorScheme(ReadingAppearance.scheme(appearance))
        .task {
            do {
                let material = try await ShareInput.read(items)
                try Task.checkCancellation()
                source = material
            } catch is CancellationError {
                return
            } catch { issue = error.localizedDescription }
        }
    }
}

private enum ShareInput {
    private static let linkOnly = "只收到了网页链接。请在 Safari 打开文章后，从分享菜单选择如许AI，或选中要解读的文字再分享。"

    static func read(_ items: [NSExtensionItem]) async throws -> ReadingSource {
        let providers = items.flatMap { $0.attachments ?? [] }
        var hasWebLink = false
        // Safari supplies current-page text as a property list, including access-controlled pages the user can see.
        for provider in providers where provider.hasItemConformingToTypeIdentifier(UTType.propertyList.identifier) {
            if let raw = try? await item(provider, UTType.propertyList.identifier),
               let box = raw as? [String: Any],
               let result = box[NSExtensionJavaScriptPreprocessingResultsKey] as? [String: Any] {
                hasWebLink = true
                guard let body = result["text"] as? String, !body.isEmpty else { continue }
                let title = (result["title"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                let label = (title.isEmpty ? "Safari 网页" : String(title.prefix(220))) +
                    ((result["selection"] as? Bool) == true ? " · 选中文字" : "")
                return try ReaderImport.text(body, title: label, alreadyTruncated: result["truncated"] as? Bool ?? false)
            }
        }

        for provider in providers {
            try Task.checkCancellation()
            if let pdf = concreteType(.pdf, in: provider) {
                return try await fileRepresentation(provider, type: pdf, category: .pdf)
            }
            if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier),
               let raw = try? await item(provider, UTType.fileURL.identifier),
               let url = raw as? URL, url.isFileURL {
                return try await ReaderImport.file(url)
            }
        }

        // A browser may also share a preview image or the page title. Neither contains the article.
        for provider in providers where provider.hasItemConformingToTypeIdentifier(UTType.url.identifier) {
            guard let raw = try? await item(provider, UTType.url.identifier) else { continue }
            if let url = raw as? URL {
                if url.isFileURL { return try await ReaderImport.file(url) }
                hasWebLink = true
            } else if let literal = raw as? String, isLink(literal) { hasWebLink = true }
        }
        for provider in providers {
            try Task.checkCancellation()
            if !hasWebLink, let image = concreteType(.image, in: provider) {
                return try await fileRepresentation(provider, type: image, category: .image)
            }
            if let textID = concreteType(.plainText, in: provider) ?? concreteType(.text, in: provider),
               let raw = try? await item(provider, textID) {
                let body: String?
                var truncated = false
                if let text = raw as? String { body = text }
                else if let text = raw as? NSAttributedString { body = text.string }
                else if let data = raw as? Data, UTType(textID)?.conforms(to: .plainText) == true {
                    let prefix = data.prefix(ReaderImport.textLimit * 4)
                    truncated = prefix.count < data.count
                    body = String(data: prefix, encoding: .utf8) ?? (truncated ? String(decoding: prefix, as: UTF8.self) : nil)
                } else { body = nil }
                if let body {
                    let prefix = body.unicodeScalars.prefix(ReaderImport.textLimit + 1)
                    truncated = truncated || prefix.count > ReaderImport.textLimit
                    let cleaned = String(prefix.prefix(ReaderImport.textLimit)).trimmingCharacters(in: .whitespacesAndNewlines)
                    if isLink(cleaned) { hasWebLink = true }
                    else if !cleaned.isEmpty && (!hasWebLink || cleaned.contains("\n") || cleaned.count > 200) {
                        return try ReaderImport.text(cleaned, title: "分享文字", alreadyTruncated: truncated)
                    }
                }
            }
        }
        if hasWebLink { throw ReaderImport.ImportError(linkOnly) }
        if let caption = items.compactMap({ $0.attributedContentText?.string }).first(where: { !$0.isEmpty }) {
            if isLink(caption) { throw ReaderImport.ImportError(linkOnly) }
            return try ReaderImport.text(caption, title: "分享文字")
        }
        throw ReaderImport.ImportError("请分享一张图片、一份 PDF、UTF-8 文本或 Safari 文章。")
    }

    private static func isLink(_ string: String) -> Bool {
        let trimmed = string.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              let url = URL(string: trimmed), let scheme = url.scheme?.lowercased() else { return false }
        return (scheme == "http" || scheme == "https") && url.host != nil
    }

    private static func concreteType(_ type: UTType, in provider: NSItemProvider) -> String? {
        provider.registeredTypeIdentifiers.first { UTType($0)?.conforms(to: type) == true }
    }

    private static func item(_ provider: NSItemProvider, _ type: String) async throws -> NSSecureCoding {
        try await withCheckedThrowingContinuation { continuation in
            provider.loadItem(forTypeIdentifier: type, options: nil) { payload, error in
                if let payload { continuation.resume(returning: payload) }
                else { continuation.resume(throwing: error ?? ReaderImport.ImportError("分享来源没有提供可读数据。")) }
            }
        }
    }

    private static func fileRepresentation(_ provider: NSItemProvider, type: String, category: UTType) async throws -> ReadingSource {
        let fm = FileManager.default
        let folder = fm.temporaryDirectory.appendingPathComponent("import-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: folder, withIntermediateDirectories: false, attributes: [.protectionKey: FileProtectionType.complete])
        defer { try? fm.removeItem(at: folder) }
        // Provider-owned URLs expire as soon as the callback returns. Copy a bounded regular file inside the callback.
        let snapshot: URL = try await withCheckedThrowingContinuation { continuation in
            provider.loadFileRepresentation(forTypeIdentifier: type) { url, error in
                do {
                    guard let url else { throw error ?? ReaderImport.ImportError("分享来源没有提供文件。") }
                    let meta = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey, .isSymbolicLinkKey])
                    guard meta.isRegularFile == true, meta.isSymbolicLink != true,
                          let size = meta.fileSize, size > 0, size <= ReaderImport.attachmentByteLimit else {
                        throw ReaderImport.ImportError("文件为空或超过 32 MB，请裁切图片或拆分 PDF。")
                    }
                    let ext = category == .pdf ? "pdf" : (UTType(type)?.preferredFilenameExtension ?? "img")
                    let dest = folder.appendingPathComponent("source").appendingPathExtension(ext)
                    try fm.copyItem(at: url, to: dest)
                    try fm.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: dest.path)
                    continuation.resume(returning: dest)
                } catch { continuation.resume(throwing: error) }
            }
        }
        try Task.checkCancellation()
        let name = provider.suggestedName.map { String(($0 as NSString).lastPathComponent.prefix(180)) } ?? ""
        if category == .image {
            let result = try await ReaderImport.image(Data(contentsOf: snapshot, options: [.mappedIfSafe]))
            return ReadingSource(title: name.isEmpty ? result.title : result.title.replacingOccurrences(of: "图片", with: name),
                                 text: result.text, image: result.image)
        }
        let result = try await ReaderImport.file(snapshot)
        let title = name.isEmpty ? "分享的 PDF" : name
        return ReadingSource(title: result.title.replacingOccurrences(of: snapshot.lastPathComponent, with: title),
                             text: result.text, image: result.image)
    }
}
