using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jiyue
{
    internal sealed class JumpToLatestButton : Button
    {
        readonly ToolTip hint = new ToolTip();
        bool dark;

        public JumpToLatestButton()
        {
            Text = AccessibleName = "回到最新消息";
            AccessibleRole = AccessibleRole.PushButton;
            AutoSize = false;
            Size = new Size(40,32);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,true);
            hint.SetToolTip(this,AccessibleName);
            ApplyTheme(false);
        }

        public void ApplyTheme(bool useDark)
        {
            dark = useDark;
            BackColor = Theme.Surface(dark);
            ForeColor = Theme.Muted(dark);
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width<2 || Height<2) return;
            using (var outline = Pill(0))
                Region = new Region(outline);
        }

        GraphicsPath Pill(float inset)
        {
            float w = Width-2*inset, h = Height-2*inset, diameter = Math.Min(w,h);
            var path = new GraphicsPath();
            path.AddArc(inset,inset,diameter,diameter,180,90);
            path.AddArc(inset+w-diameter,inset,diameter,diameter,270,90);
            path.AddArc(inset+w-diameter,inset+h-diameter,diameter,diameter,0,90);
            path.AddArc(inset,inset+h-diameter,diameter,diameter,90,90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float side = Math.Min(Width,Height);
            if (side<4) return;
            float unit = side/30f, x = Width/2f, y = Height/2f;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool contrast = SystemInformation.HighContrast;
            bool hover = ClientRectangle.Contains(PointToClient(MousePosition));
            Color fill = AppAppearance.Surface(dark), ink = ForeColor;
            Color border = Theme.Line(dark);
            if (contrast)
            {
                border = SystemColors.WindowText;
                if (hover || Focused) { fill = SystemColors.Highlight; ink = SystemColors.HighlightText; }
            }
            else if (hover)
                fill = Capture ? Theme.Selected(dark) : Theme.SurfaceHover(dark);

            using (var shape = Pill(unit/2))
            using (var brush = new LinearGradientBrush(ClientRectangle,
                contrast ? fill : ControlPaint.Light(fill,.055f),fill,LinearGradientMode.Vertical))
            using (var edge = new Pen(border,unit))
            using (var dots = new SolidBrush(ink))
            {
                g.FillPath(brush,shape);
                g.DrawPath(edge,shape);
                float diameter = 2.8f*unit;
                for (int i=-1;i<=1;i++)
                    g.FillEllipse(dots,x+i*5*unit-diameter/2,y-diameter/2,diameter,diameter);
                if (Focused && ShowFocusCues)
                {
                    edge.Color = ink;
                    edge.DashStyle = DashStyle.Dot;
                    using (var ring = Pill(3*unit)) g.DrawPath(edge,ring);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hint.Dispose();
            base.Dispose(disposing);
        }
    }
}
