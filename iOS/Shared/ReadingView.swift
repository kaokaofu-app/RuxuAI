import SwiftUI
import UIKit
import ImageIO

enum ReadingAppearance {
    static let defaults = (Bundle.main.object(forInfoDictionaryKey: "RuxuAppGroup") as? String)
        .flatMap { UserDefaults(suiteName: $0) } ?? .standard

    static func scheme(_ value: String) -> ColorScheme? {
        switch value {
        case "light": return .light
        case "dark": return .dark
        default: return nil
        }
    }
}

@MainActor
private final class ReadingSession: ObservableObject {
    @Published private(set) var conversation: Conversation
    @Published private(set) var isAnswering = false
    @Published var requestError: String?
    @Published var storageError: String?
    private var job: Task<Void, Never>?
    private var activeRequest: UUID?
    private var receivedText = false
    private var attemptedQuestion = ""
    private var retryExisting = false

    init(_ conversation: Conversation) {
        self.conversation = conversation
    }

    deinit { job?.cancel() }

    func ask(_ text: String, retry: Bool = false) {
        let question = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !question.isEmpty, !isAnswering else { return }
        attemptedQuestion = question
        retryExisting = retry
        let config: ReaderConfig
        do {
            config = try ReaderConfig.load()
            guard !config.endpoint.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  !config.model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                requestError = "请先在设置里填写模型 API 地址和模型名称。"
                return
            }
        } catch {
            requestError = "读取模型设置失败。\(error.localizedDescription)"
            return
        }
        requestError = nil
        let turn: Turn
        if retry, let last = conversation.turns.last, last.question == question {
            turn = last
        } else {
            turn = Turn(id: UUID(), question: question, answer: "")
            conversation.turns.append(turn)
        }
        let previous = conversation.turns.dropLast().filter { !$0.answer.isEmpty }
        retryExisting = true
        conversation.updated = Date()
        persist()
        let request = UUID()
        activeRequest = request
        receivedText = false
        isAnswering = true
        let source = conversation.source
        job = Task { [weak self] in
            do {
                try await ReaderAPI.answer(source: source, previous: previous, question: question, config: config) { [weak self] chunk in
                    guard !chunk.isEmpty, let self, self.activeRequest == request,
                          self.conversation.turns.last?.id == turn.id else { return }
                    let index = self.conversation.turns.count - 1
                    if self.receivedText { self.conversation.turns[index].answer += chunk }
                    else { self.conversation.turns[index].answer = chunk; self.receivedText = true }
                }
                guard let self, self.activeRequest == request else { return }
                if !self.receivedText {
                    self.requestError = "模型结束了回复，但没有返回文字。可以重试或检查模型设置。"
                }
                self.finish()
            } catch {
                guard let self, self.activeRequest == request else { return }
                self.requestError = error.localizedDescription
                self.finish()
            }
        }
    }

    func stop() {
        guard isAnswering else { return }
        job?.cancel()
        finish()
    }

    func retry() { ask(attemptedQuestion, retry: retryExisting) }

    func persist() {
        guard !conversation.turns.isEmpty else { return }
        do {
            try ReadingHistory.save(conversation)
            storageError = nil
        } catch {
            storageError = "未能保存阅读记录。\(error.localizedDescription)"
        }
    }

    private func finish() {
        activeRequest = nil
        job = nil
        isAnswering = false
        conversation.updated = Date()
        persist()
    }
}

@MainActor
struct ReadingView: View {
    @StateObject private var session: ReadingSession
    @State private var question = ""
    @State private var showSettings = false
    @State private var showSource = false
    @State private var thumb: UIImage?
    @State private var nearBottom = true
    @FocusState private var editing: Bool
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage("reader.appearance", store: ReadingAppearance.defaults) private var appearance = "system"
    private let scrollEnd = "ruxu-reading-bottom"

    init(source: ReadingSource) {
        _session = StateObject(wrappedValue: ReadingSession(Conversation(id: UUID(), source: source, turns: [], updated: Date())))
    }

    init(conversation: Conversation) {
        _session = StateObject(wrappedValue: ReadingSession(conversation))
    }

