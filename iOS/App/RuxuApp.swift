import SwiftUI
import PhotosUI
import UniformTypeIdentifiers
import AppIntents

@MainActor
private final class ShortcutInbox: ObservableObject {
    struct Entry: Identifiable {
        let id = UUID()
        let source: ReadingSource
    }
    static let shared = ShortcutInbox()
    @Published private(set) var pending: [Entry] = []

    func receive(_ source: ReadingSource) throws {
        guard pending.count < 5 else {
            throw ReaderImport.ImportError("如许AI 中还有 5 张待打开的截图，请先打开其中一张。")
        }
        pending.append(Entry(source: source))
    }
    func take() -> Entry? { pending.isEmpty ? nil : pending.removeFirst() }
}

struct ReadScreenshotIntent: AppIntent {
    static let title: LocalizedStringResource = "用如许AI阅读截图"
    static let description = IntentDescription("从快捷指令接收一张截图，在如许AI中打开以便解读或提问。")
    static let openAppWhenRun = true

    @Parameter(title: "截图", supportedTypeIdentifiers: ["public.image"])
    var screenshot: IntentFile

    static var parameterSummary: some ParameterSummary { Summary("用如许AI阅读\(\.$screenshot)") }

    func perform() async throws -> some IntentResult {
        var source: ReadingSource
        if let url = screenshot.fileURL {
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize
            guard let size, size > 0, size <= ReaderImport.attachmentByteLimit else {
                throw ReaderImport.ImportError("截图为空或超过 32 MB，请选择较小的图片。")
            }
            source = try await ReaderImport.image(Data(contentsOf: url, options: .mappedIfSafe))
        } else {
            source = try await ReaderImport.image(screenshot.data)
        }
        source.title = source.title.replacingOccurrences(of: "图片", with: "快捷指令截图")
        try await ShortcutInbox.shared.receive(source)
        return .result()
    }
}

@main
struct RuxuApp: App {
    @AppStorage("reader.appearance", store: ReadingAppearance.defaults) private var appearance = "system"

    var body: some Scene {
        WindowGroup {
            ReaderHome().preferredColorScheme(ReadingAppearance.scheme(appearance))
        }
    }
}

@MainActor
private final class HistoryShelf: ObservableObject {
    @Published private(set) var records: [Conversation] = []
    @Published private(set) var loaded = false
    @Published var error: String?
    private var generation = 0

    func refresh() async {
        generation += 1
        let current = generation
        do {
            let items = try await Task.detached(priority: .userInitiated) { try ReadingHistory.list() }.value
            guard current == generation else { return }
            records = items.sorted { $0.updated > $1.updated }; error = nil
            loaded = true
        } catch {
            guard current == generation else { return }
            self.error = "读取历史失败。\(error.localizedDescription)"
            loaded = true
        }
    }

    func remove(_ item: Conversation) async {
        do {
            try await Task.detached(priority: .userInitiated) { try ReadingHistory.remove(item.id) }.value
            await refresh()
        } catch { self.error = "删除失败。\(error.localizedDescription)" }
    }
}

private enum HomeRoute: Hashable { case history, reader(UUID) }
private enum OpenReading { case new(ReadingSource), saved(Conversation) }

@MainActor
private struct ReaderHome: View {
    @StateObject private var shelf = HistoryShelf()
    @ObservedObject private var inbox = ShortcutInbox.shared
    @State private var shortcutReading: ShortcutInbox.Entry?
    @State private var path: [HomeRoute] = []
    @State private var selection: OpenReading?
    @State private var photo: PhotosPickerItem?
    @State private var pickingPhoto = false
    @State private var pickingFile = false
    @State private var writing = false
    @State private var settings = false
    @State private var busy = false
    @State private var importTask: Task<Void, Never>?
    @State private var importID: UUID?
    @State private var issue: String?
    @Environment(\.scenePhase) private var scenePhase

    private var fileTypes: [UTType] {
        [.pdf, .plainText, .image] + ["md", "markdown"].compactMap { UTType(filenameExtension: $0) }
    }

