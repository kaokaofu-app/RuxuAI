using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Jiyue
{
    internal static class CaptureSource
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Bounds { public int Left,Top,Right,Bottom; }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window,uint command);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Bounds bounds);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window,out Bounds bounds);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window,ref Point point);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder title,int size);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int size);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr window,out uint affinity);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc,int mode);
        [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc,int x,int y,IntPtr previous);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr target,int x,int y,int width,int height,
            IntPtr source,int left,int top,int sourceWidth,int sourceHeight,uint operation);
        [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr window,IntPtr region);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
        [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr target,IntPtr first,IntPtr second,int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);

        static bool Visible(IntPtr window)
        {
            int cloaked;
            return window!=IntPtr.Zero && IsWindowVisible(window) && !IsIconic(window)
                && (DwmGetWindowAttribute(window,14,out cloaked,4)!=0 || cloaked==0);
        }
        static IntPtr RootAt(int x,int y) { return GetAncestor(WindowFromPoint(new Point(x,y)),2); }
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window,ref Point point);
        [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent,Point clientPoint,uint flags);

        internal static List<KeyValuePair<IntPtr,Rectangle>> SnapshotWindows()
        {
            var roots=new List<KeyValuePair<IntPtr,Rectangle>>();
            var seen=new HashSet<IntPtr>();
            for(var w=GetTopWindow(IntPtr.Zero);w!=IntPtr.Zero && seen.Count<1024 && seen.Add(w);w=GetWindow(w,2)) {
                Bounds b;
                if(Visible(w) && GetWindowRect(w,out b) && b.Right>b.Left && b.Bottom>b.Top)
                    roots.Add(new KeyValuePair<IntPtr,Rectangle>(w,Rectangle.FromLTRB(b.Left,b.Top,b.Right,b.Bottom)));
            }
            return roots;
        }

        internal static Rectangle ControlAt(List<KeyValuePair<IntPtr,Rectangle>> roots,Point point,out IntPtr control)
        {
            control=IntPtr.Zero;
            Rectangle visible=Screen.FromPoint(point).Bounds;
            for(int i=0;i<roots.Count;i++) {
                var root=roots[i];
                var box=root.Value;
                if(!box.Contains(point)) continue;
                visible=Rectangle.Intersect(visible,box);
                control=root.Key;
                for(int depth=0;depth<32;depth++) {
                    Point client=point;
                    if(!ScreenToClient(control,ref client)) break;
                    var child=ChildWindowFromPointEx(control,client,5); // skip invisible and transparent children
                    Bounds b;
                    if(child==IntPtr.Zero || child==control || !GetWindowRect(child,out b)) break;
                    var part=Rectangle.Intersect(visible,Rectangle.FromLTRB(b.Left,b.Top,b.Right,b.Bottom));
                    if(!part.Contains(point) || part.Width<3 || part.Height<3) break;
                    control=child; visible=part;
                }
                for(int front=0;front<i;front++) {
                    Rectangle cover=Rectangle.Intersect(visible,roots[front].Value);
                    if(cover.Width<=0 || cover.Height<=0) continue;
                    var horizontal=Rectangle.Empty; var vertical=Rectangle.Empty;
                    if(point.X<cover.Left) horizontal=Rectangle.FromLTRB(visible.Left,visible.Top,cover.Left,visible.Bottom);
                    else if(point.X>=cover.Right) horizontal=Rectangle.FromLTRB(cover.Right,visible.Top,visible.Right,visible.Bottom);
                    if(point.Y<cover.Top) vertical=Rectangle.FromLTRB(visible.Left,visible.Top,visible.Right,cover.Top);
                    else if(point.Y>=cover.Bottom) vertical=Rectangle.FromLTRB(visible.Left,cover.Bottom,visible.Right,visible.Bottom);
                    visible=(long)horizontal.Width*horizontal.Height>(long)vertical.Width*vertical.Height?horizontal:vertical;
                }
                return visible;
            }
            return Rectangle.Empty;
        }
        static uint ProcessOf(IntPtr window)
        {
            uint process;
            return GetWindowThreadProcessId(window,out process)!=0 ? process : 0;
        }
        static string ClassOf(IntPtr window)
        {
            var name=new StringBuilder(128); GetClassName(window,name,name.Capacity); return name.ToString();
        }
        static bool Desktop(string name) { return name=="WorkerW" || name=="Progman"; }
        static bool DocumentWindow(IntPtr window)
        {
            uint process=ProcessOf(window);
            if (process==0 || process==GetCurrentProcessId() || !Visible(window)) return false;
            string name=ClassOf(window);
            return name.Length!=0 && !Desktop(name) && name!="Shell_TrayWnd" && name!="Shell_SecondaryTrayWnd"
                && name!="#32768" && name!="NotifyIconOverflowWindow";
        }
        public static string WindowTitle(IntPtr window)
        {
            if (DocumentWindow(window)) {
                var text=new StringBuilder(256);
                if (GetWindowText(window,text,text.Capacity)>0) {
                    string title=text.ToString().Replace('\r',' ').Replace('\n',' ').Trim();
                    if (title.Length>0) return title;
                }
            }
            return "当前窗口";
        }

        static bool Uncovered(IntPtr target,Rectangle region)
        {
            // Point hits alone miss a small unrelated popup in the middle of a large window.
            var seen=new HashSet<IntPtr>();
            for (var w=GetTopWindow(IntPtr.Zero); w!=IntPtr.Zero && seen.Count<1024 && seen.Add(w); w=GetWindow(w,2)) {
                if (w==target) {
                    int right=region.Right-1,bottom=region.Bottom-1;
                    return RootAt(region.Left,region.Top)==target && RootAt(right,region.Top)==target
                        && RootAt(region.Left,bottom)==target && RootAt(right,bottom)==target
                        && RootAt(region.Left+region.Width/2,region.Top+region.Height/2)==target;
                }
                if (!Visible(w)) continue;
                Bounds b;
                if (!GetWindowRect(w,out b) || region.IntersectsWith(Rectangle.FromLTRB(b.Left,b.Top,b.Right,b.Bottom))) return false;
            }
            return false;
        }
        public static string RegionTitle(Rectangle region)
        {
            try {
                if (region.Width>0 && region.Height>0) {
                    var target=RootAt(region.Left+region.Width/2,region.Top+region.Height/2);
                    if (target!=IntPtr.Zero && Uncovered(target,region)) {
                        if (Desktop(ClassOf(target))) return "Windows 桌面";
                        Bounds b;
                        if (DocumentWindow(target) && (GetWindowLong(target,-20)&0x80020)==0 && GetWindowRect(target,out b)) {
                            var outer=Rectangle.FromLTRB(b.Left,b.Top,b.Right,b.Bottom);
                            if (outer.Contains(region) && WithinWindowRegion(target,region,outer)) return WindowTitle(target);
                        }
                    }
                }
            } catch (ExternalException) { }
            return "屏幕选区";
        }

        static bool WithinWindowRegion(IntPtr window,Rectangle part,Rectangle outer)
        {
            // An explicit window region can have holes revealing a second application's pixels.
            IntPtr shape=CreateRectRgn(0,0,0,0);
            if (shape==IntPtr.Zero) return false;
            try {
                if (GetWindowRgn(window,shape)==0) return true; // A normal rectangular window has no explicit region.
                part.Offset(-outer.Left,-outer.Top);
                IntPtr diff=CreateRectRgn(part.Left,part.Top,part.Right,part.Bottom);
                if (diff==IntPtr.Zero) return false;
                try { return CombineRgn(diff,diff,shape,4)==1; } // RGN_DIFF must be NULLREGION.
                finally { DeleteObject(diff); }
            } finally { DeleteObject(shape); }
        }
        static bool PreviewBounds(IntPtr window,Rectangle expected,out Rectangle part)
        {
            part=Rectangle.Empty;
            if (window!=GetForegroundWindow() || !DocumentWindow(window)) return false;
            const int layered=0x80000,transparent=0x20;
            uint affinity;
            if ((GetWindowLong(window,-20)&(layered|transparent))!=0
                || (GetWindowDisplayAffinity(window,out affinity) && affinity!=0)) return false;
            Bounds b,c;
            if (!GetWindowRect(window,out b) || !GetClientRect(window,out c)) return false;
            var outer=Rectangle.FromLTRB(b.Left,b.Top,b.Right,b.Bottom);
            var desktop=SystemInformation.VirtualScreen;
            if (Rectangle.Intersect(outer,desktop)!=Rectangle.Intersect(expected,desktop)) return false;
            var topLeft=new Point(c.Left,c.Top); var bottomRight=new Point(c.Right,c.Bottom);
            if (!ClientToScreen(window,ref topLeft) || !ClientToScreen(window,ref bottomRight)) return false;
            var client=Rectangle.FromLTRB(topLeft.X,topLeft.Y,bottomRight.X,bottomRight.Y);
            part=Rectangle.Intersect(Rectangle.Intersect(client,outer),Screen.FromRectangle(client).WorkingArea);
            if (part.Width<24 || part.Height<24) return false;
            part.Inflate(-4,-4);
            return WithinWindowRegion(window,part,outer) && Uncovered(window,part);
        }
        static Bitmap ReducedImage(IntPtr sourceDc,Rectangle part)
        {
            double scale=Math.Min(1,Math.Min(800.0/part.Width,500.0/part.Height));
            int width=Math.Max(1,(int)(part.Width*scale)),height=Math.Max(1,(int)(part.Height*scale));
            var image=new Bitmap(width,height,PixelFormat.Format24bppRgb);
            try {
                using (var graphics=Graphics.FromImage(image)) {
                    IntPtr destination=graphics.GetHdc();
                    try {
                        SetStretchBltMode(destination,4); // HALFTONE downsampling.
                        SetBrushOrgEx(destination,0,0,IntPtr.Zero);
                        if (!StretchBlt(destination,0,0,width,height,sourceDc,part.X,part.Y,part.Width,part.Height,0x00CC0020))
                            throw new ExternalException("无法生成窗口预览。");
                    } finally { graphics.ReleaseHdc(destination); }
                }
                return image;
            } catch { image.Dispose(); throw; }
        }
        internal static IntPtr SelectionWindow(Rectangle area)
        {
            return RootAt(area.Left + area.Width/2, area.Top + area.Height/2);
        }

        internal static bool CanReadSelection(IntPtr window, Rectangle area)
        {
            Bounds outer,client;
            uint affinity;
            if (window != GetForegroundWindow() || !DocumentWindow(window) ||
                (GetWindowLong(window,-20) & 0x80020) != 0 ||
                (GetWindowDisplayAffinity(window,out affinity) && affinity != 0) ||
                !GetWindowRect(window,out outer) || !GetClientRect(window,out client)) return false;
            var first = new Point(client.Left,client.Top);
            var last = new Point(client.Right,client.Bottom);
            return ClientToScreen(window,ref first) && ClientToScreen(window,ref last) &&
                Rectangle.FromLTRB(first.X,first.Y,last.X,last.Y).Contains(area) &&
                WithinWindowRegion(window,area,Rectangle.FromLTRB(outer.Left,outer.Top,outer.Right,outer.Bottom)) &&
                Uncovered(window,area);
        }
        public static byte[] VisiblePagePreview(IntPtr window,Rectangle screenBounds)
        {
            try {
                Rectangle part,after;
                uint process=ProcessOf(window);
                if (!PreviewBounds(window,screenBounds,out part)) return null;
                IntPtr dc=GetDC(IntPtr.Zero);
                if (dc==IntPtr.Zero) return null;
                Bitmap image;
                try { image=ReducedImage(dc,part); }
                finally { ReleaseDC(IntPtr.Zero,dc); }
                using (image) {
                    if (ProcessOf(window)!=process || !PreviewBounds(window,screenBounds,out after) || after!=part) return null;
                    using (var stream=new MemoryStream()) { image.Save(stream,ImageFormat.Png); return stream.ToArray(); }
                }
            } catch (Exception) { return null; } // Reading the document does not depend on its optional preview.
        }
    }
}