    var body: some View {
        VStack(spacing: 0) {
            sourceHeader
            Divider()
            ScrollViewReader { proxy in
                GeometryReader { viewport in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 22) {
                            if session.conversation.turns.isEmpty {
                                Button {
                                    session.ask("请解释这段内容。")
                                } label: {
                                    Label("开始解读", systemImage: "sparkles")
                                        .padding(.horizontal, 8).padding(.vertical, 4)
                                }
                                .buttonStyle(.borderedProminent).buttonBorderShape(.capsule)
                            }
                            ForEach(session.conversation.turns, id: \.id) { turn in
                                turnView(turn)
                            }
                            if let error = session.requestError {
                                VStack(alignment: .leading, spacing: 8) {
                                    Label(error, systemImage: "exclamationmark.circle").font(.callout)
                                    HStack {
                                        Button("重试") { session.retry() }
                                        Button("模型设置") { showSettings = true }
                                    }.buttonStyle(.bordered)
                                }
                            }
                            if let error = session.storageError {
                                VStack(alignment: .leading, spacing: 6) {
                                    Text(error).font(.footnote)
                                    Button("重新保存") { session.persist() }.font(.footnote.bold())
                                }
                            }
                            Color.clear.frame(height: 1).id(scrollEnd)
                                .background(GeometryReader { position in
                                    Color.clear.preference(key: ReadingBottom.self, value: position.frame(in: .named("readingScroll")).maxY)
                                })
                        }
                        .frame(maxWidth: 760, alignment: .leading)
                        .padding(18).frame(maxWidth: .infinity, alignment: .center)
                    }
                    .coordinateSpace(name: "readingScroll")
                    .scrollDismissesKeyboard(.interactively)
                    .onPreferenceChange(ReadingBottom.self) { bottom in
                        nearBottom = bottom <= viewport.size.height + 80
                    }
                    .onChange(of: session.conversation.turns.count) { _, _ in
                        proxy.scrollTo(scrollEnd, anchor: .bottom)
                    }
                    .onChange(of: session.conversation.turns.last?.answer.count) { _, _ in
                        if nearBottom { proxy.scrollTo(scrollEnd, anchor: .bottom) }
                    }
                    .overlay(alignment: .bottom) {
                        if !nearBottom {
                            Button { withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo(scrollEnd, anchor: .bottom) } } label: {
                                Image(systemName: "arrow.down").frame(width: 40, height: 40)
                                    .background(.regularMaterial, in: Circle())
                            }
                            .accessibilityLabel("查看最新回复").padding(.bottom, 6)
                        }
                    }
                }
            }
            composer
        }
        .navigationTitle("阅读解读").navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button { showSettings = true } label: { Image(systemName: "slider.horizontal.3") }
                    .accessibilityLabel("阅读设置")
            }
        }
        .task(id: session.conversation.id) {
            guard let bytes = session.conversation.source.image else { return }
            let result = await Task.detached(priority: .utility) { () -> Data? in
                guard let src = CGImageSourceCreateWithData(bytes as CFData, nil),
                      let image = CGImageSourceCreateThumbnailAtIndex(src, 0, [
                          kCGImageSourceCreateThumbnailFromImageAlways: true,
                          kCGImageSourceCreateThumbnailWithTransform: true,
                          kCGImageSourceThumbnailMaxPixelSize: 240
                      ] as CFDictionary) else { return nil }
                return UIImage(cgImage: image).pngData()
            }.value
            if !Task.isCancelled, let result { thumb = UIImage(data: result) }
        }
        .sheet(isPresented: $showSettings) { ReaderSettingsView() }
        .sheet(isPresented: $showSource) { SourceDetail(source: session.conversation.source) }
        .preferredColorScheme(ReadingAppearance.scheme(appearance))
        .onChange(of: scenePhase) { _, phase in
            if phase == .background && session.isAnswering { session.persist() }
        }
        .onDisappear { if session.isAnswering { session.persist() } }
    }

    private var sourceHeader: some View {
        Button { showSource = true } label: {
            HStack(spacing: 12) {
                if let thumb {
                    Image(uiImage: thumb).resizable().scaledToFill().frame(width: 42, height: 40)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                } else {
                    Image(systemName: session.conversation.source.image == nil ? "doc.text" : "photo")
                        .frame(width: 42, height: 40).foregroundStyle(.blue)
                        .background(.blue.opacity(0.10), in: RoundedRectangle(cornerRadius: 9))
                }
                Text(session.conversation.source.title).font(.subheadline.weight(.semibold)).lineLimit(2)
                Spacer()
                Image(systemName: "chevron.right").font(.footnote.weight(.semibold)).foregroundStyle(.secondary)
            }
            .contentShape(Rectangle()).padding(.vertical, 9).padding(.horizontal, 16)
        }
        .buttonStyle(.plain).accessibilityHint("查看来源图片或原文")
    }

    private func turnView(_ turn: Turn) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Spacer(minLength: 34)
                Text(verbatim: turn.question).foregroundStyle(.white).textSelection(.enabled)
                    .padding(.horizontal, 14).padding(.vertical, 9)
                    .background(Color(red: 0.13, green: 0.34, blue: 0.72), in: RoundedRectangle(cornerRadius: 19))
            }
            if !turn.answer.isEmpty {
                AnswerText(text: turn.answer).equatable()
            } else if session.isAnswering && turn.id == session.conversation.turns.last?.id {
                HStack(spacing: 9) { ProgressView(); Text("等待模型回复").font(.subheadline).foregroundStyle(.secondary) }
            } else if session.requestError == nil && turn.id == session.conversation.turns.last?.id {
                Button("重新解读") { session.ask(turn.question, retry: true) }.font(.subheadline)
            }
        }
    }

    private var composer: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField("就这段内容提问", text: $question, axis: .vertical)
                .focused($editing).lineLimit(1...6).padding(.leading, 8).padding(.vertical, 9)
            if session.isAnswering {
                Button { session.stop() } label: { Image(systemName: "stop.circle.fill").font(.title) }
                    .accessibilityLabel("停止回答")
            } else {
                Button {
                    let draft = question
                    session.ask(draft)
                    if session.isAnswering { question = ""; editing = false }
                } label: { Image(systemName: "arrow.up.circle.fill").font(.title) }
                    .accessibilityLabel("发送问题")
                    .disabled(question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(6).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 22))
        .overlay(RoundedRectangle(cornerRadius: 22).strokeBorder(.secondary.opacity(0.18)))
        .padding(.horizontal, 14).padding(.vertical, 8)
    }
}

