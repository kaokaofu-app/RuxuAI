using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jiyue
{
    internal sealed class QuestionComposer : Panel
    {
        readonly QuestionBox editor;
        readonly Button send;
        int dpi = 96, lineHeight = 22, desiredHeight;
        bool layoutQueued, dark;
        Control layoutParent;

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        public QuestionComposer(QuestionBox editor, Button send)
        {
            this.editor = editor;
            this.send = send;
            SuspendLayout();
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            TabStop = false;
            Margin = new Padding(0,12,0,0);
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint,true);
            editor.AutoSize = false;
            editor.BorderStyle = BorderStyle.None;
            editor.Margin = Padding.Empty;
            editor.TabIndex = 0;
            send.AutoSize = false;
            send.TabIndex = 1;
            Controls.Add(editor);
            Controls.Add(send);
            ApplyTheme(false);
            editor.TextChanged += delegate { UpdateHeight(); };
            editor.HandleCreated += delegate { MeasureEditor(); };
            editor.FontChanged += delegate { MeasureEditor(); };
            editor.GotFocus += delegate { Invalidate(); };
            editor.LostFocus += delegate { Invalidate(); };
            ResumeLayout(true);
        }

        int Px(int value) { return (value*dpi + 48)/96; }

        public void ApplyTheme(bool dark)
        {
            this.dark = dark;
            BackColor = Theme.Surface(dark);
            editor.BackColor = BackColor;
            editor.ForeColor = Theme.Ink(dark);
            send.BackColor = BackColor;
            Invalidate(true);
        }

        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            if (editor != null && send != null) ApplyTheme(dark);
        }

        void MeasureEditor()
        {
            if (!editor.IsHandleCreated) return;
            using (var g = editor.CreateGraphics())
            {
                try { dpi = (int)GetDpiForWindow(editor.Handle); }
                catch (EntryPointNotFoundException) { dpi = (int)Math.Round(g.DpiX); }
                if (dpi <= 0) dpi = 96;
                lineHeight = TextRenderer.MeasureText(g,"国Ag",editor.Font,Size.Empty,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
            }
            PerformLayout();
            UpdateHeight();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            const int DpiChangedAfterParent = 0x02E3;
            if (message.Msg == DpiChangedAfterParent && editor != null) MeasureEditor();
        }

        protected override void OnParentChanged(EventArgs e)
        {
            if (layoutParent != null) layoutParent.SizeChanged -= ParentResized;
            base.OnParentChanged(e);
            layoutParent = Parent;
            if (layoutParent != null) layoutParent.SizeChanged += ParentResized;
            if (editor != null) UpdateHeight();
        }

        void ParentResized(object sender, EventArgs e) { UpdateHeight(); }

        void UpdateHeight()
        {
            int maxLines = 6;
            if (Parent != null && Parent.ClientSize.Height > 0)
                maxLines = Math.Max(2,Math.Min(6,(Parent.ClientSize.Height/3 - Px(24))/lineHeight));
            int lines = Math.Max(2,Math.Min(maxLines,editor.DisplayLineCount));
            int next = lines*lineHeight + Px(24) + 2;
            if (desiredHeight == next) return;
            desiredHeight = next;
            // Word wrapping is recalculated by EDIT after its new width arrives. Let the current
            // table layout finish before requesting the row height matching that real line count.
            if (layoutQueued || !IsHandleCreated || Parent == null || Disposing) return;
            layoutQueued = true;
            BeginInvoke((MethodInvoker)delegate {
                layoutQueued = false;
                if (!IsDisposed && Parent != null) Parent.PerformLayout(this,"PreferredSize");
            });
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(1,desiredHeight > 0 ? desiredHeight : 2*lineHeight + Px(24) + 2);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (editor == null || send == null) return;
            int button = Px(36), right = Px(11), bottom = Px(10), left = Px(17), top = Px(12);
            send.SetBounds(Math.Max(0,ClientSize.Width-right-button),Math.Max(0,ClientSize.Height-bottom-button),button,button);
            editor.SetBounds(left,top,Math.Max(1,send.Left-Px(9)-left),Math.Max(1,ClientSize.Height-2*top));
            UpdateHeight();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && editor.CanFocus) editor.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
            float stroke = Math.Max(1f,dpi/96f);
            float width = Width-stroke, height = Height-stroke;
            if (width <= 0 || height <= 0) return;
            float inset = stroke/2, diameter = Math.Min(Px(2*Theme.RadiusMedium),Math.Min(width,height));
            Color edge = editor.Focused ? Theme.Accent(dark) : Theme.Line(dark);
            using (var outline = new GraphicsPath())
            using (var fill = new SolidBrush(BackColor))
            using (var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : edge,stroke))
            {
                outline.AddArc(inset,inset,diameter,diameter,180,90);
                outline.AddArc(inset+width-diameter,inset,diameter,diameter,270,90);
                outline.AddArc(inset+width-diameter,inset+height-diameter,diameter,diameter,0,90);
                outline.AddArc(inset,inset+height-diameter,diameter,diameter,90,90);
                outline.CloseFigure();
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(fill,outline);
                g.DrawPath(border,outline);
            }
            base.OnPaint(e);
        }
    }

    internal sealed class SendQuestionButton : Button
    {
        readonly Timer fade=new Timer {Interval=15};
        readonly System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        readonly ToolTip hint=new ToolTip();
        bool generating;
        float progress,from;
        public bool Generating {
            get {return generating;}
            set {
                if(value==generating) return;
                generating=value; from=progress; fade.Stop();clock.Restart();
                Text=AccessibleName=value?"停止生成":"发送问题";
                hint.SetToolTip(this,value?"停止生成（Esc）":"发送问题（Enter）");
                if(Motion.Enabled && Visible) fade.Start(); else progress=value?1f:0f;
                Invalidate();
            }
        }
        public SendQuestionButton()
        {
            Text = "提问";
            AccessibleName = Text;
            AccessibleRole = AccessibleRole.PushButton;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,true);
            hint.SetToolTip(this,"发送问题（Enter）");
            fade.Tick+=delegate {
                float elapsed=Motion.Enabled?Math.Min(1f,clock.ElapsedMilliseconds/120f):1f;
                progress=from+((generating?1f:0f)-from)*elapsed;Invalidate();
                if(elapsed>=1f) fade.Stop();
            };
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing) {fade.Dispose();hint.Dispose();}
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Width < 4 || Height < 4) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = Math.Min(Width,Height)/34f;
            bool hover = ClientRectangle.Contains(PointToClient(MousePosition));
            bool dark = BackColor.GetBrightness() < 0.5f;
            Color fill = AppAppearance.Accent(dark);
            Color ink = Theme.OnAccent;
            if (!Enabled)
            {
                fill = Theme.SurfaceHover(dark);
                ink = Theme.Muted(dark);
            }
            else if (hover)
                fill = Capture ? Theme.AccentPressed(dark) : Theme.AccentHover(dark);
            if (SystemInformation.HighContrast)
            {
                fill = Enabled ? SystemColors.Highlight : SystemColors.Window;
                ink = Enabled ? SystemColors.HighlightText : SystemColors.GrayText;
            }
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush,unit,unit,Width-2*unit,Height-2*unit);
            float x = Width/2f, y = Height/2f;
            if(progress>0) using(var stopFill=new SolidBrush(Color.FromArgb((int)(255*progress),Theme.Ink(dark))))
                g.FillEllipse(stopFill,unit,unit,Width-2*unit,Height-2*unit);
            if(progress>0) using(var shape=GlassButton.Rounded(new RectangleF(x-5*unit,y-5*unit,10*unit,10*unit),2*unit))
                using(var brush=new SolidBrush(Color.FromArgb((int)(255*progress),Theme.Background(dark)))) g.FillPath(brush,shape);
            using (var arrow = new Pen(Color.FromArgb((int)(255*(1-progress)),ink),2.1f*unit))
            {
                arrow.StartCap = arrow.EndCap = LineCap.Round;
                arrow.LineJoin = LineJoin.Round;
                g.DrawLine(arrow,x,y+6*unit,x,y-6*unit);
                g.DrawLines(arrow,new[]{new PointF(x-5*unit,y-unit),new PointF(x,y-6*unit),new PointF(x+5*unit,y-unit)});
            }
            if (Focused && ShowFocusCues)
                using (var ring = new Pen(ink,unit) {DashStyle=DashStyle.Dot})
                    g.DrawEllipse(ring,4*unit,4*unit,Width-8*unit,Height-8*unit);
        }
    }
}
