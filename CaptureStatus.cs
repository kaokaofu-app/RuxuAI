using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jiyue
{
    // This small control stays outside the capture area and leaves the article in focus.
    internal sealed class CaptureStatus : AppearanceForm
    {
        readonly Label label;
        readonly Panel indicator,progress;
        readonly Timer pulse=new Timer {Interval=65};
        int screens=1;
        readonly System.Diagnostics.Stopwatch elapsed=new System.Diagnostics.Stopwatch();
        public CaptureStatus(string text,Action finish,Action cancel)
        {
            FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false;
            StartPosition=FormStartPosition.Manual; AutoSize=true; AutoSizeMode=AutoSizeMode.GrowAndShrink;
            Font=Style.Font(10); DoubleBuffered=true;
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            var row=new FlowLayoutPanel {AutoSize=true,WrapContents=false,Margin=Padding.Empty,Padding=new Padding(12,7,12,7)};
            indicator=new Panel {Size=new Size(10,12),Anchor=AnchorStyles.Left,Margin=new Padding(2,0,5,0),Visible=finish!=null};
            indicator.Paint+=delegate(object sender,PaintEventArgs e) {
                bool dark=AppAppearance.IsDark;
                double mix=Motion.Enabled ? (1+Math.Cos(elapsed.Elapsed.TotalSeconds*Math.PI*1.6))/2:1;
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                using(var brush=new SolidBrush(Color.FromArgb((int)(120+135*mix),Theme.Danger(dark)))) {
                    int d=Math.Min(indicator.Width,indicator.Height)*4/5;
                    e.Graphics.FillEllipse(brush,(indicator.Width-d)/2,(indicator.Height-d)/2,d,d);
                }
            };
            row.Controls.Add(indicator);
            var column=new TableLayoutPanel {AutoSize=true,ColumnCount=1,RowCount=2,Anchor=AnchorStyles.Left,Margin=new Padding(4,0,8,0)};
            label=new Label {Text=text,AutoSize=true,Margin=Padding.Empty};
            progress=new Panel {Height=2,Dock=DockStyle.Top,Margin=Padding.Empty,Visible=finish!=null};
            progress.Paint+=delegate(object sender,PaintEventArgs e) {
                using(var brush=new SolidBrush(Theme.Accent(AppAppearance.IsDark)))
                    e.Graphics.FillRectangle(brush,0,0,progress.Width*screens/LongArticleImage.MaxScreens,progress.Height);
            };
            column.Controls.Add(label,0,0);column.Controls.Add(progress,0,1);row.Controls.Add(column);
            if(finish!=null) SetProgress(1);
            var done=Style.Button("完成",0,0,1,1,true);
            var stop=Style.Button("取消",0,0,1,1);
            foreach (var button in new[]{done,stop}) { button.AutoSize=true; button.Padding=new Padding(6,1,6,1); }
            var enter=new Label {Text="Enter",AutoSize=true,Margin=new Padding(1,0,6,0),Anchor=AnchorStyles.Left};
            var esc=new Label {Text="Esc",AutoSize=true,Margin=new Padding(1,0,1,0),Anchor=AnchorStyles.Left};
            if(finish!=null) {row.Controls.Add(done);row.Controls.Add(enter);} else {done.Dispose();enter.Dispose();}
            row.Controls.Add(stop);row.Controls.Add(esc);
            done.Click+=delegate {if(finish!=null) finish();};
            stop.Click+=delegate {cancel();};
            Controls.Add(row);
            pulse.Tick+=delegate {indicator.Invalidate();};
            Shown+=delegate {if(finish!=null && Motion.Enabled) {elapsed.Start();pulse.Start();}};
            AppAppearance.Bind(this,delegate(bool dark) {
                BackColor=AppAppearance.Surface(dark); label.ForeColor=AppAppearance.Ink(dark);
                if(finish!=null) {AppAppearance.ButtonColors(done,dark,true);enter.ForeColor=Theme.Muted(dark);}
                esc.ForeColor=Theme.Muted(dark);progress.BackColor=Theme.SurfaceHover(dark);
                AppAppearance.ButtonColors(stop,dark,false);
            });
        }
        protected override bool ShowWithoutActivation { get {return true;} }
        protected override CreateParams CreateParams {
            // Form.TopMost calls SetWindowPos without SWP_NOACTIVATE when its handle is
            // created. Set TOPMOST, TOOLWINDOW and NOACTIVATE together at creation.
            get {var cp=base.CreateParams; cp.ExStyle|=0x08000088; return cp;}
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg==0x21) { m.Result=new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        public bool Place(Rectangle source,bool outside)
        {
            // Create the handle before measuring so child controls have their monitor's DPI.
            var work=Screen.FromRectangle(source).WorkingArea;
            Location=new Point(work.Left,work.Top);
            var handle=Handle;
            PerformLayout();
            Size=GetPreferredSize(Size.Empty);
            int x=Math.Max(work.Left,Math.Min(source.Left,work.Right-Width));
            int y=Math.Max(work.Top,Math.Min(source.Top,work.Bottom-Height));
            var choices=new[]{new Point(x,source.Top-Height-8),new Point(x,source.Bottom+8),
                new Point(source.Right+8,y),new Point(source.Left-Width-8,y)};
            foreach (var p in choices) {
                if (!work.Contains(new Rectangle(p,Size))) continue;
                Location=p; Show(); return true;
            }
            if (outside) return false;
            Location=new Point(work.Left+(work.Width-Width)/2,work.Top+8); Show(); return true;
        }
        public void SetProgress(int value) {
            screens=Math.Max(1,Math.Min(LongArticleImage.MaxScreens,value));
            label.Text="正在采集 · 已 "+screens+" 屏 / 最多 "+LongArticleImage.MaxScreens+" 屏";
            progress.Invalidate();
        }
        protected override void OnSizeChanged(EventArgs e) {
            base.OnSizeChanged(e);
            if(Width<2||Height<2) return;
            using(var shape=GlassButton.Rounded(new RectangleF(0,0,Width,Height),Math.Min(Height/2,Math.Max(1,WindowDpi(this)*18/96)))) {
                var previous=Region;Region=new Region(shape);if(previous!=null)previous.Dispose();
            }
        }
        protected override void Dispose(bool disposing) {if(disposing)pulse.Dispose();base.Dispose(disposing);}
    }
}
