using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jiyue
{
    internal enum ToastKind { Info, Success, Error }

    internal sealed class Toast : Form
    {
        static readonly List<Toast> active=new List<Toast>();
        static bool suspended, arranging;
        readonly string message;
        readonly ToastKind kind;
        readonly Form anchor;
        readonly Screen originalScreen;
        readonly Button actionButton;
        readonly Timer timer=new Timer();
        readonly Stopwatch clock=new Stopwatch();
        readonly Font textFont;
        Rectangle textBounds;
        int dpi=96;
        double remaining, fadeStarted=-1;
        long lastTick;
        const TextFormatFlags TextFlags=TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

        Toast(string text,ToastKind type,Form owner,string actionText,Action action)
        {
            message=text; kind=type; anchor=owner; originalScreen=Screen.FromPoint(Cursor.Position);
            AutoScaleMode=AutoScaleMode.None; FormBorderStyle=FormBorderStyle.None;
            StartPosition=FormStartPosition.Manual; ShowInTaskbar=false; DoubleBuffered=true;
            AccessibleRole=AccessibleRole.Alert; AccessibleName=Text=message;
            textFont=Style.Font(10); Font=textFont;
            remaining=action!=null ? 6000 : 3500;
            if(action!=null) {
                actionButton=Style.Button(actionText,0,0,32,32);
                actionButton.Font=Font;
                actionButton.AccessibleName=actionText;
                actionButton.Click+=delegate { Close(); action(); };
                Controls.Add(actionButton);
            }
            AppAppearance.Bind(this,delegate(bool dark) {
                BackColor=Theme.Surface(dark); ForeColor=Theme.Ink(dark);
                if(actionButton!=null) AppAppearance.ButtonColors(actionButton,dark,false);
                Invalidate(true);
            });
            timer.Tick+=Tick;
        }

        public static void Show(string message,ToastKind kind=ToastKind.Info,Form owner=null,string actionText=null,Action action=null)
        {
            if(String.IsNullOrWhiteSpace(message)) return;
            if(action!=null && String.IsNullOrWhiteSpace(actionText)) throw new ArgumentException("通知按钮需要名称。","actionText");
            var toast=new Toast(message,kind,owner,actionText,action);
            try {
                while(active.Count>=3) active[0].Dispose();
                active.Add(toast);
                if(!suspended) toast.Reveal();
            } catch { toast.Dispose(); throw; }
        }

        internal static void HideForCapture()
        {
            suspended=true;
            foreach(var toast in active) {
                toast.timer.Stop(); toast.clock.Stop();
                if(toast.Visible) toast.Hide();
            }
        }

        internal static void FinishCapture()
        {
            if(!suspended) return;
            suspended=false;
            foreach(var toast in active.ToArray()) toast.Reveal();
        }

        internal static void CloseAll()
        {
            foreach(var toast in active.ToArray()) toast.Dispose();
            suspended=false;
        }

        protected override bool ShowWithoutActivation {get {return true;}}
        protected override CreateParams CreateParams {
            get {var cp=base.CreateParams; cp.ExStyle|=0x08000088; return cp;} // NOACTIVATE, TOPMOST, TOOLWINDOW
        }
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x21) {m.Result=new IntPtr(3); return;} // MA_NOACTIVATE, even on action clicks
            base.WndProc(ref m);
            if(m.Msg==0x02E0 && !IsDisposed && !Disposing && active.Contains(this))
                BeginInvoke(new Action(delegate { if(!IsDisposed) Reflow(); }));
        }

        bool HasAnchor {get {return anchor!=null && !anchor.IsDisposed && anchor.Visible && anchor.WindowState!=FormWindowState.Minimized;}}
        Screen TargetScreen {get {return HasAnchor ? Screen.FromControl(anchor) : originalScreen;}}
        int Px(int value) {return (int)Math.Round(value*dpi/96d);}

        void Reveal()
        {
            if(!IsHandleCreated) Location=TargetScreen.WorkingArea.Location;
            var handle=Handle; // Create at the target monitor's DPI before measuring fonts.
            if(!clock.IsRunning && clock.ElapsedMilliseconds==0) Opacity=Motion.Enabled ? .01 : 1;
            Reflow();
            base.Show();
            clock.Start(); lastTick=clock.ElapsedMilliseconds; timer.Interval=16; timer.Start();
            AccessibilityNotifyClients(AccessibleEvents.SystemAlert,-1);
        }

        void MeasureToast(Rectangle workArea)
        {
            using(var g=CreateGraphics()) {
                dpi=(int)Math.Round(g.DpiX);
                int pad=Px(12),glyph=Px(16),space=Px(8);
                int maxWidth=Math.Max(Px(92),Math.Min(Px(360),workArea.Width-2*Px(16)));
                int buttonWidth=actionButton==null ? 0 : Math.Min(maxWidth/3,
                    Math.Max(Px(42),TextRenderer.MeasureText(g,actionButton.Text,actionButton.Font,Size.Empty,TextFormatFlags.NoPadding).Width+2*space));
                int fixedWidth=pad*2+glyph+space+(buttonWidth==0 ? 0 : buttonWidth+space);
                int maxTextWidth=Math.Max(1,maxWidth-fixedWidth);
                Size measured=TextRenderer.MeasureText(g,message,Font,new Size(maxTextWidth,Int32.MaxValue),TextFlags);
                int textWidth=Math.Max(1,Math.Min(maxTextWidth,measured.Width));
                int height=Math.Min(Math.Max(Px(44),measured.Height+2*Px(10)),Math.Max(Px(44),workArea.Height-2*Px(16)));
                var size=new Size(textWidth+fixedWidth,height);
                if(ClientSize!=size || Region==null) {
                    ClientSize=size;
                    using(var shape=GlassButton.Rounded(ClientRectangle,Px(Theme.RadiusMedium))) Region=new Region(shape);
                }
                textBounds=new Rectangle(pad+glyph+space,Px(6),textWidth,height-2*Px(6));
                if(actionButton!=null) actionButton.Bounds=new Rectangle(Width-pad-buttonWidth,
                    (height-Px(32))/2,buttonWidth,Px(32));
            }
        }

        static void Reflow()
        {
            if(arranging || suspended) return;
            arranging=true;
            try {
                var placed=new List<Rectangle>();
                for(int i=active.Count-1;i>=0;i--) {
                    var toast=active[i];
                    if(!toast.IsHandleCreated || toast.Disposing || toast.IsDisposed) continue;
                    Rectangle work=toast.TargetScreen.WorkingArea;
                    toast.MeasureToast(work);
                    int gap=toast.Px(8), pad=toast.Px(16);
                    int x=toast.HasAnchor ? toast.anchor.Left+(toast.anchor.Width-toast.Width)/2 : work.Right-pad-toast.Width;
                    int bottom=toast.HasAnchor ? Math.Min(toast.anchor.Bottom,work.Bottom)-pad : work.Bottom-pad;
                    var rect=new Rectangle(Math.Max(work.Left,Math.Min(x,work.Right-toast.Width)),
                        Math.Max(work.Top,bottom-toast.Height),toast.Width,toast.Height);
                    foreach(var other in placed) {
                        var padded=other; padded.Inflate(0,gap);
                        if(rect.IntersectsWith(padded)) rect.Y=Math.Max(work.Top,other.Top-gap-rect.Height);
                    }
                    placed.Add(rect);
                    if(toast.Location!=rect.Location) toast.Location=rect.Location;
                }
            } finally { arranging=false; }
        }

        void Tick(object sender,EventArgs e)
        {
            long now=clock.ElapsedMilliseconds, delta=now-lastTick;
            lastTick=now;
            if(fadeStarted<0 && !Bounds.Contains(Cursor.Position)) remaining-=delta;
            if(remaining<=0 && fadeStarted<0) fadeStarted=now;
            if(fadeStarted>=0) {
                double progress=(now-fadeStarted)/Motion.Normal;
                if(!Motion.Enabled || progress>=1) { Close(); return; }
                Opacity=Math.Max(.01,1-Motion.EaseOutCubic(progress)); timer.Interval=16;
            } else if(now<Motion.Fast && Motion.Enabled) {
                Opacity=Math.Max(.01,Motion.EaseOutCubic((double)now/Motion.Fast));
            } else { if(Opacity!=1) Opacity=1; timer.Interval=80; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool dark=AppAppearance.IsDark;
            var g=e.Graphics; float scale=dpi/96f;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var shape=GlassButton.Rounded(new RectangleF(scale/2,scale/2,Width-scale,Height-scale),Theme.RadiusMedium*scale))
            using(var edge=new Pen(Theme.Line(dark),scale)) g.DrawPath(edge,shape);
            RectangleF icon=new RectangleF(Px(12),(Height-Px(16))/2f,Px(16),Px(16));
            Color color=kind==ToastKind.Error ? Theme.Danger(dark) : kind==ToastKind.Success ? Theme.Success(dark) : Theme.Accent(dark);
            using(var stroke=new Pen(color,1.7f*scale) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
                g.DrawEllipse(stroke,icon);
                float cx=icon.X+icon.Width/2,cy=icon.Y+icon.Height/2;
                if(kind==ToastKind.Success) g.DrawLines(stroke,new[]{new PointF(cx-4*scale,cy),
                    new PointF(cx-scale,cy+3*scale),new PointF(cx+4*scale,cy-3*scale)});
                else {
                    float sign=kind==ToastKind.Error ? -1 : 1;
                    g.DrawLine(stroke,cx,cy-scale*sign,cx,cy+4*scale*sign);
                    using(var brush=new SolidBrush(color)) g.FillEllipse(brush,cx-scale,cy-4*scale*sign-scale,2*scale,2*scale);
                }
            }
            TextRenderer.DrawText(g,message,Font,textBounds,ForeColor,TextFlags);
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing && !IsDisposed) {
                timer.Dispose(); clock.Stop();
                if(active.Remove(this)) Reflow();
                textFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
