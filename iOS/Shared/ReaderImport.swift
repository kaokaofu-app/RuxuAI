import Foundation
import ImageIO
import PDFKit
import UniformTypeIdentifiers
import Vision

enum ReaderImport {
    // These are import limits. The model client applies its own request budget.
    static let textLimit = 60_000
    static let attachmentByteLimit = 32 * 1_024 * 1_024
    private static let pageLimit = 100
    private static let scannedPageLimit = 8

    struct ImportError: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }

    static func text(_ input: String, title: String, alreadyTruncated: Bool = false) throws -> ReadingSource {
        let prefix = input.unicodeScalars.prefix(textLimit + 1)
        let truncated = alreadyTruncated || prefix.count > textLimit
        let body = String(prefix.prefix(textLimit)).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !body.isEmpty else { throw ImportError("没有可读的正文。可以选中一段文字再分享。") }
        let note = truncated ? "\n\n【仅导入部分正文，后续内容未纳入。】" : ""
        return ReadingSource(title: truncated ? title + " · 部分正文" : title, text: body + note, image: nil)
    }

    static func image(_ data: Data) async throws -> ReadingSource {
        try Task.checkCancellation()
        guard !data.isEmpty, data.count <= attachmentByteLimit else {
            throw ImportError("图片为空或超过 32 MB，请先裁切图片。")
        }
        guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let info = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = (info[kCGImagePropertyPixelWidth] as? NSNumber)?.doubleValue,
              let height = (info[kCGImagePropertyPixelHeight] as? NSNumber)?.doubleValue,
              width > 0, height > 0, width * height <= 64_000_000 else {
            throw ImportError("无法读取图片，或图片尺寸过大。可以先用 iPhone 的截图裁切工具选择重点。")
        }
        // Apply EXIF orientation while decoding, before Vision sees the image.
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 2200,
            kCGImageSourceShouldCacheImmediately: true
        ]
        guard let bitmap = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
            throw ImportError("图片解码失败，请换用 PNG、JPEG 或 HEIC 图片。")
        }
        let content = (try? recognize(bitmap)) ?? ""
        try Task.checkCancellation()
        let jpeg = NSMutableData()
        guard let target = CGImageDestinationCreateWithData(jpeg as CFMutableData, UTType.jpeg.identifier as CFString, 1, nil) else {
            throw ImportError("无法生成图片预览。")
        }
        CGImageDestinationAddImage(target, bitmap, [kCGImageDestinationLossyCompressionQuality: 0.78] as CFDictionary)
        guard CGImageDestinationFinalize(target) else { throw ImportError("无法保存本次图片预览。") }
        if content.isEmpty {
            return ReadingSource(title: "图片 · 未识别到文字", text: "", image: jpeg as Data)
        }
        let limited = try text(content, title: "图片")
        return ReadingSource(title: limited.title, text: limited.text, image: jpeg as Data)
    }

    static func file(_ url: URL) async throws -> ReadingSource {
        guard url.isFileURL else { throw ImportError("请选择设备或文件 App 中的文档。") }
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        try Task.checkCancellation()
        let values = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey, .contentTypeKey])
        guard values.isRegularFile == true else { throw ImportError("请选择单个文件。") }
        let title = url.lastPathComponent
        let type = UTType(filenameExtension: url.pathExtension) ?? values.contentType
        if type?.conforms(to: .pdf) == true || type?.conforms(to: .image) == true {
            guard let size = values.fileSize, size > 0, size <= attachmentByteLimit else {
                throw ImportError("文件为空或超过 32 MB，请拆分后再导入。")
            }
            if type?.conforms(to: .pdf) == true { return try pdf(url, title: title) }
            let result = try await image(Data(contentsOf: url, options: [.mappedIfSafe]))
            return ReadingSource(title: result.title.replacingOccurrences(of: "图片", with: title),
                                 text: result.text, image: result.image)
        }
        guard type?.conforms(to: .plainText) == true || ["md", "markdown", "txt"].contains(url.pathExtension.lowercased()) else {
            throw ImportError("支持 PDF、UTF-8 文本、Markdown 和图片。")
        }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let limit = textLimit * 4
        var bytes = try handle.read(upToCount: limit + 1) ?? Data()
        let partial = bytes.count > limit
        if partial { bytes.removeLast() }
        if bytes.starts(with: [0xEF, 0xBB, 0xBF]) { bytes = Data(bytes.dropFirst(3)) }
        guard !bytes.contains(0) else { throw ImportError("该文件包含二进制数据，请导入 UTF-8 文本。") }
        var decoded = String(data: bytes, encoding: .utf8)
        if partial {
            // A byte ceiling may split the last UTF-8 sequence.
            for _ in 0..<3 where decoded == nil && !bytes.isEmpty {
                bytes.removeLast()
                decoded = String(data: bytes, encoding: .utf8)
            }
        }
        guard let content = decoded else { throw ImportError("文本编码无法识别，请使用 UTF-8 编码。") }
        return try text(content, title: title, alreadyTruncated: partial)
    }

    private static func recognize(_ image: CGImage) throws -> String {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        let supported = try request.supportedRecognitionLanguages()
        let preferred = ["zh-Hans", "zh-Hant", "en-US"].filter(supported.contains)
        if !preferred.isEmpty { request.recognitionLanguages = preferred }
        request.usesLanguageCorrection = true
        try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func pdf(_ url: URL, title: String) throws -> ReadingSource {
        guard let document = PDFDocument(url: url) else { throw ImportError("PDF 无法打开。") }
        guard !document.isLocked else { throw ImportError("PDF 需要密码，请先在文件 App 中解锁。") }
        guard document.pageCount > 0 else { throw ImportError("PDF 没有可读页面。") }
        guard document.allowsCopying else { throw ImportError("这份 PDF 的权限限制了正文提取。") }
        var pieces = [String](), remaining = textLimit, scanned = 0, missing = 0, last = 0
        var stopped: String?
        for index in 0..<min(document.pageCount, pageLimit) {
            try Task.checkCancellation()
            if remaining <= 0 { stopped = "达到正文长度上限"; break }
            guard let page = document.page(at: index) else { missing += 1; last = index + 1; continue }
            var content = page.string?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if content.isEmpty {
                if scanned >= scannedPageLimit { stopped = "达到单次扫描页识字上限（8 页）"; break }
                scanned += 1
                content = autoreleasepool {
                    guard let bitmap = pageImage(page) else { return "" }
                    return (try? recognize(bitmap)) ?? ""
                }
                try Task.checkCancellation()
            }
            last = index + 1
            if content.isEmpty { missing += 1; continue }
            let prefix = content.unicodeScalars.prefix(remaining + 1)
            pieces.append("【第 \(last) 页】\n" + String(prefix.prefix(remaining)))
            if prefix.count > remaining { stopped = "达到正文长度上限，第 \(last) 页仅导入一部分"; break }
            remaining -= prefix.count
        }
        guard !pieces.isEmpty else {
            throw ImportError("前 \(last) 页未识别到可读文字。可以截取清晰的一页，再使用图片解读。")
        }
        if stopped == nil && last < document.pageCount { stopped = "达到单次导入页数上限（\(pageLimit) 页）" }
        if let stopped { pieces.append("【\(stopped)。本次处理到第 \(last) 页，共 \(document.pageCount) 页。】") }
        if missing > 0 { pieces.append("【已处理范围内有 \(missing) 页未能提取文字。】") }
        return ReadingSource(title: stopped == nil && missing == 0 ? title : title + " · 部分正文",
                             text: pieces.joined(separator: "\n\n"), image: nil)
    }

    private static func pageImage(_ page: PDFPage) -> CGImage? {
        guard let pdfPage = page.pageRef else { return nil }
        let rect = pdfPage.getBoxRect(.cropBox)
        guard rect.width.isFinite, rect.height.isFinite, rect.width > 0, rect.height > 0 else { return nil }
        let rotated = pdfPage.rotationAngle % 180 != 0
        let w = rotated ? rect.height : rect.width, h = rotated ? rect.width : rect.height
        let width = max(1, Int(w / max(w,h) * 1600)), height = max(1, Int(h / max(w,h) * 1600))
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        let canvas = CGRect(x: 0, y: 0, width: CGFloat(width), height: CGFloat(height))
        context.setFillColor(CGColor(gray: 1, alpha: 1))
        context.fill(canvas)
        context.concatenate(pdfPage.getDrawingTransform(.cropBox, rect: canvas, rotate: 0, preserveAspectRatio: true))
        context.drawPDFPage(pdfPage)
        return context.makeImage()
    }
}