    var body: some View {
        NavigationStack(path: $path) {
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("开始阅读").font(.headline)
                        ViewThatFits(in: .horizontal) {
                            HStack(spacing: 12) { photoAction; fileAction }
                            VStack(spacing: 12) { photoAction; fileAction }
                        }
                        Button { writing = true } label: {
                            HStack {
                                Image(systemName: "text.alignleft").foregroundStyle(.blue)
                                Text("输入或粘贴文字")
                                Spacer()
                                Image(systemName: "chevron.right").font(.footnote).foregroundStyle(.secondary)
                            }.padding(.horizontal, 16).padding(.vertical, 14)
                                .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16))
                        }.buttonStyle(.plain)
                    }
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            Text("最近阅读").font(.headline)
                            Spacer()
                            NavigationLink(value: HomeRoute.history) { Text("全部记录").font(.subheadline) }
                        }
                        if let error = shelf.error {
                            VStack(alignment: .leading, spacing: 6) {
                                Text(error).font(.footnote)
                                Button("重新读取") { Task { await shelf.refresh() } }
                            }
                        } else if !shelf.loaded {
                            ProgressView()
                        } else if shelf.records.isEmpty {
                            Text("解读过的截图和文章会保存在这里。")
                                .font(.subheadline).foregroundStyle(.secondary).padding(.vertical, 10)
                        } else {
                            VStack(spacing: 0) {
                                ForEach(Array(shelf.records.prefix(4)), id: \.id) { item in
                                    Button { restore(item) } label: { HistoryRow(item: item).padding(.horizontal, 14) }
                                        .buttonStyle(.plain)
                                    if item.id != shelf.records.prefix(4).last?.id { Divider().padding(.leading, 14) }
                                }
                            }.background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16))
                        }
                    }
                }.padding(18).frame(maxWidth: 720).frame(maxWidth: .infinity)
            }
            .background(Color(uiColor: .systemGroupedBackground))
            .navigationTitle("如许AI")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { settings = true } label: { Image(systemName: "gearshape") }.accessibilityLabel("设置")
                }
            }
            .navigationDestination(for: HomeRoute.self) { route in
                switch route {
                case .history: HistoryLibrary(shelf: shelf, onOpen: restore)
                case .reader:
                    switch selection {
                    case .new(let source): ReadingView(source: source)
                    case .saved(let item): ReadingView(conversation: item)
                    case nil: ContentUnavailableView("无法打开来源", systemImage: "doc.questionmark")
                    }
                }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            if !inbox.pending.isEmpty {
                Button { shortcutReading = inbox.take() } label: {
                    HStack {
                        Image(systemName: "photo.badge.arrow.down")
                        Text("快捷指令截图（\(inbox.pending.count)）")
                        Spacer()
                        Text("打开").fontWeight(.semibold)
                    }.font(.subheadline).padding(.horizontal, 16).padding(.vertical, 9)
                }
                .background(.regularMaterial)
            }
        }
        .sheet(item: $shortcutReading, onDismiss: { Task { await shelf.refresh() } }) { entry in
            NavigationStack {
                ReadingView(source: entry.source)
                    .toolbar { ToolbarItem(placement: .cancellationAction) { Button("完成") { shortcutReading = nil } } }
            }
        }
        .sheet(isPresented: $writing) { TextImportSheet { open(.new($0)) } }
        .sheet(isPresented: $settings) { ReaderSettingsView() }
        .photosPicker(isPresented: $pickingPhoto, selection: $photo, matching: .images, preferredItemEncoding: .current)
        .fileImporter(isPresented: $pickingFile, allowedContentTypes: fileTypes) { result in
            switch result {
            case .success(let url):
                importSource { try await ReaderImport.file(url) }
            case .failure(let error):
                if (error as NSError).code != NSUserCancelledError { issue = error.localizedDescription }
            }
        }
        .onChange(of: photo) { _, item in
            guard let item else { return }
            importSource {
                guard let data = try await item.loadTransferable(type: Data.self) else {
                    throw ReaderImport.ImportError("无法读取这张照片，请重新选择。")
                }
                return try await ReaderImport.image(data)
            }
        }
        .task { await shelf.refresh() }
        .onChange(of: inbox.pending.first?.id, initial: true) { _, _ in showShortcutFromHome() }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { showShortcutFromHome(); Task { await shelf.refresh() } }
        }
        .onChange(of: path) { old, new in
            if new.count < old.count {
                selection = nil
                Task { await shelf.refresh() }
            }
        }
        .overlay {
            if busy {
                ZStack {
                    Color.black.opacity(0.12).ignoresSafeArea()
                    VStack(spacing: 12) {
                        ProgressView("正在读取内容")
                        Button("取消", action: cancelImport).font(.subheadline)
                    }
                    .padding(24).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20))
                }
            }
        }
        .alert("无法打开内容", isPresented: Binding(get: { issue != nil }, set: { if !$0 { issue = nil } })) {
            Button("好") { issue = nil }
        } message: { Text(issue ?? "") }
    }

    private var photoAction: some View {
        Button { pickingPhoto = true } label: {
            ImportTile(title: "读截图", icon: "photo.on.rectangle", color: .blue)
        }.buttonStyle(.plain)
    }

    private var fileAction: some View {
        Button { pickingFile = true } label: {
            ImportTile(title: "读文档", icon: "doc.text", color: .indigo)
        }.buttonStyle(.plain)
    }

    private func open(_ target: OpenReading) {
        selection = target
        path.append(.reader(UUID()))
    }

    private func showShortcutFromHome() {
        guard scenePhase == .active, path.isEmpty, !settings, !writing, !pickingFile, !pickingPhoto, !busy,
              shortcutReading == nil, let entry = inbox.take() else { return }
        shortcutReading = entry
    }

    private func importSource(_ work: @escaping () async throws -> ReadingSource) {
        prepare { .new(try await work()) }
    }

    private func restore(_ item: Conversation) {
        let id = item.id
        prepare {
            let saved = try await Task.detached(priority: .userInitiated) { try ReadingHistory.load(id: id) }.value
            return .saved(saved)
        }
    }

    private func prepare(_ work: @escaping () async throws -> OpenReading) {
        importTask?.cancel()
        let id = UUID()
        importID = id; busy = true; issue = nil
        importTask = Task {
            do {
                let target = try await work()
                guard !Task.isCancelled, importID == id else { return }
                open(target)
            } catch {
                guard !Task.isCancelled, importID == id else { return }
                issue = error.localizedDescription
            }
            if importID == id { importID = nil; importTask = nil; busy = false; photo = nil }
        }
    }

    private func cancelImport() {
        importID = nil; importTask?.cancel(); importTask = nil; busy = false; photo = nil
    }
}

