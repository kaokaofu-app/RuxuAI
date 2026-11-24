using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jiyue
{
    // Retains at most one small decoded thumbnail. The original bytes are shared with the conversation.
    internal sealed class SourceThumbnail : Button
    {
        public byte[] Bytes { get; private set; }
        public bool HasImage { get { return BackgroundImage != null; } }

        public SourceThumbnail()
        {
            Size = new Size(73,46);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = Theme.Line(AppAppearance.IsDark);
            BackColor = Theme.Surface(AppAppearance.IsDark);
            BackgroundImageLayout = ImageLayout.Zoom;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
            Visible = false;
        }

        public void SetBytes(byte[] bytes)
        {
            if (Object.ReferenceEquals(Bytes,bytes)) return;
            var old = BackgroundImage;
            BackgroundImage = null;
            if (old != null) old.Dispose();
            Bytes = bytes;
            if (bytes != null && bytes.Length > 0)
            {
                Bitmap small = null;
                try
                {
                    using (var stream = new MemoryStream(bytes,false))
                    using (var original = Image.FromStream(stream,false,true))
                    {
                        double scale = Math.Min(1,Math.Min(240.0/original.Width,150.0/original.Height));
                        small = new Bitmap(Math.Max(1,(int)Math.Round(original.Width*scale)),
                                           Math.Max(1,(int)Math.Round(original.Height*scale)), PixelFormat.Format32bppPArgb);
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(original,new Rectangle(Point.Empty,small.Size));
                        }
                    }
                    BackgroundImage = small;
                    small = null;
                }
                catch (Exception ex) { if (!ImageFailed(ex)) throw; }
                finally { if (small != null) small.Dispose(); }
            }
            Visible = HasImage;
        }

        public void ApplyTheme(bool dark)
        {
            BackColor = AppAppearance.Surface(dark);
            FlatAppearance.BorderColor = Theme.Line(dark);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;
            g.Clear(Parent==null ? BackColor : Parent.BackColor);
            float u=g.DpiX/96f;
            if (Width<=3*u || Height<=3*u) return;
            var bounds=new RectangleF(u/2,u/2,Width-u,Height-u);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            using (var shape=GlassButton.Rounded(bounds,Theme.RadiusSmall*u))
            using (var fill=new SolidBrush(BackColor))
            using (var edge=new Pen(FlatAppearance.BorderColor,u)) {
                g.FillPath(fill,shape);
                var thumbnail=BackgroundImage;
                if (thumbnail!=null) {
                    var saved=g.Save();
                    g.SetClip(shape);
                    float scale=Math.Min(bounds.Width/thumbnail.Width,bounds.Height/thumbnail.Height);
                    float w=thumbnail.Width*scale,h=thumbnail.Height*scale;
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    g.DrawImage(thumbnail,(Width-w)/2,(Height-h)/2,w,h);
                    g.Restore(saved);
                }
                if (Focused && ShowFocusCues) { edge.Color=SystemInformation.HighContrast ? SystemColors.Highlight : AppAppearance.Accent(AppAppearance.IsDark); edge.Width=3*u; }
                g.DrawPath(edge,shape);
            }
        }

        internal static bool ImageFailed(Exception ex)
        {
            // GDI+ also reports invalid encoded images as OutOfMemoryException.
            return ex is ArgumentException || ex is OutOfMemoryException || ex is ExternalException || ex is IOException;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var old = BackgroundImage;
                BackgroundImage = null;
                if (old != null) old.Dispose();
                Bytes = null;
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class SourceCard : UserControl
    {
        readonly SourceThumbnail thumbnail;
        readonly Label heading, time, detail;
        readonly FlowLayoutPanel articleActions;
        readonly Button originalText, previous, next, expand;
        readonly TableLayoutPanel grid;
        readonly ToolTip previewTip=new ToolTip {OwnerDraw=true,InitialDelay=400,ReshowDelay=400,AutoPopDelay=30000,ShowAlways=false};
        Bitmap hoverImage;
        bool actionsAvailable;
        public bool Expanded {get; private set;}
        public event Action ExpandedChanged;
        string articleText;
        bool longPicture;
        SourceImageWindow viewer;
        Form textViewer;
        public event Action<int> MoveSection;

        public SourceCard()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Inherit;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Style.Tint;
            Padding = new Padding(12,6,12,6);
            TabStop = false;
            grid = new TableLayoutPanel {Dock=DockStyle.Fill,Margin=Padding.Empty,ColumnCount=4,RowCount=3};
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent,50));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            thumbnail = new SourceThumbnail {Anchor=AnchorStyles.Left,Size=new Size(40,28),Margin=new Padding(0,0,8,0),AccessibleName="放大查看来源截图"};
            thumbnail.Click += delegate { OpenPicture(); };
            previewTip.Popup+=PrepareThumbnailPreview;
            previewTip.Draw+=delegate(object sender,DrawToolTipEventArgs e) {if(hoverImage!=null) e.Graphics.DrawImageUnscaled(hoverImage,0,0);};
            thumbnail.MouseLeave+=delegate {previewTip.Hide(thumbnail);};
            thumbnail.VisibleChanged+=delegate {if(!thumbnail.Visible && thumbnail.IsHandleCreated)previewTip.Hide(thumbnail);};
            heading = new Label {Dock=DockStyle.Fill,Font=Style.Font(10,true),ForeColor=Style.Ink,UseMnemonic=false,
                AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty};
            time = new Label {AutoSize=true,Anchor=AnchorStyles.Right,Font=Style.Font(9),ForeColor=Style.Muted,Margin=new Padding(6,0,0,0)};
            detail = new Label {Dock=DockStyle.Fill,Font=Style.Font(9),ForeColor=Style.Muted,UseMnemonic=false,
                AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Margin=Padding.Empty};
            expand=Style.Button("▾",0,0,32,32);expand.Anchor=AnchorStyles.Right;expand.Margin=new Padding(4,0,0,0);
            expand.Click+=delegate {SetExpanded(!Expanded);var handler=ExpandedChanged;if(handler!=null)handler();};
            grid.Controls.Add(thumbnail,0,0); grid.SetRowSpan(thumbnail,2);
            grid.Controls.Add(heading,1,0); grid.Controls.Add(time,2,0);
            grid.Controls.Add(expand,3,0);grid.Controls.Add(detail,1,1); grid.SetColumnSpan(detail,3);
            articleActions = new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,Margin=Padding.Empty,Visible=false};
            originalText=SourceButton("查看原文"); previous=SourceButton("上一段"); next=SourceButton("下一段");
            originalText.Click+=delegate { OpenText(); };
            previous.Click+=delegate { if (MoveSection!=null) MoveSection(-1); };
            next.Click+=delegate { if (MoveSection!=null) MoveSection(1); };
            articleActions.Controls.AddRange(new Control[]{originalText,previous,next});
            grid.Controls.Add(articleActions,0,2); grid.SetColumnSpan(articleActions,4);
            Controls.Add(grid);
            SetExpanded(false);
            ResumeLayout(true);
        }
        public void SetExpanded(bool value)
        {
            Expanded=value; detail.Visible=value; articleActions.Visible=value && actionsAvailable;
            grid.RowStyles[0].SizeType=SizeType.Percent;grid.RowStyles[0].Height=value?50:100;
            grid.RowStyles[1].SizeType=value?SizeType.Percent:SizeType.Absolute;grid.RowStyles[1].Height=value?50:0;
            expand.Text=value?"▴":"▾";expand.AccessibleName=value?"折叠来源详情":"展开来源详情";
            grid.PerformLayout();if(Parent!=null)Parent.PerformLayout(this,"PreferredSize");
        }
        void PrepareThumbnailPreview(object sender,PopupEventArgs e)
        {
            if(hoverImage!=null){hoverImage.Dispose();hoverImage=null;}
            if(!thumbnail.Visible || !thumbnail.HasImage || thumbnail.Bytes==null){e.Cancel=true;return;}
            try {
                using(var stream=new MemoryStream(thumbnail.Bytes,false))
                using(var source=Image.FromStream(stream,false,true))
                using(var screen=thumbnail.CreateGraphics()) {
                    int width=Math.Max(1,(int)Math.Round(320*screen.DpiX/96f));
                    int height=Math.Min(width,Math.Max(1,(int)Math.Round((double)width*source.Height/source.Width)));
                    hoverImage=new Bitmap(width,height);
                    using(var g=Graphics.FromImage(hoverImage)) {
                        g.Clear(AppAppearance.Surface(AppAppearance.IsDark));g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                        g.DrawImage(source,new Rectangle(0,0,width,height),0,0,source.Width,Math.Min(source.Height,(int)Math.Ceiling((double)height*source.Width/width)),GraphicsUnit.Pixel);
                    }
                    e.ToolTipSize=hoverImage.Size;
                }
            } catch(Exception ex){if(!SourceThumbnail.ImageFailed(ex))throw;e.Cancel=true;}
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent == null ? BackColor : Parent.BackColor);
            if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = GlassButton.Rounded(ClientRectangle, Theme.RadiusLarge * g.DpiX / 96f))
            using (var fill = new SolidBrush(BackColor))
                g.FillPath(fill, shape);
        }

        static Button SourceButton(string label)
        {
            var button=Style.Button(label,0,0,1,1);
            button.AutoSize=true; button.AutoSizeMode=AutoSizeMode.GrowAndShrink;
            button.Padding=new Padding(7,2,7,2); button.Margin=new Padding(0,3,6,0);
            return button;
        }

        public void ApplyTheme(bool dark)
        {
            bool contrast = SystemInformation.HighContrast;
            BackColor = contrast ? SystemColors.Control : AppAppearance.Tint(dark);
            heading.ForeColor = contrast ? SystemColors.ControlText : AppAppearance.Ink(dark);
            time.ForeColor = detail.ForeColor = contrast ? SystemColors.ControlText : AppAppearance.Muted(dark);
            thumbnail.ApplyTheme(dark);
            foreach (var button in new[]{originalText,previous,next,expand}) AppAppearance.ButtonColors(button,dark,false);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            if (heading == null || detail == null) return base.GetPreferredSize(proposedSize);
            int line = Math.Max(heading.Font.Height,detail.Font.Height)+2;
            int content = Math.Max(line*(Expanded?2:1),Math.Max(expand==null?0:expand.Height,thumbnail.HasImage ? thumbnail.Height : 0));
            if (Expanded && articleActions!=null && actionsAvailable) {
                int width=proposedSize.Width>1 ? proposedSize.Width : Width;
                content+=articleActions.GetPreferredSize(new Size(Math.Max(1,width-Padding.Horizontal),0)).Height;
            }
            // The hosting table supplies the width. Long document titles must not enlarge the reading window.
            return new Size(1,Padding.Vertical+content+2);
        }

        internal static string Caption(string scope, string sourceTitle, bool hasPicture, bool hasText)
        {
            string kind = (scope ?? "").Trim();
            if (IsPage(kind)) kind = "整页";
            else if (kind.Contains("框选")) kind = "框选";
            else if (kind.Length == 0) kind = hasPicture ? "截图" : hasText ? "原文" : "";
            string title = HistoryWindow.ShortLine(sourceTitle,120);
            if (kind.Length == 0) return title;
            return title.Length == 0 || title == kind ? kind : kind + " · " + title;
        }

        static bool IsPage(string scope)
        {
            return scope != null && (scope.Contains("整页") || scope.StartsWith("正文前",StringComparison.Ordinal));
        }

        internal static string LocalTime(DateTime utc)
        {
            if (utc == default(DateTime)) return "";
            if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc,DateTimeKind.Utc);
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
        }

        public void SetSource(string scope, string sourceTitle, DateTime capturedUtc, byte[] preview, string excerpt)
        {
            bool changed = !Object.ReferenceEquals(thumbnail.Bytes,preview);
            if (changed && viewer != null) viewer.Close();
            thumbnail.SetBytes(preview);
            if(changed)previewTip.SetToolTip(thumbnail,thumbnail.HasImage?"来源截图预览":null);
            string original = HistoryWindow.ShortLine(excerpt,120);
            heading.Text = Caption(scope,sourceTitle,thumbnail.HasImage,original.Length>0);
            time.Text = LocalTime(capturedUtc);
            time.Visible = time.Text.Length > 0;
            bool page = IsPage(scope);
            longPicture = (scope ?? "").Contains("长截图");
            if (thumbnail.HasImage) detail.Text = page ? "当时可见的页面 · 点击缩略图可放大" : "点击缩略图可放大";
            else if (original.Length > 0) detail.Text = original;
            else detail.Text = preview != null && preview.Length > 0 ? "这张截图无法预览。" : "";
            if (viewer != null) viewer.Text = PreviewTitle();
            Visible = heading.Text.Length + detail.Text.Length + time.Text.Length > 0 || thumbnail.HasImage;
            if (changed && Parent != null) Parent.PerformLayout(this,"PreferredSize");
        }

        public void SetArticle(string text, int section, int count, bool canNavigate, string notice)
        {
            bool layoutChanged=(articleText==null)!=(text==null) || previous.Visible!=(count>1);
            articleText=text;
            originalText.Visible=!String.IsNullOrEmpty(text);
            previous.Visible=next.Visible=count>1;
            previous.Enabled=canNavigate && section>0; next.Enabled=canNavigate && section<count;
            previous.Text=section==1 ? "回到速览" : "上一段";
            next.Text=section==0 ? "解读第 1 段" : "下一段";
            actionsAvailable=count>1 || !String.IsNullOrEmpty(text);
            articleActions.Visible=Expanded && actionsAvailable;
            string extent=count>1 ? (section==0 ? "全文速览 · 首中尾节选 · 共 "+count+" 段" : "原文第 "+section+" / "+count+" 段") :
                !String.IsNullOrEmpty(text) ? text.Length.ToString("N0")+" 个字符" : "点击缩略图查看长截图";
            detail.Text=String.IsNullOrWhiteSpace(notice) ? extent : "部分内容 · "+extent;
            if (layoutChanged && Parent!=null) Parent.PerformLayout(this,"PreferredSize");
        }

        void OpenText()
        {
            if (String.IsNullOrEmpty(articleText)) return;
            if (textViewer!=null && !textViewer.IsDisposed) {
                if (textViewer.WindowState==FormWindowState.Minimized) textViewer.WindowState=FormWindowState.Normal;
                textViewer.Activate(); return;
            }
            var owner=FindForm();
            if (owner==null || owner.IsDisposed) return;
            var window=new AppearanceForm();
            Style.Form(window,"如许AI · 采集原文",720,560);
            window.StartPosition=FormStartPosition.CenterParent; window.ShowInTaskbar=false;
            var body=new RichTextBox {ReadOnly=true,Dock=DockStyle.Fill,DetectUrls=false,BorderStyle=BorderStyle.None,
                ScrollBars=RichTextBoxScrollBars.Vertical,Text=articleText,Font=Style.Font(11),AccessibleName="采集原文"};
            window.Padding=new Padding(18); window.Controls.Add(body);
            var bar=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=3,Padding=new Padding(0,0,0,10)};
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var label=new Label {Text="查找原文",AutoSize=true,Anchor=AnchorStyles.Left};
            var term=new TextBox {Dock=DockStyle.Fill,AccessibleName="查找原文"};
            var next=Style.Button("下一处",0,0,1,1); next.AutoSize=true; next.Enabled=false;
            bar.Controls.Add(label,0,0); bar.Controls.Add(term,1,0); bar.Controls.Add(next,2,0); window.Controls.Add(bar);
            body.HideSelection=false;
            int from=0;
            Action find=delegate {
                if(term.TextLength==0) return;
                int hit=body.Text.IndexOf(term.Text,from,StringComparison.OrdinalIgnoreCase);
                if(hit<0) hit=body.Text.IndexOf(term.Text,StringComparison.OrdinalIgnoreCase);
                label.Text=hit<0?"未找到":"查找原文";
                if(hit>=0) {body.Select(hit,term.TextLength);body.ScrollToCaret();from=hit+term.TextLength;}
            };
            term.TextChanged+=delegate {from=0;label.Text="查找原文";next.Enabled=term.TextLength>0;};
            next.Click+=delegate {find();};
            term.KeyDown+=delegate(object sender,KeyEventArgs e) {if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;find();}};
            AppAppearance.Bind(window,delegate(bool dark) { window.BackColor=body.BackColor=AppAppearance.Surface(dark);
                term.BackColor=AppAppearance.Background(dark); term.ForeColor=label.ForeColor=body.ForeColor=AppAppearance.Ink(dark);
                AppAppearance.ButtonColors(next,dark,false); AppAppearance.ScrollColors(body,dark); });
            window.FormClosed+=delegate {textViewer=null;};
            window.KeyPreview=true;
            window.KeyDown+=delegate(object sender,KeyEventArgs e) {
                if(e.KeyData==(Keys.Control|Keys.F)){e.SuppressKeyPress=true;term.Focus();term.SelectAll();}
                else if(e.KeyCode==Keys.Escape) window.Close();
            };
            textViewer=window; window.ResumeLayout(true);
            CapturePopup.ShowFrom(window,owner,originalText);
        }

        string PreviewTitle() { return heading.Text.Length > 0 ? "如许AI · "+heading.Text : "如许AI · 来源截图"; }

        void OpenPicture()
        {
            if (!thumbnail.HasImage) return;
            if (viewer != null && !viewer.IsDisposed)
            {
                if (viewer.WindowState == FormWindowState.Minimized) viewer.WindowState = FormWindowState.Normal;
                viewer.Activate(); return;
            }
            var owner = FindForm();
            if (owner == null || owner.IsDisposed) return;
            var next = new SourceImageWindow(PreviewTitle());
            try
            {
                next.LoadBytes(thumbnail.Bytes,longPicture);
                viewer = next;
                next.FormClosed += delegate { if (viewer == next) viewer = null; };
                CapturePopup.ShowFrom(next,owner,thumbnail);
            }
            catch (Exception ex)
            {
                viewer = null; next.Dispose();
                if (!SourceThumbnail.ImageFailed(ex)) throw;
                detail.Text = "原图暂时无法打开。";
            }
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing){previewTip.Dispose();if(hoverImage!=null){hoverImage.Dispose();hoverImage=null;}}
            if (disposing && viewer != null) { var previous=viewer; viewer=null; previous.Dispose(); }
            if (disposing && textViewer!=null) {textViewer.Dispose(); textViewer=null;}
            base.Dispose(disposing);
        }

        sealed class SourceImageWindow : AppearanceForm
        {
            readonly PictureBox picture;
            readonly Panel surface;
            readonly Button zoom;
            bool longPreview, fitWidth;
            MemoryStream stream;
            Image original;

            public SourceImageWindow(string title)
            {
                Style.Form(this,title,760,510);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimumSize = new Size(300,220);
                ShowInTaskbar = false;
                MinimizeBox = false;
                var layout = new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(10),ColumnCount=1,RowCount=2};
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                surface = new Panel {Dock=DockStyle.Fill,BackColor=Color.White,AutoScroll=true,Margin=Padding.Empty};
                picture = new PictureBox {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Margin=Padding.Empty};
                surface.Controls.Add(picture);
                zoom = Style.Button("实际像素",0,0,1,1);
                zoom.AutoSize = true; zoom.AutoSizeMode = AutoSizeMode.GrowAndShrink; zoom.Anchor = AnchorStyles.Right;
                zoom.Padding = new Padding(10,4,10,4); zoom.Margin = new Padding(0,7,0,0);
                zoom.Click += delegate {
                    if (longPreview) {
                        fitWidth=!fitWidth;
                        surface.AutoScrollPosition=Point.Empty;
                        SetLongSize();
                        return;
                    }
                    bool actual = picture.Dock == DockStyle.Fill;
                    surface.AutoScrollPosition = Point.Empty;
                    picture.Dock = actual ? DockStyle.None : DockStyle.Fill;
                    picture.SizeMode = actual ? PictureBoxSizeMode.Normal : PictureBoxSizeMode.Zoom;
                    if (actual) { picture.Location=Point.Empty; picture.Size=original.Size; }
                    surface.AutoScrollMinSize = actual ? original.Size : Size.Empty;
                    zoom.Text = actual ? "适合窗口" : "实际像素";
                };
                surface.SizeChanged+=delegate { if(longPreview && fitWidth) SetLongSize(); };
                layout.Controls.Add(surface,0,0); layout.Controls.Add(zoom,0,1); Controls.Add(layout);
                surface.HandleCreated += delegate { AppAppearance.ScrollColors(surface,AppAppearance.IsDark); };
                AppAppearance.Bind(this, delegate(bool dark) {
                    BackColor = AppAppearance.Background(dark);
                    ForeColor = AppAppearance.Ink(dark);
                    surface.BackColor = AppAppearance.Surface(dark);
                    AppAppearance.ScrollColors(surface,dark);
                    AppAppearance.ButtonColors(zoom,dark,false);
                });
                Load += delegate {
                    var area = Screen.FromControl(Owner ?? this).WorkingArea;
                    Size = new Size(Math.Min(Width,area.Width),Math.Min(Height,area.Height));
                    Location = new Point(Math.Max(area.Left,Math.Min(Left,area.Right-Width)),
                                         Math.Max(area.Top,Math.Min(Top,area.Bottom-Height)));
                };
                ResumeLayout(true);
            }
            void SetLongSize()
            {
                if (original==null) return;
                int viewport=Math.Max(1,surface.Width-SystemInformation.VerticalScrollBarWidth-2);
                int width=fitWidth ? Math.Min(viewport,original.Width) : original.Width;
                Size size=new Size(width,Math.Max(1,(int)Math.Round((double)width*original.Height/original.Width)));
                picture.Dock=DockStyle.None; picture.SizeMode=PictureBoxSizeMode.StretchImage; picture.Size=size;
                surface.AutoScrollMinSize=size;
                zoom.Text=fitWidth ? "实际像素" : "适合宽度";
            }
            public void LoadBytes(byte[] data,bool longImage)
            {
                stream = new MemoryStream(data,false);
                original = Image.FromStream(stream,false,true);
                picture.Image = original;
                longPreview=fitWidth=longImage;
                if (longPreview) SetLongSize();
            }
            protected override bool ProcessCmdKey(ref Message msg, Keys key)
            {
                if (key == Keys.Escape) { Close(); return true; }
                return base.ProcessCmdKey(ref msg,key);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (picture != null) picture.Image = null;
                    if (original != null) { original.Dispose(); original=null; }
                    if (stream != null) { stream.Dispose(); stream=null; }
                }
                base.Dispose(disposing);
            }
        }
    }
}
