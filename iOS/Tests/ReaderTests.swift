import XCTest
import UIKit
@testable import RuxuReader

final class ReaderTests: XCTestCase {
    private func wire(_ value: String) -> AsyncStream<UInt8> {
        AsyncStream { continuation in
            for byte in value.utf8 { continuation.yield(byte) }
            continuation.finish()
        }
    }

    func testEndpointAndCredentialBoundaries() throws {
        let root = try ReaderAPI.address("https://example.invalid", withKey: true)
        XCTAssertEqual(root.path, "/v1/chat/completions")
        let full = try ReaderAPI.address("https://example.invalid/custom/chat/completions/", withKey: false)
        XCTAssertEqual(full.path, "/custom/chat/completions")
        XCTAssertThrowsError(try ReaderAPI.address("http://example.invalid", withKey: false))
        XCTAssertThrowsError(try ReaderAPI.address("https://user:pass@example.invalid", withKey: true))
        XCTAssertThrowsError(try ReaderAPI.address("http://127.0.0.1:8080", withKey: true))
    }

    func testTextModeAndLongContext() throws {
        let material = "BEGIN " + String(repeating: "论文正文。", count: 12_000) + " END"
        let source = ReadingSource(title: "测试资料", text: material, image: Data([0xff, 0xd8, 0xff]))
        var config = ReaderConfig()
        config.fast = true
        config.useVision = false
        config.model = "fixture"
        let data = try ReaderAPI.body(source: source, previous: [Turn(question: "上一问", answer: "上一答")],
                                      question: "再解释一下", config: config, url: URL(fileURLWithPath: "/fixture"))
        let payload = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let messages = try XCTUnwrap(payload["messages"] as? [[String: Any]])
        let sent = try XCTUnwrap(messages[1]["content"] as? String)
        XCTAssertTrue(sent.contains("BEGIN"))
        XCTAssertTrue(sent.contains("END"))
        XCTAssertTrue(sent.contains("节选"))
        XCTAssertLessThan(sent.utf8.count, material.utf8.count)
        XCTAssertEqual(messages.last?["content"] as? String, "再解释一下")
        XCTAssertEqual(messages[messages.count - 2]["content"] as? String, "上一答")
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("base64"))
    }

    @MainActor func testStreamingChineseAndInterruptedResponse() async throws {
        let first = "data: {\"choices\":[{\"delta\":{\"content\":\"总\"}}]}\r\n\r\n"
        let second = "data: {\"choices\":[{\"delta\":{\"content\":\"结\"}}]}\n\ndata: [DONE]\n\n"
        var text = ""
        try await ReaderAPI.consume(wire(first + second)) { text += $0 }
        XCTAssertEqual(text, "总结")
        text = ""
        do {
            try await ReaderAPI.consume(wire(first)) { text += $0 }
            XCTFail("An interrupted stream must not be marked complete")
        } catch { XCTAssertEqual(text, "总") }
    }

    @MainActor func testNonStreamingCompatibility() async throws {
        let json = #"{"choices":[{"message":{"content":"已收到"},"finish_reason":"stop"}]}"#
        var text = ""
        try await ReaderAPI.consume(wire(json)) { text += $0 }
        XCTAssertEqual(text, "已收到")
    }

    func testTextImportLimitAndInvalidFile() async throws {
        let source = try ReaderImport.text(String(repeating: "文", count: 60_001), title: "长文")
        XCTAssertTrue(source.title.contains("部分"))
        XCTAssertThrowsError(try ReaderImport.text("   \n", title: "空白"))
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".txt")
        try Data([0, 1, 2]).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        do {
            _ = try await ReaderImport.file(file)
            XCTFail("binary content must not be imported as text")
        } catch { XCTAssertFalse(error.localizedDescription.isEmpty) }
    }

    @MainActor func testPDFIncludesLastPage() async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".pdf")
        defer { try? FileManager.default.removeItem(at: file) }
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 400, height: 400))
        try renderer.writePDF(to: file) { pdf in
            for text in ["FIRST PAGE", "LAST PAGE"] {
                pdf.beginPage()
                (text as NSString).draw(at: CGPoint(x: 20, y: 20), withAttributes: [.font: UIFont.systemFont(ofSize: 16)])
            }
        }
        let source = try await ReaderImport.file(file)
        XCTAssertTrue(source.text.contains("FIRST PAGE"))
        XCTAssertTrue(source.text.contains("LAST PAGE"))
    }
}