private struct ImportTile: View {
    let title: String
    let icon: String
    let color: Color
    var body: some View {
        VStack(alignment: .leading, spacing: 19) {
            Image(systemName: icon).font(.title2).foregroundStyle(color)
            Text(title).font(.title3.weight(.semibold)).fixedSize(horizontal: true, vertical: false)
        }
        .padding(16).frame(minWidth: 120, maxWidth: .infinity, alignment: .leading)
        .background(Color(uiColor: .secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 19))
    }
}

private struct TextImportSheet: View {
    let onImport: (ReadingSource) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""
    @State private var error: String?
    @FocusState private var focused: Bool
    var body: some View {
        NavigationStack {
            VStack(alignment: .leading) {
                TextEditor(text: $text).focused($focused).accessibilityLabel("要解读的原文")
                    .overlay(alignment: .topLeading) {
                        if text.isEmpty {
                            Text("输入或粘贴要解读的原文").foregroundStyle(.tertiary)
                                .padding(.horizontal, 5).padding(.top, 8).allowsHitTesting(false)
                        }
                    }
                if let error { Text(error).font(.footnote).foregroundStyle(.secondary) }
            }.padding().navigationTitle("文字阅读").navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("继续") {
                        do {
                            let title = text.split(whereSeparator: { $0.isWhitespace }).first.map { String($0.prefix(24)) } ?? "文字阅读"
                            let source = try ReaderImport.text(text, title: title)
                            dismiss(); onImport(source)
                        } catch { self.error = error.localizedDescription }
                    }.disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
            .onAppear { focused = true }
        }
    }
}

private struct HistoryRow: View {
    let item: Conversation
    var body: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: item.source.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                if let turn = item.turns.last {
                    Text(verbatim: turn.question).font(.footnote).foregroundStyle(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            Text(item.updated, format: .dateTime.month().day()).font(.caption).foregroundStyle(.secondary)
            Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary)
        }.padding(.vertical, 12).contentShape(Rectangle())
    }
}

@MainActor
private struct HistoryLibrary: View {
    @ObservedObject var shelf: HistoryShelf
    let onOpen: (Conversation) -> Void
    @State private var query = ""
    @State private var deleting: Conversation?
    private var filtered: [Conversation] {
        let terms = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !terms.isEmpty else { return shelf.records }
        return shelf.records.filter {
            $0.source.title.localizedCaseInsensitiveContains(terms) ||
            $0.source.text.localizedCaseInsensitiveContains(terms) ||
            $0.turns.contains(where: { $0.question.localizedCaseInsensitiveContains(terms) })
        }
    }
    var body: some View {
        List {
            if let error = shelf.error {
                Text(error)
                Button("重新读取") { Task { await shelf.refresh() } }
            } else if shelf.loaded && filtered.isEmpty {
                ContentUnavailableView(query.isEmpty ? "还没有阅读记录" : "没有找到相关记录", systemImage: "clock")
            } else {
                ForEach(filtered, id: \.id) { item in
                    Button { onOpen(item) } label: { HistoryRow(item: item) }.buttonStyle(.plain)
                        .swipeActions { Button("删除", role: .destructive) { deleting = item } }
                }
            }
        }
        .searchable(text: $query, prompt: "搜索标题、原文或提问")
        .navigationTitle("阅读记录")
        .refreshable { await shelf.refresh() }
        .alert("删除这段阅读记录？", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } }), presenting: deleting) { item in
            Button("删除", role: .destructive) { Task { await shelf.remove(item) } }
            Button("取消", role: .cancel) { }
        } message: { item in Text(item.source.title) }
    }
}
