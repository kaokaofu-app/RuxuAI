using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jiyue
{
    internal sealed class ReplyWaitingDots : Control
    {
        readonly ReplyView reply;
        readonly Stopwatch elapsed=new Stopwatch();
        readonly Label status;
        readonly LinkLabel help;
        int answerStart;
        int lastSecond=-1;
        public event Action SettingsRequested;

        public ReplyWaitingDots(ReplyView reply)
        {
            this.reply=reply;
            Size=new Size(30,20);
            Visible=false;
            TabStop=false;
            AccessibleName="正在等待回答";
            AccessibleRole=AccessibleRole.Graphic;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,true);
            SetStyle(ControlStyles.Selectable,false);
            status=new Label {AutoSize=true,Visible=false,Font=Style.Font(9),Margin=Padding.Empty};
            help=new LinkLabel {AutoSize=false,Visible=false,Font=Style.Font(9),TabStop=true,
                Text="思考模型较慢，可在 设置 › 阅读选项 › 思考模式 关闭思考"};
            help.LinkArea=new LinkArea(help.Text.IndexOf("设置",StringComparison.Ordinal),2);
            help.LinkClicked+=delegate {if(SettingsRequested!=null) SettingsRequested();};
            Controls.Add(status);Controls.Add(help);
        }

        public bool IsWaiting { get {return elapsed.IsRunning;} }

        public void Start(int index)
        {
            answerStart=index;
            lastSecond=-1;
            elapsed.Restart();
            Advance();
        }
        public void Stop()
        {
            if (!IsWaiting) return;
            elapsed.Reset();
            Visible=false;
        }
        public void Advance()
        {
            if (!IsWaiting || !reply.IsHandleCreated || reply.IsDisposed) return;
            int y=reply.ReplyStartY(answerStart);
            if (y<0 || y>=reply.ClientSize.Height) { Visible=false; return; }
            int seconds=(int)elapsed.Elapsed.TotalSeconds;
            var width=Math.Max(1,reply.ClientSize.Width);
            if(seconds!=lastSecond || Width!=width) {
                float scale; using(var g=CreateGraphics()) scale=g.DpiX/96f;
                lastSecond=seconds;
                int h=Math.Max(status.PreferredHeight,(int)(20*scale));
                status.Location=new Point((int)(30*scale),0);
                status.Visible=seconds>=3; help.Visible=seconds>=15;
                if(seconds>=3) status.Text="正在思考 · 已等待 "+seconds+" 秒";
                help.SetBounds(0,h,width,Math.Max(h,TextRenderer.MeasureText(help.Text,help.Font,
                    new Size(width,Int32.MaxValue),TextFormatFlags.WordBreak).Height));
                Size=new Size(width,h+(seconds>=15?help.Height:0));
                AccessibleName=seconds<3?"正在等待回答":status.Text;
            }
            help.LinkColor=SystemInformation.HighContrast?SystemColors.HotTrack:Theme.Accent(AppAppearance.IsDark);
            help.ActiveLinkColor=help.LinkColor;help.VisitedLinkColor=help.LinkColor;
            Location=new Point(reply.Left,reply.Top+Math.Min(y,Math.Max(0,reply.ClientSize.Height-Height)));
            Visible=true;
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale=e.Graphics.DpiX/96f;
            double phase=elapsed.Elapsed.TotalSeconds/1.05;
            bool animate=Motion.Enabled;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            for (int i=0;i<3;i++) {
                double lift=animate ? Math.Max(0,Math.Sin(2*Math.PI*(phase-0.16*i))) : 0;
                float diameter=4.2f*scale;
                float x=(3+i*7.5f)*scale;
                float y=(float)((10.5-3*lift)*scale)-diameter/2;
                int opacity=SystemInformation.HighContrast ? 255 : (int)(142+113*lift);
                using (var dot=new SolidBrush(Color.FromArgb(opacity,ForeColor)))
                    e.Graphics.FillEllipse(dot,x,y,diameter,diameter);
            }
        }
    }
}
