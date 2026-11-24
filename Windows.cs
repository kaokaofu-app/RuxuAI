using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Jiyue
{
    internal static class Style
    {
        public static Color Ink {get {return Theme.Ink(AppAppearance.IsDark);}}
        public static Color Muted {get {return Theme.Muted(AppAppearance.IsDark);}}
        public static Color Accent {get {return Theme.Accent(AppAppearance.IsDark);}}
        public static Color Tint {get {return Theme.SurfaceHover(AppAppearance.IsDark);}}
        internal const string ChineseFace="Source Han Sans CN", LatinFace="Source Sans 3";
        internal static readonly string FontRoot=System.IO.Path.GetDirectoryName(typeof(Style).Assembly.Location);
        static readonly System.Drawing.Text.PrivateFontCollection faces=new System.Drawing.Text.PrivateFontCollection();
        static readonly FontFamily chinese;
        [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]
        static extern int AddFontResourceEx(string path,uint flags,IntPtr reserved);
        static Style()
        {
            foreach(var name in new[]{"SourceHanSansCN-Regular.otf","SourceHanSansCN-Bold.otf","SourceSans3-Regular.ttf","SourceSans3-Bold.ttf"}) {
                string path=System.IO.Path.Combine(FontRoot,"Fonts",name);
                if(!System.IO.File.Exists(path) || AddFontResourceEx(path,0x10,IntPtr.Zero)==0) continue;
                try {faces.AddFontFile(path);} catch(ArgumentException) { }
            }
            foreach(var family in faces.Families) if(family.Name==ChineseFace || family.GetName(1033)==ChineseFace) chinese=family;
        }
        public static Font Font(float size, bool bold=false)
        {
            var weight=bold ? FontStyle.Bold : FontStyle.Regular;
            if(chinese!=null && chinese.IsStyleAvailable(weight)) return new Font(chinese,size,weight,GraphicsUnit.Point);
            return new Font("Microsoft YaHei UI",size,weight,GraphicsUnit.Point);
        }
        public static void Form(Form f, string title,int width,int height)
        {
            // Apply the design-to-device scale once, after all child controls have been added.
            f.SuspendLayout();
            f.Font=Font(10); f.AutoScaleMode=AutoScaleMode.Dpi; f.AutoScaleDimensions=new SizeF(96,96);
            f.Text=title; f.BackColor=Theme.Background(AppAppearance.IsDark); f.ForeColor=Ink; f.ClientSize=new Size(width,height);
            f.StartPosition=FormStartPosition.CenterScreen; f.Icon=System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        public static Label Label(string text,int x,int y,int w,int h,float size,bool bold=false)
        {
            return new Label { Text=text,Location=new Point(x,y),Size=new Size(w,h),Font=Font(size,bold),ForeColor=bold?Ink:Muted,TextAlign=ContentAlignment.MiddleLeft };
        }
        public static Button Button(string text,int x,int y,int w,int h,bool primary=false,ButtonIcon icon=ButtonIcon.None)
        {
            return new GlassButton(primary,icon) {Text=text,Location=new Point(x,y),Size=new Size(w,h),
                BackColor=primary?Accent:Tint,ForeColor=primary?Theme.OnAccent:Ink};
        }
    }
    internal sealed class QuestionBox : TextBox
    {
        public string HintText = "就这段内容继续提问…";
        public bool ShowHintOnFocus;
        public event EventHandler SubmitRequested;

        public QuestionBox()
        {
            Multiline = true;
            AcceptsReturn = true;
            WordWrap = true;
            AutoSize = false;
            BorderStyle = BorderStyle.None;
            AccessibleName = "继续提问";
            AccessibleDescription = "Enter 发送，Shift+Enter 换行";
        }

        [DllImport("user32.dll", CharSet=CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        public int DisplayLineCount
        {
            get
            {
                const int getLineCount = 0x00BA; // EM_GETLINECOUNT counts soft wraps as well as newlines.
                return IsHandleCreated ? Math.Max(1, SendMessage(Handle,getLineCount,IntPtr.Zero,IntPtr.Zero).ToInt32()) : 1;
            }
        }

        void DrawHint(IntPtr destination)
        {
            if (!Enabled || (Focused && !ShowHintOnFocus) || composing || TextLength != 0) return;
            using (var canvas = destination == IntPtr.Zero ? CreateGraphics() : Graphics.FromHdc(destination))
            {
                var bounds = ClientRectangle;
                bounds.Inflate(-(int)Math.Ceiling(canvas.DpiX / 96f), 0);
                var flags = TextFormatFlags.SingleLine | TextFormatFlags.Top |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                Color hint = Theme.Muted(AppAppearance.IsDark);
                TextRenderer.DrawText(canvas, HintText, Font, bounds,
                    SystemInformation.HighContrast ? SystemColors.GrayText : hint, flags);
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.KeyData != Keys.Enter || composing) return;
            e.SuppressKeyPress = true;
            var submit = SubmitRequested;
            if (submit != null) submit(this, EventArgs.Empty);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GuiThreadInfo
        {
            public uint Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
        [DllImport("user32.dll")] static extern bool GetCaretPos(out Point point);
        [DllImport("user32.dll")] static extern bool CreateCaret(IntPtr owner, IntPtr bitmap, int width, int height);
        [DllImport("user32.dll")] static extern bool SetCaretPos(int x, int y);
        [DllImport("user32.dll")] static extern bool ShowCaret(IntPtr owner);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr owner);

        bool composing;
        int lastWidth, wheelRemainder;

        internal static int ReadingCaretWidth(int dpi,int systemWidth)
        {
            return Math.Max(systemWidth,(3 * dpi + 48) / 96);
        }

        void UpdateCaret()
        {
            if (composing || !Focused || ReadOnly || SelectionLength != 0) return;
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(0,ref info) || info.Caret != Handle || info.Focus != Handle ||
                (info.Flags & 1) == 0 || info.Bottom <= info.Top) return;
            int dpi;
            try { dpi = (int)GetDpiForWindow(Handle); }
            catch (EntryPointNotFoundException) { using (var g = CreateGraphics()) dpi = (int)g.DpiX; }
            int width = ReadingCaretWidth(dpi > 0 ? dpi : 96,SystemInformation.CaretWidth);
            int current = info.Right - info.Left;
            if (current == width || (current > width && current != lastWidth)) return;
            Point position;
            if (!GetCaretPos(out position) || !CreateCaret(Handle,IntPtr.Zero,width,info.Bottom-info.Top)) return;
            SetCaretPos(position.X,position.Y);
            ShowCaret(Handle);
            lastWidth = width;
        }

        protected override void WndProc(ref Message message)
        {
            const int SetFocus=0x0007, KillFocus=0x0008, SettingChange=0x001a, SetFont=0x0030;
            const int Paint=0x000f, PrintClient=0x0318, Enable=0x000a;
            const int ImeStart=0x010d, ImeEnd=0x010e, KeyUp=0x0101, LeftButtonUp=0x0202, DpiAfterParent=0x02e3;
            if (message.Msg==0x020A && (message.WParam.ToInt64() & 0x000C)==0 && DisplayLineCount>1)
            {
                // The borderless EDIT has no scrollbar; keep a long draft scrollable one line per notch.
                if (SystemInformation.MouseWheelScrollLines!=0)
                {
                    int delta=unchecked((short)(message.WParam.ToInt64()>>16)) + wheelRemainder;
                    wheelRemainder=delta % 120;
                    if (delta/120!=0) SendMessage(Handle,0x00B6,IntPtr.Zero,new IntPtr(-delta/120)); // EM_LINESCROLL
                }
                message.Result=IntPtr.Zero;
                return;
            }
            if (message.Msg == ImeStart) composing=true;
            base.WndProc(ref message);
            if (message.Msg == KillFocus) { composing=false; lastWidth=0; }
            if (message.Msg == ImeEnd) composing=false;
            if (message.Msg == Paint || message.Msg == PrintClient) DrawHint(message.WParam);
            if (message.Msg == SetFocus || message.Msg == KillFocus || message.Msg == Enable || message.Msg == SettingChange)
                Invalidate();
            // Let EDIT position its own caret first and leave composition/selection to the IME.
            switch (message.Msg)
            {
                case SetFocus: case ImeEnd: case SettingChange: case SetFont:
                case KeyUp: case LeftButtonUp: case DpiAfterParent: UpdateCaret(); break;
            }
        }
    }
    internal sealed partial class Dialog : AppearanceForm
    {
        readonly Func<ModelSettings> settings; readonly Action<Action> configure;
        readonly Label state;
        readonly ReplyView output;
        readonly SourceCard sourceCard;
        readonly Panel paper;
        readonly JumpToLatestButton latest;
        readonly ReplyWaitingDots waitingDots;
        readonly QuestionComposer composer;
        readonly QuestionBox input;
        readonly SendQuestionButton send;
        bool themeInitialized, darkReading, highContrastReading;
        readonly List<KeyValuePair<string,string>> history=new List<KeyValuePair<string,string>>();
        string recognizedText;
        bool articleCapture, awaitingPartialApproval;
        string articleNotice;
        int articleSection, sectionHistoryStart;
        List<string> articleSections;
        CancellationTokenSource active;
        public bool IsGenerating { get { return active!=null; } }
        string scope;
        string sourceTitle;
        DateTime capturedUtc;
        byte[] pagePreview;
        DateTime updatedUtc;
        Action drainPending;
        bool closing;
        public string ConversationId { get; private set; }
        public event Action ConversationChanged;
        public event Action AnswerPreferenceRequested;
        string context, retry; byte[] image; bool attached, questionFirst, stopRequested;
        int pendingStart=-1;
        public Dialog(string label,Func<ModelSettings> getSettings,Action<Action> edit)
        {
            settings=getSettings; configure=edit;
            ConversationId=Guid.NewGuid().ToString("N");
            Style.Form(this,"如许AI · 阅读对话",600,620);
            FormBorderStyle=FormBorderStyle.Sizable; MinimumSize=new Size(370,290); MaximizeBox=true; MinimizeBox=true;
            ShowInTaskbar=true; StartPosition=FormStartPosition.Manual;
            var layout=new TableLayoutPanel {
                Dock=DockStyle.Fill,Padding=new Padding(16,8,16,12),ColumnCount=1,RowCount=5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            state=new Label {
                Text="",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,
                Font=Style.Font(9),AutoEllipsis=true,Margin=Padding.Empty
            };
            var header=MakeHeader(label);
            paper=new Panel {Dock=DockStyle.Fill,Padding=new Padding(20,16,16,16),Margin=Padding.Empty};
            output=new ReplyView {
                Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Color.White,
                ForeColor=Style.Ink,Font=Style.Font(12),DetectUrls=false,HideSelection=false,
                AccessibleName="模型回答",ScrollBars=RichTextBoxScrollBars.Vertical
            };
            output.HandleCreated+=delegate { ApplyScrollTheme(); };
            paper.Controls.Add(output);
            InstallStateCard();
            waitingDots=new ReplyWaitingDots(output);
            waitingDots.SettingsRequested+=delegate {if(ReadingOptionsRequested!=null) ReadingOptionsRequested();};
            paper.Controls.Add(waitingDots);
            waitingDots.BringToFront();
            latest=new JumpToLatestButton { Visible=false };
            paper.Controls.Add(latest);
            latest.BringToFront();
            paper.Layout+=delegate {
                latest.Location=new Point((paper.ClientSize.Width-latest.Width)/2,
                    Math.Max(0,paper.ClientSize.Height-paper.Padding.Bottom-latest.Height));
            };
            output.ViewportChanged+=delegate {
                latest.Visible=output.CurrentlyAboveLatest;
                if (latest.Visible) latest.BringToFront();
            };
            latest.Click+=delegate { output.Focus(); output.JumpToLatest(); };
            sourceCard=new SourceCard {Dock=DockStyle.Fill,Visible=false,Margin=new Padding(0,0,0,8)};
            sourceCard.MoveSection+=MoveArticleSection;
            sourceCard.ExpandedChanged+=MarkChanged;
            input=new QuestionBox {
                Font=Style.Font(12),Enabled=false
            };
            send=new SendQuestionButton {Enabled=false};
            input.TextChanged+=delegate { RefreshSendButton(); };
            input.EnabledChanged+=delegate { RefreshSendButton(); };
            input.SubmitRequested+=delegate { if(active==null && send.Enabled) Ask(); };
            send.Click+=delegate { if(active!=null) StopAnswer(); else Ask(); };
            composer=new QuestionComposer(input,send) {
                Dock=DockStyle.Fill,Margin=new Padding(0,12,0,0)
            };
            layout.Controls.Add(header,0,0); layout.Controls.Add(sourceCard,0,1);
            layout.Controls.Add(paper,0,2); layout.Controls.Add(MakeChips(),0,3); layout.Controls.Add(composer,0,4);
            Controls.Add(layout);
            InstallMenus();
            ReaderPreferences.FontSizeChanged+=ApplyFontSize;
            output.HandleCreated+=delegate { ApplyFontSize(); };
            if(output.IsHandleCreated) ApplyFontSize();
            Disposed+=delegate { ReaderPreferences.FontSizeChanged-=ApplyFontSize; };
            RefreshTools();
            FormClosing+=delegate {
                closing=true;
                waitingDots.Stop();
                if(active!=null) active.Cancel();
                if(drainPending!=null) drainPending();
            };
            AppAppearance.Bind(this,ApplyReadingTheme);
            ResumeLayout(true);
        }

        void ApplyScrollTheme()
        {
            if (themeInitialized) AppAppearance.ScrollColors(output,darkReading);
        }
        internal void ApplyReadingTheme(bool dark)
        {
            bool contrast=SystemInformation.HighContrast;
            if (themeInitialized && darkReading==dark && highContrastReading==contrast) return;
            themeInitialized=true;
            darkReading=dark;
            highContrastReading=contrast;
            BackColor=AppAppearance.Background(dark);
            ForeColor=AppAppearance.Ink(dark);
            state.ForeColor=Theme.Muted(dark);
            ApplyToolsTheme(dark);
            output.ApplyTheme(dark);
            ApplyScrollTheme();
            paper.BackColor=output.BackColor;
            waitingDots.BackColor=output.BackColor;
            waitingDots.ForeColor=AppAppearance.Muted(dark);
            latest.ApplyTheme(dark);
            composer.ApplyTheme(dark);
            sourceCard.ApplyTheme(dark);
        }
        // Keep the last user-resized normal window in 96-DPI units for this process.
        static Size preferredWindowSize=ReadWindowSize();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(Point point, uint flags);
        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

        static int ScreenDpi(Screen screen)
        {
            try
            {
                uint x, y;
                IntPtr monitor=MonitorFromPoint(screen.Bounds.Location, 2);
                if (GetDpiForMonitor(monitor, 0, out x, out y)==0 && x>0) return (int)x;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return 96;
        }

        static Size ScaleSize(Size size, int fromDpi, int toDpi)
        {
            return new Size((int)Math.Round((double)size.Width*toDpi/fromDpi),
                            (int)Math.Round((double)size.Height*toDpi/fromDpi));
        }

        static long Overlap(Rectangle a, Rectangle b)
        {
            var common=Rectangle.Intersect(a,b);
            return (long)common.Width*common.Height;
        }

        static Rectangle PlaceWithin(Rectangle selection, Rectangle area, Size requested, int gap)
        {
            var size=new Size(Math.Min(requested.Width,area.Width),Math.Min(requested.Height,area.Height));
            int maxX=area.Right-size.Width, maxY=area.Bottom-size.Height;
            var positions=new[] {
                new Point(selection.Right+gap,selection.Top), new Point(selection.Left-size.Width-gap,selection.Top),
                new Point(selection.Left,selection.Bottom+gap), new Point(selection.Left,selection.Top-size.Height-gap),
                new Point(maxX,area.Top), new Point(area.Left,area.Top), new Point(maxX,maxY), new Point(area.Left,maxY)
            };
            Rectangle best=Rectangle.Empty;
            long least=long.MaxValue;
            foreach (var p in positions)
            {
                var location=new Point(Math.Max(area.Left,Math.Min(maxX,p.X)),Math.Max(area.Top,Math.Min(maxY,p.Y)));
                var candidate=new Rectangle(location,size);
                long overlap=Overlap(candidate,selection);
                if (overlap<least) { best=candidate; least=overlap; }
                if (least==0) break;
            }
            return best;
        }

        static Rectangle PositionOn(Screen screen, Rectangle selection, Size logicalSize)
        {
            int dpi=ScreenDpi(screen);
            return PlaceWithin(selection,screen.WorkingArea,ScaleSize(logicalSize,96,dpi),
                               (int)Math.Round(10.0*dpi/96));
        }

        public void ShowAt(Rectangle bounds, Point? releasePoint=null, Point? openingPoint=null)
        {
            var source=releasePoint.HasValue ? Screen.FromPoint(releasePoint.Value) : Screen.FromRectangle(bounds);
            if(releasePoint.HasValue) OpenFrom(bounds,releasePoint.Value); else OpenFrom(openingPoint ?? Cursor.Position);
            Location=source.WorkingArea.Location;
            // Load runs after WinForms has scaled the controls and before the window becomes visible.
            Load+=delegate {
                int dpi=ScreenDpi(source);
                var logicalSize=preferredWindowSize.IsEmpty ? ScaleSize(Size,dpi,96) : preferredWindowSize;
                Rectangle position;
                if (releasePoint.HasValue)
                {
                    position=CapturePopup.Place(bounds,releasePoint.Value,source.WorkingArea,
                        ScaleSize(logicalSize,96,dpi));
                }
                else
                {
                    position=PositionOn(source,bounds,logicalSize);
                    long least=Overlap(position,bounds);
                    if (least>0) foreach (var screen in Screen.AllScreens)
                    {
                        if (screen.DeviceName==source.DeviceName) continue;
                        var candidate=PositionOn(screen,bounds,logicalSize);
                        long overlap=Overlap(candidate,bounds);
                        if (overlap<least) { position=candidate; least=overlap; }
                        if (least==0) break;
                    }
                }
                // Moving to another monitor may rescale a form. Set its final size after the move.
                position=CapturePopup.Stagger(position,this);
                Location=position.Location;
                MinimumSize=new Size(Math.Min(MinimumSize.Width,position.Width),Math.Min(MinimumSize.Height,position.Height));
                Bounds=position;
                Size last=ScaleSize(Size,ScreenDpi(Screen.FromRectangle(Bounds)),96);
                Action remember=delegate {
                    var normal=WindowState==FormWindowState.Normal ? Bounds : RestoreBounds;
                    if (normal.Width<=0 || normal.Height<=0) return;
                    var logical=ScaleSize(normal.Size,ScreenDpi(Screen.FromRectangle(normal)),96);
                    // An unchanged or temporarily screen-limited size should not replace the user's preference.
                    if (Math.Abs(logical.Width-last.Width)<=1 && Math.Abs(logical.Height-last.Height)<=1) return;
                    preferredWindowSize=logical;
                    SaveWindowSize(logical);
                    last=logical;
                };
                ResizeEnd+=delegate { if (WindowState==FormWindowState.Normal) remember(); };
                FormClosing+=delegate { remember(); };
            };
            Show();
        }
        public void SetCaptureSource(string captureScope,string title,DateTime timeUtc,byte[] visiblePage)
        {
            scope=captureScope; sourceTitle=title; capturedUtc=timeUtc; pagePreview=visiblePage;
            UpdateSourceCard();
        }
        void UpdateSourceCard()
        {
            sourceCard.SetSource(scope,sourceTitle,capturedUtc,image ?? pagePreview,context ?? recognizedText);
            if (articleCapture) UpdateArticleControls();
            sourceCard.Visible=true;
        }
        bool LongArticle { get {return articleCapture && (scope ?? "").Contains("长截图");} }
        void UpdateArticleControls()
        {
            sourceCard.SetArticle(context ?? recognizedText, articleSection, articleSections==null ? 0 : articleSections.Count,
                active==null && !awaitingPartialApproval && settings().Ready, articleNotice);
        }
        void BuildArticleSections()
        {
            string full=context ?? recognizedText;
            articleSections=String.IsNullOrWhiteSpace(full) ? null : ArticleBudget.Sections(full);
            articleSection=articleSections==null || articleSections.Count<2 ? 0 : Math.Min(Math.Max(0,articleSection),articleSections.Count);
        }
        void MoveArticleSection(int direction)
        {
            if (!articleCapture || active!=null || awaitingPartialApproval || articleSections==null || !settings().Ready) return;
            int target=articleSection+direction;
            if (target<0 || target>articleSections.Count || articleSections.Count<=1) return;
            articleSection=target;
            sectionHistoryStart=history.Count;
            // A new section starts a separate question, even after a failed earlier answer.
            pendingStart=-1;
            StartAnswer(target==0 ? "回到文章速览。" : "请解读第 "+target+" 段。");
        }
        public void AttachArticle(string scope,string fullText,byte[] longPng,string notice)
        {
            articleCapture=true;
            articleNotice=notice;
            awaitingPartialApproval=!String.IsNullOrWhiteSpace(notice);
            Attach(scope,fullText,longPng);
        }
        public void Attach(string scope,string text,byte[] png)
        {
            this.scope=scope; context=text; image=png; attached=true; state.Text=scope; Text="如许AI · "+scope;
            headerTitle.Text=scope;
            if(capturedUtc==default(DateTime)) capturedUtc=DateTime.UtcNow;
            if (articleCapture) BuildArticleSections();
            UpdateSourceCard();
            if (awaitingPartialApproval) { ShowPartialApproval(); MarkChanged(); return; }
            if (!settings().Ready)
            {
                pendingStart=questionFirst?-1:0;
                state.Text="等待连接";
                ShowCard(questionFirst?"内容已就绪，连接模型后即可提问。":"内容已就绪，连接模型后会自动开始解释。",
                    "连接模型 API",delegate {configure(BeginAttached);});
                MarkChanged(); return;
            }
            BeginAttached();
        }
        public void AttachQuestionFirst(string scope,string text,byte[] png)
        {
            questionFirst=true;
            Attach(scope,text,png);
        }
        void BeginAttached()
        {
            if(IsDisposed || !settings().Ready) return;
            if(!questionFirst) {StartAnswer(null);return;}
            card.Visible=false;state.Text="等待提问";input.Enabled=true;
            input.HintText="想问这段内容什么？"; input.ShowHintOnFocus=true;input.Invalidate();input.Focus();
            RefreshTools();MarkChanged();
        }
        void ShowPartialApproval()
        {
            state.Text="已采集部分内容"; input.Enabled=false;
            ShowCard(articleNotice,"解读已采集部分",delegate {
                Action proceed=delegate { awaitingPartialApproval=false; StartAnswer(null); };
                if (settings().Ready) proceed(); else configure(proceed);
            });
            RefreshTools();
        }

        void MarkChanged()
        {
            updatedUtc=DateTime.UtcNow;
            if(ConversationChanged!=null) ConversationChanged();
        }

        string ConversationTitle()
        {
            string text=context;
            if(String.IsNullOrWhiteSpace(text)) text=recognizedText;
            if(String.IsNullOrWhiteSpace(text) && history.Count>=2) text=history[1].Value;
            if(String.IsNullOrWhiteSpace(text)) return scope;
            string preview=text.Substring(0,Math.Min(140,text.Length)).TrimStart(' ','\r','\n','\t','#','*','`','>');
            preview=System.Text.RegularExpressions.Regex.Replace(preview,@"\s+"," ").Trim();
            if(preview.Length==0) return scope;
            var characters=new System.Globalization.StringInfo(preview);
            return characters.LengthInTextElements<=26 ? preview : characters.SubstringByTextElements(0,26)+"…";
        }

        public ConversationRecord CaptureRecord()
        {
            if(!attached || IsDisposed || output.IsDisposed) return null;
            var turns=new List<string[]>(history.Count);
            foreach(var turn in history) turns.Add(new[]{turn.Key,turn.Value});
            return new ConversationRecord {
                Id=ConversationId, Title=ConversationTitle(), Scope=scope, UpdatedUtc=updatedUtc,SourceExpanded=sourceCard.Expanded,
                SourceTitle=sourceTitle, CapturedUtc=capturedUtc,
                Context=context, Image=image ?? pagePreview, ImageIsPagePreview=image==null && pagePreview!=null,
                RecognizedText=recognizedText, History=turns,
                ArticleCapture=articleCapture,ArticleSection=articleSection,SectionHistoryStart=sectionHistoryStart,
                ArticleNotice=articleNotice,AwaitingPartialApproval=awaitingPartialApproval,
                TranscriptRtf=output.Rtf, RetryQuestion=retry, PendingStart=pendingStart,
                CanRetry=pendingStart>=0
            };
        }

        public void Restore(ConversationRecord record)
        {
            ConversationId=record.Id; updatedUtc=record.UpdatedUtc;
            sourceCard.SetExpanded(record.SourceExpanded);
            scope=record.Scope; context=record.Context;
            sourceTitle=record.SourceTitle; capturedUtc=record.CapturedUtc;
            // Page previews are local history. Only an explicitly selected region is a model image.
            pagePreview=record.ImageIsPagePreview ? record.Image : null;
            image=record.ImageIsPagePreview ? null : record.Image;
            recognizedText=record.RecognizedText; retry=record.RetryQuestion; attached=true;
            articleCapture=record.ArticleCapture; articleNotice=record.ArticleNotice;
            awaitingPartialApproval=record.AwaitingPartialApproval; articleSection=record.ArticleSection;
            if (articleCapture) BuildArticleSections();
            Text="如许AI · "+scope;
            headerTitle.Text=scope;
            UpdateSourceCard();
            history.Clear();
            if(record.History!=null)
                foreach(var pair in record.History)
                    if(pair!=null && pair.Length==2 && (pair[0]=="user" || pair[0]=="assistant") && pair[1]!=null)
                        history.Add(new KeyValuePair<string,string>(pair[0],pair[1]));
            sectionHistoryStart=Math.Max(0,Math.Min(history.Count,record.SectionHistoryStart));
            bool formatted=false;
            if(!String.IsNullOrEmpty(record.TranscriptRtf)) {
                try { output.RestoreTranscript(record.TranscriptRtf); formatted=true; }
                catch(ArgumentException) { }
            }
            if(formatted) {
                pendingStart=record.CanRetry ? Math.Max(0,Math.Min(record.PendingStart,output.TextLength)) : -1;
                output.RestoreCodeButtons(history);
            }
            else {
                output.Clear();
                for(int i=0;i<history.Count;i++) {
                    var turn=history[i];
                    if(turn.Key=="assistant") output.RenderAnswer(output.TextLength,turn.Value);
                    else if(i>0 || turn.Value!=DefaultQuestion()) output.AppendQuestion(turn.Value);
                }
                pendingStart=record.CanRetry ? output.TextLength : -1;
                if(record.CanRetry && retry!=null) output.AppendQuestion(retry);
            }
            questionFirst=!articleCapture && history.Count==0 && !record.CanRetry;
            ShowRestoredActions();
        }

        void ShowRestoredActions()
        {
            if(IsDisposed) return;
            if (articleCapture) UpdateArticleControls();
            if (articleCapture && String.IsNullOrWhiteSpace(context ?? recognizedText) && image==null) {
                state.Text="来源内容未能恢复"; input.Enabled=false;
                ShowCard("请重新采集原文，再开始阅读。"); RefreshTools();return;
            }
            if (awaitingPartialApproval) { ShowPartialApproval(); return; }
            bool ready=settings().Ready;
            input.Enabled=ready;
            if(!ready) {
                state.Text="等待连接";
                ShowCard("连接模型即可继续阅读。","连接模型 API",delegate {configure(ShowRestoredActions);});
            } else if(pendingStart>=0) {
                state.Text="上次回答未完成";
                ShowCard("上次回答未完成。","重新生成",delegate {StartAnswer(retry);});
            } else {
                state.Text=questionFirst?"等待提问":"可以继续提问"; card.Visible=false;
                if(questionFirst) {input.HintText="想问这段内容什么？";input.ShowHintOnFocus=true;}
            }
            RefreshTools();
        }
        public void OfferScrollCapture(string message,Action select)
        {
            state.Text="没有读到文章正文"; ShowCard(message,"改用滚轮长截图",select);
        }
        public void ShowFailure(string message) {state.Text="读取失败";ShowCard(message,error:true);}
        void Ask()
        {
            string q=input.Text.Trim(); if(q.Length==0 || active!=null || !attached) return;
            input.Clear(); StartAnswer(q);
        }
        void RefreshSendButton()
        {
            if(send==null) return;
            send.Generating=active!=null;
            send.Enabled=active!=null || (input.Enabled && !String.IsNullOrWhiteSpace(input.Text));
        }
        async void StartAnswer(string question)
        {
            if(active!=null || IsDisposed || closing || awaitingPartialApproval || !settings().Ready) return;
            retry=question;
            stopRequested=false;
            var cts=new CancellationTokenSource(); active=cts;
            if (articleCapture) UpdateArticleControls();
            bool success=false; string failure=null;
            card.Visible=false;
            // Keep completed turns. A retry replaces the previous partial answer and its error.
            if(question==null) output.Clear();
            else if(pendingStart>=0) output.RemoveTail(pendingStart);
            pendingStart=output.TextLength;
            if(question!=null) output.AppendQuestion(question);
            int replyStart=output.TextLength;
            state.Text="正在等待模型…"; input.Enabled=true;
            input.HintText="就这段内容继续提问…";input.ShowHintOnFocus=false;input.Invalidate(); RefreshTools();
            waitingDots.Start(replyStart);
            var buffered=new System.Text.StringBuilder(); object gate=new object();
            var markdown=new System.Text.StringBuilder();
            using(var timer=new System.Windows.Forms.Timer {Interval=50})
            {
                Action flush=delegate {
                    lock(gate) {
                        if(buffered.Length==0 || IsDisposed) return;
                        markdown.Append(buffered.ToString()); buffered.Clear();
                    }
                    // A delimiter can arrive in a later chunk. Reformat just this answer.
                    var elapsed=System.Diagnostics.Stopwatch.StartNew();
                    output.RenderAnswer(replyStart,markdown.ToString());
                    if (waitingDots.IsWaiting && output.TextLength>replyStart) {
                        string transcript=output.Text;
                        for (int i=replyStart;i<transcript.Length;i++) {
                            if (!Char.IsWhiteSpace(transcript[i])) { waitingDots.Stop(); break; }
                        }
                    }
                    timer.Interval=(int)Math.Max(50,Math.Min(500,elapsed.ElapsedMilliseconds*5));
                    if (!waitingDots.IsWaiting && !stopRequested) state.Text="正在解释…";
                };
                drainPending=flush;
                timer.Tick+=delegate {flush(); waitingDots.Advance();pulse.Invalidate();}; timer.Start();
                try {
                    MarkChanged();
                    string prompt=question ?? DefaultQuestion();
                    var modelSettings=settings();
                    string material=context ?? recognizedText;
                    byte[] picture=image;
                    if (image!=null && (modelSettings.ReadRegionAsText || (LongArticle && articleSection>0)))
                    {
                        if (recognizedText==null)
                        {
                            state.Text="正在本机识别文字…";
                            string extracted=LongArticle ? await LongArticleImage.ReadTextAsync(image,cts.Token) : await LocalOcr.ReadAsync(image);
                            cts.Token.ThrowIfCancellationRequested();
                            recognizedText=extracted;
                            if (articleCapture) { BuildArticleSections(); UpdateArticleControls(); }
                        }
                        cts.Token.ThrowIfCancellationRequested();
                        material=recognizedText;
                        picture=null;
                        state.Text="正在等待模型…";
                    }
                    if (articleCapture && articleSections!=null && articleSections.Count>1 && picture==null)
                        material=articleSection==0 ? ArticleBudget.Overview(material) : articleSections[articleSection-1];
                    var previousTurns=sectionHistoryStart>0 ? history.GetRange(sectionHistoryStart,history.Count-sectionHistoryStart) : history;
                    string answer=await ModelClient.ReplyAsync(modelSettings,material,picture,previousTurns,prompt,delegate(string s) {lock(gate) buffered.Append(s);},cts.Token,LongArticle && picture!=null);
                    if(IsDisposed || closing) return;
                    cts.Token.ThrowIfCancellationRequested();
                    flush();
                    history.Add(new KeyValuePair<string,string>("user",prompt));
                    history.Add(new KeyValuePair<string,string>("assistant",answer));
                    pendingStart=-1; success=true; state.Text="可以继续提问";
                } catch(Exception ex) {
                    if (IsDisposed || closing) return; flush();
                    state.Text=stopRequested?"已停止":"请求未完成";
                    failure=ex.Message;
                } finally { timer.Stop(); waitingDots.Stop(); drainPending=null; active=null; cts.Dispose(); }
            }
            if (IsDisposed || closing) return;
            if (articleCapture) UpdateArticleControls();
            if(!success && stopRequested) ShowCard("已停止","重新生成",delegate {StartAnswer(retry);});
            else if(!success) ShowCard(failure,"重试",delegate {StartAnswer(retry);},
                "检查模型设置",delegate {configure(null);},true);
            input.Enabled=true;
            RefreshTools();
            MarkChanged();
            if (ContainsFocus && WindowState!=FormWindowState.Minimized && !output.ContainsFocus) input.Focus();
        }
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {
            foreach(Control child in Controls) if(child is SettingsDialog && child.Visible) {
                if(keyData==Keys.Escape) {((SettingsDialog)child).Close();return true;}
                return base.ProcessCmdKey(ref msg,keyData);
            }
            if (keyData==(Keys.Control|Keys.V) && ImageImport.TryPaste(this)) return true;
            if (keyData==Keys.Escape) {if(active!=null) StopAnswer(); else if(input.TextLength>0) input.Clear(); else Close(); return true;}
            if (keyData==(Keys.Control|Keys.W)) {Close();return true;}
            if (keyData==(Keys.Control|Keys.R)) {Regenerate();return true;}
            if (keyData==(Keys.Control|Keys.Shift|Keys.C)) {CopyLatest();return true;}
            if (keyData==(Keys.Control|Keys.L)) {input.Focus();return true;}
            if ((keyData&Keys.Control)!=0 && (keyData&Keys.Alt)==0) {
                switch(keyData&Keys.KeyCode) {
                    case Keys.Add: case Keys.Oemplus: ReaderPreferences.FontSize++;return true;
                    case Keys.Subtract: case Keys.OemMinus: ReaderPreferences.FontSize--;return true;
                    case Keys.D0: case Keys.NumPad0: ReaderPreferences.FontSize=12;return true;
                }
            }
            return base.ProcessCmdKey(ref msg,keyData);
        }
    }
}
