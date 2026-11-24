using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jiyue
{
    internal enum ButtonIcon { None, Settings, Region, Article }

    internal sealed class GlassButton : Button
    {
        readonly ButtonIcon icon;
        readonly string shortcut;
        bool hovered, down, space;
        public bool Primary { get; private set; }

        public GlassButton(bool primary, ButtonIcon icon = ButtonIcon.None, string shortcut = null)
        {
            Primary = primary;
            this.icon = icon;
            this.shortcut = shortcut;
            MinimumSize=new Size(Theme.MinTarget,Theme.MinTarget);
            UseVisualStyleBackColor = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var size = base.GetPreferredSize(proposedSize);
            if (icon == ButtonIcon.Settings) size.Width += Font.Height + Font.Height/3;
            return size;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered=true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered=false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down=e.Button==MouseButtons.Left; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down=false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); down=false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode==Keys.Space) {space=true; Invalidate();} }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (space) {space=false; Invalidate();} }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); space=false; Invalidate(); }

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(Math.Min(bounds.Width,bounds.Height),radius*2);
            path.AddArc(bounds.Left,bounds.Top,d,d,180,90);
            path.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
            path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);
            path.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90);
            path.CloseFigure();
            return path;
        }

        static Color Mix(Color from, Color to, float amount)
        {
            return Color.FromArgb((int)(from.R+(to.R-from.R)*amount),
                (int)(from.G+(to.G-from.G)*amount),(int)(from.B+(to.B-from.B)*amount));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;
            Color background=Parent==null ? AppAppearance.Background(AppAppearance.IsDark) : Parent.BackColor;
            g.Clear(background);
            float u=g.DpiX/96f;
            if (Width<8*u || Height<8*u) return;
            bool card=shortcut!=null, dark=background.GetBrightness()<0.5f;
            bool contrast=SystemInformation.HighContrast, pressed=Enabled && ((hovered && down) || space);
            float inset=1.5f*u, shadow=card ? 3*u : 1.5f*u;
            var bounds=new RectangleF(inset,inset+(pressed ? u : 0),Width-2*inset,Height-2*inset-shadow);
            float radius=(card ? Theme.RadiusLarge : Theme.RadiusMedium)*u;
            Color face=card ? AppAppearance.Surface(dark) : BackColor;
            Color ink=card ? AppAppearance.Ink(dark) : ForeColor;
            if (!Enabled) ink=SystemColors.GrayText;
            if (contrast) {
                face=Primary && !card ? SystemColors.Highlight : SystemColors.Control;
                ink=Enabled ? (Primary && !card ? SystemColors.HighlightText : SystemColors.ControlText) : SystemColors.GrayText;
            }
            Color accent=AppAppearance.Accent(dark);
            if (hovered && Enabled && !contrast) face=Primary && !card ?
                (pressed ? Theme.AccentPressed(dark) : Theme.AccentHover(dark)) : (pressed ? Theme.Selected(dark) : Theme.SurfaceHover(dark));
            Color top=contrast ? face : Mix(face,Color.White,dark ? .045f : .13f);
            Color bottom=contrast ? face : Mix(face,Color.Black,dark ? .025f : .014f);
            Color edge=Theme.Line(dark);
            if (!contrast && Primary && !card) edge=Mix(accent,Color.Black,.09f);
            if (hovered && card && !contrast) edge=Mix(edge,accent,.32f);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            if (!contrast && !pressed) {
                var lower=bounds; lower.Offset(0,shadow*.65f);
                using (var shape=Rounded(lower,radius))
                using (var brush=new SolidBrush(Color.FromArgb(dark ? 30 : 12,Color.Black))) g.FillPath(brush,shape);
            }
            using (var shape=Rounded(bounds,radius))
            using (var brush=new LinearGradientBrush(bounds,top,bottom,LinearGradientMode.Vertical))
            using (var stroke=new Pen(edge,Math.Max(1f,u))) {
                g.FillPath(brush,shape);
                g.DrawPath(stroke,shape);
            }
            if (card) DrawCard(g,bounds,u,ink,dark);
            else {
                var textBounds=Rectangle.Round(bounds);
                if (icon==ButtonIcon.Settings) {
                    int gap=Text.Length==0 ? 0 : (int)(6*u), glyph=(int)((Text.Length==0 ? 19 : 15)*u);
                    int textWidth=TextRenderer.MeasureText(g,Text,Font,Size.Empty,TextFormatFlags.NoPadding).Width;
                    int x=(Width-glyph-gap-textWidth)/2;
                    DrawIcon(g,icon,new RectangleF(x,bounds.Top+(bounds.Height-glyph)/2,glyph,glyph),ink);
                    textBounds.X=x+glyph+gap; textBounds.Width=textWidth+2;
                }
                var flags=TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|
                    TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding;
                if (!ShowKeyboardCues) flags|=TextFormatFlags.HidePrefix;
                TextRenderer.DrawText(g,Text,Font,textBounds,ink,flags);
            }
            if (Focused && ShowFocusCues) {
                var inside=bounds; inside.Inflate(-3*u,-3*u);
                using (var shape=Rounded(inside,Math.Max(1,radius-3*u)))
                using (var focus=new Pen(contrast || (!card && Primary) ? ink : accent,2*u)) g.DrawPath(focus,shape);
            }
        }

        void DrawCard(Graphics g,RectangleF bounds,float u,Color ink,bool dark)
        {
            int left=(int)(bounds.Left+18*u), top=(int)(bounds.Top+15*u), width=Math.Max(1,(int)(bounds.Width-36*u));
            Color color=Theme.Accent(dark);
            var tile=new RectangleF(left,top,31*u,31*u);
            using (var shape=Rounded(tile,Theme.RadiusMedium*u))
            using (var wash=new SolidBrush(SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(dark ? 44 : 23,color)))
                g.FillPath(wash,shape);
            tile.Inflate(-6*u,-6*u); DrawIcon(g,icon,tile,color);
            var flags=TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|
                TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding;
            int textHeight=Math.Max(Font.Height+2,(int)Math.Ceiling(27*u));
            int bottom=(int)(bounds.Bottom-13*u);
            using (var small=Style.Font(9)) {
                int hintHeight=Math.Max((int)Math.Ceiling(21*u),TextRenderer.MeasureText(g,shortcut,small,Size.Empty,flags).Height);
                TextRenderer.DrawText(g,Text,Font,new Rectangle(left,bottom-hintHeight-textHeight-(int)(2*u),width,textHeight),ink,flags);
                TextRenderer.DrawText(g,shortcut,small,new Rectangle(left,bottom-hintHeight,width,hintHeight),
                    AppAppearance.Muted(dark),flags);
            }
        }

        static void DrawIcon(Graphics g,ButtonIcon icon,RectangleF bounds,Color color)
        {
            var saved=g.Save();
            g.TranslateTransform(bounds.X,bounds.Y);
            g.ScaleTransform(bounds.Width/24f,bounds.Height/24f);
            using (var pen=new Pen(color,1.8f) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
                if (icon==ButtonIcon.Region) {
                    g.DrawLines(pen,new[]{new Point(3,9),new Point(3,4),new Point(8,4)});
                    g.DrawLines(pen,new[]{new Point(15,4),new Point(21,4),new Point(21,9)});
                    g.DrawLines(pen,new[]{new Point(3,15),new Point(3,20),new Point(8,20)});
                    g.DrawLines(pen,new[]{new Point(15,20),new Point(21,20),new Point(21,15)});
                    g.DrawLine(pen,8,12,16,12);
                } else if (icon==ButtonIcon.Article) {
                    g.DrawPolygon(pen,new[]{new Point(5,2),new Point(14,2),new Point(19,7),new Point(19,22),new Point(5,22)});
                    g.DrawLines(pen,new[]{new Point(14,2),new Point(14,7),new Point(19,7)});
                    g.DrawLine(pen,8,12,16,12); g.DrawLine(pen,8,16,14,16);
                } else {
                    var teeth=new PointF[32];
                    for (int i=0;i<teeth.Length;i++) {
                        double angle=(i-.5)*Math.PI/16;
                        double r=(i%4<2) ? 10 : 8;
                        teeth[i]=new PointF(12+(float)(r*Math.Cos(angle)),12+(float)(r*Math.Sin(angle)));
                    }
                    g.DrawPolygon(pen,teeth); g.DrawEllipse(pen,8.5f,8.5f,7,7);
                }
            }
            g.Restore(saved);
        }
    }
}
