using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jiyue
{
    internal static class CapturePopup
    {
        public static Point TriggerPoint(Control trigger)
        {
            Point pointer=Cursor.Position;
            if (trigger==null || trigger.IsDisposed || !trigger.IsHandleCreated || !trigger.Visible) return pointer;
            Rectangle bounds=trigger.RectangleToScreen(trigger.ClientRectangle);
            return bounds.Contains(pointer) ? pointer : new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);
        }

        public static void ShowFrom(AppearanceForm window,Form owner,Control trigger)
        {
            window.OpenFrom(TriggerPoint(trigger));
            if (owner==null) window.Show(); else window.Show(owner);
        }

        public static Region Reveal(Size size, Point origin, Size seedSize, double progress)
        {
            int w = Math.Max(1,Math.Min(seedSize.Width,size.Width)), h = Math.Max(1,Math.Min(seedSize.Height,size.Height));
            int x = Math.Max(0, Math.Min(size.Width - w, origin.X - w / 2));
            int y = Math.Max(0, Math.Min(size.Height - h, origin.Y - h / 2));
            double upper = Motion.EaseOutCubic(progress / 0.78);
            double lower = Motion.EaseOutCubic((progress - 0.19) / 0.81);
            var topLeft = new PointF((float)(x * (1 - upper)), (float)(y * (1 - upper)));
            var topRight = new PointF((float)(x + w + (size.Width - x - w) * upper), topLeft.Y);
            var bottomLeft = new PointF((float)(x * (1 - lower)), (float)(y + h + (size.Height - y - h) * lower));
            var bottomRight = new PointF((float)(x + w + (size.Width - x - w) * lower), bottomLeft.Y);
            float midY = (topLeft.Y + bottomLeft.Y) / 2;

            // The top edge leads. Curved sides keep the sheet joined to the release point as it opens.
            using (var outline = new GraphicsPath())
            {
                outline.AddLine(topLeft, topRight);
                outline.AddBezier(topRight, new PointF(topRight.X, midY), new PointF(bottomRight.X, midY), bottomRight);
                outline.AddLine(bottomRight, bottomLeft);
                outline.AddBezier(bottomLeft, new PointF(bottomLeft.X, midY), new PointF(topLeft.X, midY), topLeft);
                outline.CloseFigure();
                return new Region(outline);
            }
        }

        internal static Rectangle Stagger(Rectangle target,Form current)
        {
            var work=Screen.FromRectangle(target).WorkingArea;
            int step=Math.Max(1,AppearanceForm.WindowDpi(current)*24/96);
            for(int i=0;i<=Application.OpenForms.Count;i++) {
                foreach(int direction in new[]{1,-1}) {
                    int dx=i*step*direction;
                    var location=new Point(Math.Max(work.Left,Math.Min(work.Right-target.Width,target.X+dx)),
                        Math.Max(work.Top,Math.Min(work.Bottom-target.Height,target.Y+dx)));
                    bool occupied=false;
                    foreach(Form open in Application.OpenForms)
                        if(open!=current && open is Dialog && open.WindowState==FormWindowState.Normal &&
                            Math.Abs(open.Left-location.X)<step/2 && Math.Abs(open.Top-location.Y)<step/2) {occupied=true;break;}
                    if(!occupied) return new Rectangle(location,target.Size);
                }
            }
            return target;
        }

        public static Rectangle Place(Rectangle selection, Point releasePoint, Rectangle area, Size requested)
        {
            var size = new Size(Math.Min(requested.Width, area.Width), Math.Min(requested.Height, area.Height));
            var best = Rectangle.Empty;
            long nearest = long.MaxValue, leastOverlap = long.MaxValue;
            // Both opening directions include the release pixel, even for a drag ending at the bottom right.
            var leftEdges = new[] { releasePoint.X, releasePoint.X - size.Width + 1 };
            var topEdges = new[] { releasePoint.Y, releasePoint.Y - size.Height + 1 };

            // Try each corner at the endpoint. Among equally close placements, expose more of the capture.
            foreach (int left in leftEdges)
            foreach (int top in topEdges)
            {
                int x = Math.Max(area.Left, Math.Min(area.Right - size.Width, left));
                int y = Math.Max(area.Top, Math.Min(area.Bottom - size.Height, top));
                long dx = (long)x - left, dy = (long)y - top;
                long distance = dx * dx + dy * dy;
                var candidate = new Rectangle(x, y, size.Width, size.Height);
                var common = Rectangle.Intersect(candidate, selection);
                long overlap = (long)common.Width * common.Height;
                if (distance < nearest || (distance == nearest && overlap < leastOverlap))
                {
                    best = candidate;
                    nearest = distance;
                    leastOverlap = overlap;
                }
            }
            return best;
        }
    }
}