private struct ReadingBottom: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = nextValue() }
}

private struct AnswerText: View, Equatable {
    let text: String
    private enum Kind { case heading, bullet, body, code }
    private struct Block { let kind: Kind; let value: String }
    private var blocks: [Block] {
        var result: [Block] = [], pending: [String] = []
        var code = false
        func flush(_ kind: Kind) {
            if !pending.isEmpty { result.append(Block(kind: kind, value: pending.joined(separator: "\n"))); pending.removeAll() }
        }
        for line in text.components(separatedBy: .newlines) {
            let clean = line.trimmingCharacters(in: .whitespaces)
            if clean.hasPrefix("```") { flush(code ? .code : .body); code.toggle(); continue }
            if code { pending.append(line); continue }
            if clean.isEmpty { flush(.body); continue }
            let hashes = clean.prefix(while: { $0 == "#" })
            if !hashes.isEmpty && clean.dropFirst(hashes.count).first == " " {
                flush(.body); result.append(Block(kind: .heading, value: String(clean.dropFirst(hashes.count + 1))))
            } else if clean.hasPrefix("- ") || clean.hasPrefix("* ") {
                flush(.body); result.append(Block(kind: .bullet, value: String(clean.dropFirst(2))))
            } else { pending.append(line) }
        }
        flush(code ? .code : .body)
        return result
    }
    private func inline(_ string: String) -> Text {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        return (try? AttributedString(markdown: string, options: options)).map { Text($0) } ?? Text(verbatim: string)
    }
    var body: some View {
        let content = blocks
        return VStack(alignment: .leading, spacing: 8) {
            ForEach(content.indices, id: \.self) { index in
                let block = content[index]
                switch block.kind {
                case .heading: inline(block.value).font(.headline).padding(.top, 4)
                case .bullet: HStack(alignment: .firstTextBaseline, spacing: 7) { Text("•"); inline(block.value) }
                case .body: inline(block.value)
                case .code:
                    Text(verbatim: block.value).font(.system(.callout, design: .monospaced))
                        .padding(10).frame(maxWidth: .infinity, alignment: .leading)
                        .background(.secondary.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
                }
            }
        }.lineSpacing(3).textSelection(.enabled)
    }
}

private struct SourceDetail: View {
    let source: ReadingSource
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if let bytes = source.image, let image = UIImage(data: bytes) {
                        Image(uiImage: image).resizable().scaledToFit().accessibilityLabel("来源图片")
                    }
                    if !source.text.isEmpty { Text(verbatim: source.text).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading) }
                }.padding()
            }
            .navigationTitle(source.title).navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { dismiss() } } }
        }
    }
}

