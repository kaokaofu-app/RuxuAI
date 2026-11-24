using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Jiyue
{
    internal sealed class ArticleReadResult
    {
        public readonly string Text, Scope, Notice;
        internal ArticleReadResult(string text, string scope, string notice)
        {
            Text = String.IsNullOrWhiteSpace(text) ? null : text;
            Scope = scope;
            Notice = notice;
        }
    }

    internal sealed class PageReader
    {
        const int MaxChars = 300000, TimeoutMs = 25000;
        const string TooLong = "正文超过 30 万个字符，本次保留了前 30 万个字符。";
        const string TimedOut = "读取耗时较长，尚未确认已经读到文末。";
        const string NoText = "此窗口没有提供可读取的文章文字。";
        const string Changed = "来源窗口或滚动位置已变化，全文读取已停止。";
        static int busy;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        readonly CancellationToken cancellation;
        readonly IntPtr window;
        readonly Stopwatch clock = Stopwatch.StartNew();
        // A stuck out-of-process UIA provider must not delay the UI or start more reader threads.
        volatile string progress;
        AutomationElement document;
        ScrollPattern scroll;
        double originalPosition, lastPosition;
        bool repositioned, clippedSnapshot;

        PageReader(IntPtr window, CancellationToken cancellation)
        {
            this.window = window;
            this.cancellation = cancellation;
        }

        public static async Task<ArticleReadResult> ReadArticleAsync(IntPtr window, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (window == IntPtr.Zero) return new ArticleReadResult(null, null, NoText);
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return new ArticleReadResult(null, null, "上一份文档的读取还在结束，请稍后再试。");
            var reader = new PageReader(window, cancellation);
            var read = Task.Run(delegate {
                try { return reader.Read(); }
                catch (TimeoutException) { return reader.Partial(TimedOut); }
                catch (ElementNotAvailableException) { return reader.Partial(Changed); }
                catch (UnauthorizedAccessException) { return reader.Partial("当前文档不允许读取正文。"); }
                catch (COMException) { return reader.Partial("文档未能提供其余正文。"); }
                catch (ArgumentException) { return reader.Partial("当前文档无法提供完整的文字范围。"); }
                catch (InvalidOperationException ex) { return reader.Partial(ex.Message); }
                catch (NotSupportedException) { return reader.Partial("当前文档不支持继续读取。"); }
                finally {
                    try { reader.RestorePosition(); }
                    finally { Interlocked.Exchange(ref busy, 0); }
                }
            });
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                var delay = Task.Delay(TimeoutMs + 1000, deadline.Token);
                if (await Task.WhenAny(read, delay).ConfigureAwait(false) == read) {
                    deadline.Cancel();
                    return await read.ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                return reader.Partial(TimedOut);
            }
        }

        ArticleReadResult Partial(string reason)
        {
            return new ArticleReadResult(progress, "部分正文", reason);
        }

        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (clock.ElapsedMilliseconds >= TimeoutMs) throw new TimeoutException();
            if (GetForegroundWindow() != window) throw new InvalidOperationException(Changed);
        }

        ArticleReadResult Read()
        {
            Check();
            var root = AutomationElement.FromHandle(window);
            if (root == null) return Partial(NoText);
            // Start inside the document. Browser chrome, the address bar, and other windows are out of scope.
            document = root.FindFirst(TreeScope.Subtree, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            if (document == null || document.Current.IsPassword) return Partial(NoText);
            scroll = FindScroller(root);
            if (scroll == null) {
                bool range;
                progress = Snapshot(out range);
                if (String.IsNullOrEmpty(progress)) return Partial(NoText);
                if (clippedSnapshot || progress.Length > MaxChars) return Clip();
                return range ? new ArticleReadResult(progress, "全文正文", null)
                    : Partial("文档仅提供了可访问的文字节点，无法确认是否覆盖文末。");
            }

            originalPosition = lastPosition = scroll.Current.VerticalScrollPercent;
            if (originalPosition > 0.001) MoveTo(0);
            if (lastPosition > 0.01) throw new InvalidOperationException("未能定位到文章开头。");
            bool documentRange;
            string first = Snapshot(out documentRange);
            progress = first;
            if (clippedSnapshot || progress.Length > MaxChars) return Clip();

            // A native document range often already contains off-screen text. Probe the end once.
            // Virtualized documents expose a different range there and are collected in reading order below.
            if (documentRange && first.Length > 0) {
                MoveTo(100);
                if (lastPosition >= 99.999) {
                    bool stillRange;
                    string end = Snapshot(out stillRange);
                    if (stillRange && first == end && CoversViewportEnd()) {
                        return new ArticleReadResult(first, "全文正文", null);
                    }
                }
                MoveTo(0);
                if (lastPosition > 0.01) return Partial("未能回到文章开头继续读取。");
                first = Snapshot(out documentRange);
                progress = first;
            }

            bool missingScreen = first.Length == 0, uncertainSeam = false;
            while (lastPosition < 99.999) {
                Check();
                double viewSize = scroll.Current.VerticalViewSize;
                // Move by 72% of one screen so consecutive windows share text for alignment.
                double step = viewSize > 0 && viewSize < 100 ? 72 * viewSize / (100 - viewSize) : 0;
                double previous = lastPosition;
                if (step > 0) MoveTo(Math.Min(100, lastPosition + step));
                if (lastPosition <= previous + 0.00001) {
                    CheckPosition();
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    AfterMove();
                }
                if (lastPosition <= previous + 0.00001) return Partial("阅读器未能继续向下滚动，尚未确认文末。");
                bool fromRange;
                string next = Snapshot(out fromRange);
                if (next.Length == 0) missingScreen = true;
                bool overlap;
                string merged = Merge(progress, next, out overlap);
                if (!overlap) uncertainSeam = true;
                progress = merged;
                if (clippedSnapshot || progress.Length > MaxChars) return Clip();
            }
            if (String.IsNullOrEmpty(progress)) return Partial(NoText);
            if (missingScreen) return Partial("部分页面没有可读取的文字，可能包含扫描图像。");
            if (uncertainSeam) return Partial("相邻两屏的文字没有重叠，无法确认段落是否连续。");
            return new ArticleReadResult(progress, "滚动全文", null);
        }

        bool CoversViewportEnd()
        {
            object pattern;
            if (!document.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return false;
            var provider = (TextPattern)pattern;
            try {
                var visible = provider.GetVisibleRanges();
                if (visible == null || visible.Length == 0) return false;
                return visible[visible.Length - 1].CompareEndpoints(TextPatternRangeEndpoint.End,
                    provider.DocumentRange, TextPatternRangeEndpoint.End) >= 0;
            } catch (NotSupportedException) { return false; }
              catch (InvalidOperationException) { return false; }
        }

        ScrollPattern FindScroller(AutomationElement root)
        {
            object pattern;
            var current = document;
            // Some readers attach ScrollPattern to the viewport surrounding the Document.
            while (current != null) {
                Check();
                if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern) &&
                    ((ScrollPattern)pattern).Current.VerticallyScrollable)
                    return (ScrollPattern)pattern;
                if (Automation.Compare(current, root)) break;
                if (current.Current.ControlType == ControlType.Window) break;
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            // Some sites put nearly the entire article in a separate scrolling reading pane.
            // Avoid picking a narrow sidebar, menu, or editable text area as the article.
            var documentBox = document.Current.BoundingRectangle;
            if (documentBox.IsEmpty || documentBox.Width <= 0 || documentBox.Height <= 0) return null;
            var panes = document.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true),
                new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List))));
            ScrollPattern selectedPattern = null;
            AutomationElement selectedPane = null;
            double largest = documentBox.Width * documentBox.Height * 0.45;
            foreach (AutomationElement pane in panes) {
                Check();
                var box = System.Windows.Rect.Intersect(pane.Current.BoundingRectangle, documentBox);
                if (box.IsEmpty || box.Width * box.Height <= largest || !pane.TryGetCurrentPattern(ScrollPattern.Pattern, out pattern))
                    continue;
                var candidate = (ScrollPattern)pattern;
                if (!candidate.Current.VerticallyScrollable || !IsPublicPane(pane)) continue;
                largest = box.Width * box.Height;
                selectedPane = pane;
                selectedPattern = candidate;
            }
            if (selectedPane != null) document = selectedPane;
            return selectedPattern;
        }

        bool IsPublicPane(AutomationElement pane)
        {
            for (var node = pane; node != null; node = TreeWalker.ControlViewWalker.GetParent(node)) {
                var kind = node.Current.ControlType;
                if (node.Current.IsPassword || kind == ControlType.Edit || kind == ControlType.ComboBox ||
                    kind == ControlType.ToolBar || kind == ControlType.MenuBar) return false;
                if (Automation.Compare(node, document)) return true;
            }
            return false;
        }

        void CheckPosition()
        {
            Check();
            if (document.Current.IsOffscreen || Math.Abs(scroll.Current.VerticalScrollPercent - lastPosition) > 0.02)
                throw new InvalidOperationException(Changed);
        }

        void MoveTo(double percent)
        {
            CheckPosition();
            if (Math.Abs(lastPosition - percent) < 0.00001) return;
            scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
            AfterMove();
        }

        void AfterMove()
        {
            repositioned = true;
            // Let the provider realize the next viewport; record its position before honoring a cancellation.
            Thread.Sleep(85);
            lastPosition = scroll.Current.VerticalScrollPercent;
            Check();
        }

        void RestorePosition()
        {
            if (!repositioned || Math.Abs(lastPosition - originalPosition) < 0.001) return;
            try {
                // A user who has scrolled independently keeps their new position.
                if (!document.Current.IsOffscreen && Math.Abs(scroll.Current.VerticalScrollPercent - lastPosition) <= 0.02)
                    scroll.SetScrollPercent(ScrollPattern.NoScroll, originalPosition);
            } catch (ElementNotAvailableException) { }
              catch (UnauthorizedAccessException) { }
              catch (InvalidOperationException) { }
              catch (ArgumentException) { }
              catch (NotSupportedException) { }
              catch (COMException) { }
              catch (TimeoutException) { }
        }

        ArticleReadResult Clip()
        {
            string value = progress;
            int end = Math.Min(value.Length, MaxChars);
            if (end > 0 && Char.IsHighSurrogate(value[end - 1])) end--;
            progress = value.Substring(0, end);
            return Partial(TooLong);
        }

        string Snapshot(out bool documentRange)
        {
            Check();
            documentRange = false;
            clippedSnapshot = false;
            var result = new StringBuilder();
            object pattern;
            if (document.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) {
                var inputs = document.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.IsPasswordProperty, true)));
                Check();
                if (ReadRanges((TextPattern)pattern, inputs, result)) {
                    clippedSnapshot = result.Length > MaxChars;
                    string text = Clean(result);
                    if (text.Length > 0) { documentRange = true; return text; }
                }
                result.Clear();
            }
            AppendPublicNodes(result);
            clippedSnapshot = result.Length > MaxChars;
            return Clean(result);
        }

        static string Clean(StringBuilder text)
        {
            return text.ToString().Replace("\uFFFC", " ").Replace("\r\n", "\n").Replace('\r','\n').Trim();
        }

        bool ReadRanges(TextPattern text, AutomationElementCollection inputs, StringBuilder result)
        {
            try {
                var cursor = text.DocumentRange.Clone();
                var excluded = new List<TextPatternRange>();
                foreach (AutomationElement input in inputs) {
                    Check();
                    var range = text.RangeFromChild(input);
                    if (range == null || range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0)
                        return false;
                    excluded.Add(range);
                }
                excluded.Sort((a, b) => a.CompareEndpoints(TextPatternRangeEndpoint.Start, b, TextPatternRangeEndpoint.Start));
                foreach (var range in excluded) {
                    if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, cursor, TextPatternRangeEndpoint.Start) > 0) {
                        var before = cursor.Clone();
                        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                        result.Append(before.GetText(MaxChars + 1 - result.Length));
                        if (result.Length >= MaxChars) { result.Append(' '); return true; }
                        result.Append(' ');
                    }
                    if (range.CompareEndpoints(TextPatternRangeEndpoint.End, cursor, TextPatternRangeEndpoint.Start) > 0)
                        cursor.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                }
                result.Append(cursor.GetText(MaxChars + 1 - result.Length));
                return true;
            } catch (ArgumentException) { return false; }
              catch (InvalidOperationException) { return false; }
              catch (NotSupportedException) { return false; }
              catch (COMException) { return false; }
        }

        void AppendPublicNodes(StringBuilder result)
        {
            var request = new CacheRequest();
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.IsPasswordProperty);
            request.TreeScope = TreeScope.Subtree;
            request.TreeFilter = Automation.ControlViewCondition;
            var nodes = new Stack<AutomationElement>();
            var snapshot = document.GetUpdatedCache(request);
            nodes.Push(snapshot);
            while (nodes.Count > 0) {
                Check();
                var node = nodes.Pop();
                if (!Object.ReferenceEquals(node, snapshot)) {
                    var kind = node.Cached.ControlType;
                    if (node.Cached.IsPassword || kind == ControlType.Edit || kind == ControlType.ComboBox ||
                        kind == ControlType.ToolBar || kind == ControlType.MenuBar) continue;
                    if (kind == ControlType.Text) {
                        var value = node.Cached.Name;
                        if (!String.IsNullOrWhiteSpace(value)) {
                            result.Append(value, 0, Math.Min(value.Length, MaxChars + 1 - result.Length));
                            result.Append('\n');
                            if (result.Length > MaxChars) return;
                            continue;
                        }
                    }
                }
                var children = node.CachedChildren;
                if (children != null)
                    for (int i = children.Count - 1; i >= 0; --i) nodes.Push(children[i]);
            }
        }

        internal static string Merge(string prior, string next, out bool continuous)
        {
            continuous = true;
            if (String.IsNullOrEmpty(next) || prior == next) return prior;
            if (String.IsNullOrEmpty(prior) || next.StartsWith(prior, StringComparison.Ordinal)) return next;
            // Longest suffix of the text already read that equals a prefix of the new snapshot.
            // Repeated paragraphs elsewhere in the article are deliberately retained.
            int length = Math.Min(prior.Length, next.Length);
            var prefix = new int[length];
            for (int i = 1, n = 0; i < length; i++) {
                while (n > 0 && next[n] != next[i]) n = prefix[n-1];
                if (next[n] == next[i]) n++;
                prefix[i] = n;
            }
            int overlap = 0;
            for (int i = prior.Length - length; i < prior.Length; i++) {
                while (overlap > 0 && next[overlap] != prior[i]) overlap = prefix[overlap-1];
                if (next[overlap] == prior[i]) overlap++;
            }
            if (overlap == next.Length) return prior;
            if (overlap < 4 && !(overlap > 0 && next[overlap] == '\n')) overlap = 0;
            continuous = overlap > 0;
            return overlap == 0 ? prior + "\n\n" + next : prior + next.Substring(overlap);
        }
    }
}
