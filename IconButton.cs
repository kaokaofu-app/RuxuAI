using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Xml;
using M = System.Windows.Media;

namespace Jiyue
{
    internal sealed class IconButton : Button
    {
        readonly ToolTip hint = new ToolTip();
        readonly Timer copied = new Timer { Interval = 1200 };
        GraphicsPath glyph;
        bool hover, mouseDown, spaceDown;

        public IconButton(string icon, string label, string shortcut = null)
        {
            AutoSize = false;
            MinimumSize = Size = new Size(32,32);
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.PushButton;
            SetIcon(icon);
            SetLabel(label,shortcut);
            copied.Tick += delegate { copied.Stop(); Invalidate(); };
        }

        public void SetLabel(string label, string shortcut = null)
        {
            AccessibleName = label;
            AccessibleDescription = String.IsNullOrEmpty(shortcut) ? label : label+"（"+shortcut+"）";
            hint.SetToolTip(this,AccessibleDescription);
        }

        public void SetIcon(string icon)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var stream=typeof(IconButton).Assembly.GetManifestResourceStream("Fluent."+icon+".svg")) {
                if (stream==null) throw new ArgumentException("未嵌入的图标："+icon,"icon");
                document.Load(stream);
            }
            var shape = new GraphicsPath(FillMode.Winding);
            try {
                foreach (XmlElement path in document.GetElementsByTagName("path")) {
                    var geometry=M.Geometry.Parse(path.GetAttribute("d")).GetFlattenedPathGeometry(.06,M.ToleranceType.Absolute);
                    foreach (var figure in geometry.Figures) {
                        shape.StartFigure();
                        var prev=new PointF((float)figure.StartPoint.X,(float)figure.StartPoint.Y);
                        foreach (var segment in figure.Segments) {
                            var poly=segment as M.PolyLineSegment;
                            if (poly!=null) foreach (var point in poly.Points) {
                                var next=new PointF((float)point.X,(float)point.Y);
                                shape.AddLine(prev,next); prev=next;
                            }
                            else {
                                var line=(M.LineSegment)segment;
                                var next=new PointF((float)line.Point.X,(float)line.Point.Y);
                                shape.AddLine(prev,next); prev=next;
                            }
                        }
                        if (figure.IsClosed) shape.CloseFigure();
                    }
                }
            } catch { shape.Dispose(); throw; }
            if (glyph!=null) glyph.Dispose();
            glyph=shape;
            Invalidate();
        }

        public void ShowCopied() { copied.Stop(); copied.Start(); Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover=true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover=false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); mouseDown=e.Button==MouseButtons.Left; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); mouseDown=false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); mouseDown=false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode==Keys.Space) {spaceDown=true; Invalidate();} }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (spaceDown) {spaceDown=false; Invalidate();} }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); spaceDown=false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;
            bool dark=AppAppearance.IsDark;
            float scale=g.DpiX/96f;
            g.Clear(Parent==null ? Theme.Surface(dark) : Parent.BackColor);
            if (Width<4*scale || Height<4*scale) return;
            var bounds=new RectangleF(scale,scale,Width-2*scale,Height-2*scale);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            using (var shape=GlassButton.Rounded(bounds,8*scale)) {
                bool pressed=Enabled && ((mouseDown && hover) || spaceDown);
                if (Enabled && (hover || pressed)) {
                    using (var background=new SolidBrush(pressed ? Theme.Selected(dark) : Theme.SurfaceHover(dark)))
                        g.FillPath(background,shape);
                }
                if (Focused && ShowFocusCues) using (var ring=new Pen(Theme.Accent(dark),2*scale)) g.DrawPath(ring,shape);
                var color=!Enabled ? SystemColors.GrayText : copied.Enabled ? Theme.Success(dark) : Theme.Ink(dark);
                if (SystemInformation.HighContrast && Enabled)
                    color=pressed ? SystemColors.HighlightText : SystemColors.WindowText;
                float size=Math.Min(Math.Min(bounds.Width,bounds.Height),18*scale);
                var saved=g.Save();
                g.TranslateTransform((Width-size)/2,(Height-size)/2);
                g.ScaleTransform(size/20,size/20);
                if (copied.Enabled) {
                    using (var pen=new Pen(color,2) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round})
                        g.DrawLines(pen,new[]{new Point(3,10),new Point(8,15),new Point(17,5)});
                } else using (var brush=new SolidBrush(color)) g.FillPath(brush,glyph);
                g.Restore(saved);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) {
                hint.Dispose(); copied.Dispose();
                if (glyph!=null) { glyph.Dispose(); glyph=null; }
            }
            base.Dispose(disposing);
        }
    }
}