struct ReaderSettingsView: View {
    @Environment(\.dismiss) private var dismiss
    @AppStorage("reader.appearance", store: ReadingAppearance.defaults) private var savedAppearance = "system"
    @State private var appearance = "system"
    @State private var endpoint = ""
    @State private var model = ""
    @State private var key = ""
    @State private var profile = "default"
    @State private var fast = true
    @State private var useVision = false
    @State private var preference = ""
    @State private var loaded = false
    @State private var clearedOldKey = false
    @State private var errorText: String?

    var body: some View {
        NavigationStack {
            Form {
                if let errorText {
                    Section {
                        Label(errorText, systemImage: "exclamationmark.circle")
                        if !loaded { Button("重新读取设置", action: load) }
                    }
                }
                Section("阅读选项") {
                    Picker("读物类型", selection: $profile) {
                        Text("默认").tag("default")
                        Text("学术论文").tag("paper")
                        Text("技术文档").tag("docs")
                        Text("一般文章").tag("article")
                    }
                    Toggle("快速回答", isOn: $fast)
                    Picker("处理截图", selection: $useVision) {
                        Text("本机识别文字").tag(false)
                        Text("发送原图").tag(true)
                    }
                }
                Section {
                    TextField("例如，先概括结论，再解释关键术语", text: $preference, axis: .vertical)
                        .lineLimit(3...7)
                } header: { Text("自定义回答偏好") }
                Section("外观") {
                    Picker("主题", selection: $appearance) {
                        Text("跟随系统").tag("system")
                        Text("浅色").tag("light")
                        Text("深色").tag("dark")
                    }
                }
                Section {
                    LabeledContent("API 地址") {
                        TextField("https://…/v1", text: Binding(get: { endpoint }, set: editEndpoint)).multilineTextAlignment(.trailing)
                            .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                    LabeledContent("模型名称") {
                        TextField("服务商提供的模型 ID", text: $model).multilineTextAlignment(.trailing)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                    LabeledContent("API Key") {
                        SecureField("填写密钥", text: $key).multilineTextAlignment(.trailing)
                            .textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                    if clearedOldKey && key.isEmpty {
                        Text("服务地址已切换，已清除上一服务的密钥。").font(.footnote).foregroundStyle(.secondary)
                    }
                } header: { Text("模型 API") } footer: {
                    Text("使用兼容 Chat Completions 的接口。点击解读或发送问题时，选中的内容会发往这个地址，费用按服务商规则计算。")
                }
            }
            .navigationTitle("设置").navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) { Button("保存", action: save).disabled(!loaded) }
            }
        }
        .preferredColorScheme(ReadingAppearance.scheme(appearance))
        .onAppear { if !loaded { load() } }
    }

    private func load() {
        do {
            let settings = try ReaderConfig.load()
            endpoint = settings.endpoint; model = settings.model; key = settings.key
            profile = settings.profile; fast = settings.fast; useVision = settings.useVision
            preference = settings.preference; appearance = savedAppearance
            loaded = true; errorText = nil
        } catch {
            loaded = false; errorText = "读取设置失败。\(error.localizedDescription)"
        }
    }

    private struct Origin: Equatable {
        let scheme: String
        let host: String
        let port: Int?
        init?(_ address: String) {
            guard let url = URLComponents(string: address.trimmingCharacters(in: .whitespacesAndNewlines)),
                  let scheme = url.scheme?.lowercased(), let host = url.host?.lowercased(), !host.isEmpty else { return nil }
            self.scheme = scheme; self.host = host
            self.port = url.port ?? (scheme == "https" ? 443 : scheme == "http" ? 80 : nil)
        }
    }

    private func editEndpoint(_ value: String) {
        let old = Origin(endpoint)
        if value != endpoint, !key.isEmpty, old == nil || old != Origin(value) {
            key = ""; clearedOldKey = true
        }
        endpoint = value
    }

    private func save() {
        do {
            var settings = try ReaderConfig.load()
            settings.endpoint = endpoint.trimmingCharacters(in: .whitespacesAndNewlines)
            settings.model = model.trimmingCharacters(in: .whitespacesAndNewlines)
            settings.key = key.trimmingCharacters(in: .whitespacesAndNewlines)
            settings.profile = profile; settings.fast = fast; settings.useVision = useVision
            settings.preference = preference
            try settings.save()
            savedAppearance = appearance
            dismiss()
        } catch { errorText = "保存设置失败。\(error.localizedDescription)" }
    }
}
