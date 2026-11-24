using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Accessibility;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Jiyue
{
    internal static class Snipper
    {
        public static IDisposable Begin(Action<Bitmap, Rectangle, Point, bool> done, Action cancelled, string hint = "移动鼠标自动识别 · 单击 解释 · 拖动 自由框选 · Shift+松开 先提问 · Esc 取消")
        {
            if (done == null) throw new ArgumentNullException("done");
            var session = new SelectionSession(done, cancelled, hint);
            try
            {
                session.Show();
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private sealed class SelectionSession : IDisposable
        {
            private readonly Action<Bitmap, Rectangle, Point, bool> done;
            private readonly Action cancelled;
            private readonly List<Overlay> windows = new List<Overlay>();
            private Point anchor;
            private Point? hover;
            private IntPtr hoveredControl;
            private Rectangle nativeBounds;
            private Rectangle elementBounds;
            private List<KeyValuePair<IntPtr,Rectangle>> roots;
            private readonly Timer refine = new Timer { Interval=55 };
            private bool needsRefinement, pressed;
            private static int providerBusy;
            private bool dragging;
            private bool closed;
            public Rectangle Selection { get; private set; }
            public bool ShowHint { get { return !pressed; } }
            public bool Dragging { get {return dragging;} }
            public bool Pressed { get {return pressed;} }

            public readonly string Hint;
            public SelectionSession(Action<Bitmap, Rectangle, Point, bool> done, Action cancelled, string hint)
            {
                this.done = done;
                this.cancelled = cancelled;
                Hint = hint;
                refine.Tick+=Refine;
            }

            public void Show()
            {
                // Capture all monitors before displaying any overlay.
                roots=CaptureSource.SnapshotWindows();
                foreach (var screen in Screen.AllScreens)
                    windows.Add(new Overlay(this, screen.Bounds));

                Point cursor = Cursor.Position;
                Overlay focus = null;
                foreach (var window in windows)
                {
                    window.Show();
                    if (window.ScreenArea.Contains(cursor)) focus = window;
                }
                if (focus != null) focus.Activate();
                Update(cursor);
                refine.Start();
            }

            [DllImport("oleacc.dll")]
            static extern int AccessibleObjectFromWindow(IntPtr hwnd,uint objectId,ref Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible);

            [DllImport("user32.dll")]
            static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

            static Rectangle ElementAt(IntPtr window,Point point,Rectangle bounds)
            {
                IntPtr previous=IntPtr.Zero;
                try {
                    try { previous=SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { }
                    return ElementBounds(window,point,bounds);
                } finally { if(previous!=IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
            }

            static Rectangle ElementBounds(IntPtr window,Point point,Rectangle bounds)
            {
                Rectangle candidate=bounds;
                try { candidate=AccessibleBounds(window,point,bounds); }
                catch(COMException) { }
                catch(NotImplementedException) { }
                if(candidate!=bounds) return candidate;
                // Ask the underlying window directly: point lookup on the desktop would hit our overlay.
                var node=AutomationElement.FromHandle(window);
                var walker=TreeWalker.ControlViewWalker;
                var clock=System.Diagnostics.Stopwatch.StartNew();
                for(int depth=0;node!=null && depth<16 && clock.ElapsedMilliseconds<140;depth++) {
                    var child=walker.GetFirstChild(node);
                    node=null;
                    for(int i=0;child!=null && i<128 && clock.ElapsedMilliseconds<140;i++,child=walker.GetNextSibling(child)) {
                        var b=child.Current.BoundingRectangle;
                        if(b.IsEmpty || !b.Contains(point.X,point.Y)) continue;
                        var part=Rectangle.Intersect(candidate,Rectangle.FromLTRB((int)Math.Floor(b.Left),(int)Math.Floor(b.Top),
                            (int)Math.Ceiling(b.Right),(int)Math.Ceiling(b.Bottom)));
                        if(part.Width<3 || part.Height<3) continue;
                        candidate=part; node=child; break;
                    }
                }
                return candidate;
            }
            static Rectangle AccessibleBounds(IntPtr window,Point point,Rectangle bounds)
            {
                var iid=typeof(IAccessible).GUID;
                IAccessible node;
                if(AccessibleObjectFromWindow(window,0xfffffffc,ref iid,out node)!=0) return bounds;
                for(int depth=0;node!=null && depth<24;depth++) {
                    object hit=node.accHitTest(point.X,point.Y);
                    if(hit==null) break;
                    var child=hit as IAccessible;
                    int x,y,w,h;
                    (child??node).accLocation(out x,out y,out w,out h,child==null?hit:0);
                    var part=Rectangle.Intersect(bounds,new Rectangle(x,y,w,h));
                    if(!part.Contains(point) || part.Width<3 || part.Height<3) break;
                    bounds=part;
                    if(child==null || Object.ReferenceEquals(child,node)) break;
                    node=child;
                }
                return bounds;
            }

            private async void Refine(object sender,EventArgs e)
            {
                if(!needsRefinement || pressed || closed || hoveredControl==IntPtr.Zero ||
                    System.Threading.Interlocked.CompareExchange(ref providerBusy,1,0)!=0) return;
                needsRefinement=false;
                Point point=hover.Value; IntPtr control=hoveredControl; Rectangle bounds=nativeBounds;
                try {
                    var result=await Task.Run(()=>ElementAt(control,point,bounds));
                    if(!closed && !pressed && hover==point && hoveredControl==control && result!=bounds) {
                        elementBounds=result;
                        if(Selection.Contains(result)) SetSelection(result);
                    }
                } catch(COMException) { }
                  catch(InvalidCastException) { }
                  catch(ArgumentException) { }
                  catch(UnauthorizedAccessException) { }
                  catch(NotImplementedException) { }
                  catch(InvalidOperationException) { }
                  catch(ElementNotAvailableException) { }
                finally {System.Threading.Interlocked.Exchange(ref providerBusy,0);}
            }

            public void Start(Point location)
            {
                Update(location);
                anchor = location;
                pressed = true;
                foreach (var window in windows) window.Invalidate();
            }

            public void Update(Point location)
            {
                if (closed) return;
                if(!pressed) {
                    if(hover==location) return;
                    hover=location;
                    IntPtr control;
                    var bounds=CaptureSource.ControlAt(roots,location,out control);
                    if(control!=hoveredControl || bounds!=nativeBounds || !elementBounds.Contains(location)) elementBounds=Rectangle.Empty;
                    var suggested=bounds;
                    foreach(var overlay in windows) if(overlay.ScreenArea.Contains(location)) {
                        suggested=overlay.Suggest(location,bounds);
                        break;
                    }
                    if(!elementBounds.IsEmpty && suggested.Contains(elementBounds)) suggested=elementBounds;
                    SetSelection(suggested);
                    hoveredControl=control; nativeBounds=bounds; needsRefinement=true;
                    return;
                }
                if(!dragging) {
                    Size threshold=SystemInformation.DragSize;
                    if(new Rectangle(anchor.X-threshold.Width/2,anchor.Y-threshold.Height/2,
                        threshold.Width,threshold.Height).Contains(location)) return;
                    dragging=true;
                }
                SetSelection(Rectangle.FromLTRB(Math.Min(anchor.X, location.X),
                    Math.Min(anchor.Y, location.Y), Math.Max(anchor.X, location.X) + 1,
                    Math.Max(anchor.Y, location.Y) + 1));
            }

            void SetSelection(Rectangle bounds)
            {
                Rectangle previous = Selection;
                if(previous==bounds) return;
                Selection=bounds;
                foreach (var window in windows)
                {
                    if (previous.IsEmpty)
                    {
                        window.Invalidate();
                        continue;
                    }
                    Rectangle dirty = Rectangle.Union(previous, Selection);
                    dirty.Inflate(window.ScalePx(104),window.ScalePx(34));
                    dirty.Offset(-window.ScreenArea.X, -window.ScreenArea.Y);
                    window.Invalidate(dirty);
                }
            }

            public void Finish(Point location)
            {
                if (!pressed || closed) return;
                bool questionFirst=(Control.ModifierKeys & Keys.Shift)!=0;
                Update(location);
                pressed=dragging=false;
                if (Selection.Width < 3 || Selection.Height < 3)
                {
                    Selection = Rectangle.Empty;
                    hover=null; Update(location);
                    foreach (var window in windows) window.Invalidate();
                    return;
                }

                Rectangle bounds = Selection;
                Bitmap result = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (var graphics = Graphics.FromImage(result))
                    {
                        graphics.Clear(Color.Black); // Gaps between differently aligned monitors.
                        foreach (var window in windows)
                        {
                            Rectangle overlap = Rectangle.Intersect(bounds, window.ScreenArea);
                            if (overlap.Width <= 0 || overlap.Height <= 0) continue;
                            var source = new Rectangle(overlap.X - window.ScreenArea.X,
                                overlap.Y - window.ScreenArea.Y, overlap.Width, overlap.Height);
                            var target = new Rectangle(overlap.X - bounds.X, overlap.Y - bounds.Y,
                                overlap.Width, overlap.Height);
                            graphics.DrawImage(window.Frame, target, source, GraphicsUnit.Pixel);
                        }
                    }
                }
                catch
                {
                    result.Dispose();
                    Cancel();
                    throw;
                }
                Dispose();
                done(result, bounds, location, questionFirst);
            }

            public void Cancel()
            {
                if (closed) return;
                Dispose();
                if (cancelled != null) cancelled();
            }

            public void Dispose()
            {
                if (closed) return;
                closed = true;
                refine.Dispose();
                foreach (var window in windows)
                    window.Dispose();
                windows.Clear();
            }
        }

        private sealed class Overlay : Form
        {
            private readonly SelectionSession session;
            public Rectangle ScreenArea { get; private set; }
            public readonly Bitmap Frame;
            readonly VisualSelection visual;
            readonly System.Diagnostics.Stopwatch hintAge=new System.Diagnostics.Stopwatch();
            readonly Timer hintTimer=new Timer {Interval=40};
            bool bottomHint;
            Rectangle hintBounds;
            int hintAlpha=255;
            internal int ScalePx(int logical) {return Math.Max(1,(int)Math.Round(logical*AppearanceForm.WindowDpi(this)/96d));}

            public Rectangle Suggest(Point location,Rectangle bounds)
            {
                if(bounds.IsEmpty) bounds=ScreenArea;
                if(bounds.Width<280 && bounds.Height<95) return bounds;
                bounds.Offset(-ScreenArea.X,-ScreenArea.Y);
                var local=new Point(location.X-ScreenArea.X,location.Y-ScreenArea.Y);
                var selected=visual.Find(local,bounds);
                selected.Offset(ScreenArea.Location);
                return selected;
            }

            public Overlay(SelectionSession session, Rectangle screenArea)
            {
                this.session = session;
                ScreenArea = screenArea;
                AutoScaleMode = AutoScaleMode.None;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                TopMost = true;
                Cursor = Cursors.Cross;
                Bounds = screenArea;
                DoubleBuffered = true;
                Font=Style.Font(10);
                AccessibleName="框选内容，方向键移动，空格开始拖选，Enter 确认，Esc 取消";
                hintTimer.Tick+=delegate {
                    if(hintAge.ElapsedMilliseconds<3000) return;
                    hintAlpha=Motion.Enabled?Math.Max(0,255-(int)((hintAge.ElapsedMilliseconds-3000)*255/Motion.Normal)):0;
                    Invalidate(HintBand(bottomHint)); if(hintAlpha==0) hintTimer.Stop();
                };
                Shown+=delegate {hintAge.Start();hintTimer.Start();};

                Frame = new Bitmap(screenArea.Width, screenArea.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (var g = Graphics.FromImage(Frame))
                        g.CopyFromScreen(screenArea.Location, Point.Empty, screenArea.Size);
                    visual=new VisualSelection(Frame);
                }
                catch
                {
                    Frame.Dispose();
                    throw;
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var area = session.Selection;
                area.Offset(-ScreenArea.X, -ScreenArea.Y);
                g.DrawImageUnscaled(Frame, 0, 0);
                GraphicsState beforeMask = g.Save();
                if (!session.Selection.IsEmpty) g.ExcludeClip(area);
                using (var veil = new SolidBrush(Color.FromArgb(100, Color.Black)))
                    g.FillRectangle(veil, ClientRectangle);
                g.Restore(beforeMask);
                if (!session.Selection.IsEmpty && area.IntersectsWith(ClientRectangle)) {
                    using(var border=new Pen(Theme.Accent(AppAppearance.IsDark),ScalePx(2)) {Alignment=PenAlignment.Inset}) {
                        g.DrawRectangle(border,area.X,area.Y,area.Width-1,area.Height-1);
                        border.Width=ScalePx(3);
                        int len=Math.Min(ScalePx(8),Math.Min(area.Width,area.Height)/2);
                        foreach(int dx in new[]{1,-1}) foreach(int dy in new[]{1,-1}) {
                            int x=dx==1?area.Left:area.Right-1,y=dy==1?area.Top:area.Bottom-1;
                            g.DrawLines(border,new[]{new Point(x+dx*len,y),new Point(x,y),new Point(x,y+dy*len)});
                        }
                    }
                    if(session.Dragging && ClientRectangle.Contains(new Point(area.Right-1,area.Bottom-1))) {
                        string text=area.Width+" × "+area.Height;
                        var s=TextRenderer.MeasureText(text,Font);s.Width+=ScalePx(12);s.Height+=ScalePx(6);
                        var box=new Rectangle(Math.Max(0,Math.Min(Width-s.Width,area.Right-s.Width)),area.Bottom+ScalePx(4),s.Width,s.Height);
                        if(box.Bottom>Height) box.Y=Math.Max(0,area.Bottom-s.Height-ScalePx(4));
                        using(var path=GlassButton.Rounded(box,ScalePx(6)))
                        using(var fill=new SolidBrush(Theme.Surface(AppAppearance.IsDark))) g.FillPath(fill,path);
                        TextRenderer.DrawText(g,text,Font,box,Theme.Ink(AppAppearance.IsDark),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
                    }
                }
                if(session.ShowHint && hintAlpha>0) DrawHint(g);
            }
            Rectangle HintBand(bool bottom) {
                int h=Math.Min(Height,Math.Max(ScalePx(90),hintBounds.Height+ScalePx(20)));
                return new Rectangle(0,bottom?Height-h:0,Width,h);
            }
            void DrawHint(Graphics g)
            {
                string[] parts=session.Hint.Split(new[]{" · "},StringSplitOptions.None);
                float pad=ScalePx(8),gap=ScalePx(8),line=Font.GetHeight(g)+pad;
                float available=Math.Max(1,ClientSize.Width-4*pad),x=0,y=0,widest=0;
                var positions=new PointF[parts.Length];
                for(int i=0;i<parts.Length;i++) {
                    float width=Math.Min(available,g.MeasureString(parts[i],Font).Width);
                    if(x>0 && x+width>available) {x=0;y+=line;}
                    positions[i]=new PointF(x,y);
                    widest=Math.Max(widest,x+width);x+=width+gap;
                }
                float height=y+line+pad,widthWithPadding=widest+2*pad;
                float left=Math.Max(0,(ClientSize.Width-widthWithPadding)/2);
                var box=new RectangleF(left,bottomHint?Math.Max(0,Height-height-2*pad):2*pad,widthWithPadding,height);
                hintBounds=Rectangle.Ceiling(box);
                g.SmoothingMode=SmoothingMode.AntiAlias;
                using(var backdrop=new SolidBrush(Color.FromArgb(SystemInformation.HighContrast ? hintAlpha : hintAlpha*225/255,Theme.Background(true))))
                using(var text=new SolidBrush(Color.FromArgb(hintAlpha,Theme.Ink(true))))
                using(var rim=new Pen(Color.FromArgb(hintAlpha,Theme.Muted(true))))
                using(var path=GlassButton.Rounded(box,ScalePx(10))) {
                    g.FillPath(backdrop,path);
                    for(int i=0;i<parts.Length;i++) {
                        string s=parts[i];
                        x=box.X+pad+positions[i].X;y=box.Y+pad/2+positions[i].Y;
                        int split=s.IndexOf(' ');
                        if(split>0) {
                            float keyWidth=g.MeasureString(s.Substring(0,split),Font).Width;
                            using(var cap=GlassButton.Rounded(new RectangleF(x-1,y,keyWidth+2,line),ScalePx(4)))g.DrawPath(rim,cap);
                        }
                        g.DrawString(s,Font,text,x,y+pad/2);
                    }
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) { session.Cancel(); return; }
                if (e.Button != MouseButtons.Left) return;
                Activate();
                Capture = true;
                session.Start(PointToScreen(e.Location));
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                var near=hintBounds;near.Inflate(ScalePx(80),ScalePx(80));
                if(hintAlpha>0 && !hintBounds.IsEmpty && near.Contains(e.Location)) {
                    Invalidate(HintBand(bottomHint));bottomHint=!bottomHint;Invalidate(HintBand(bottomHint));
                    hintBounds=Rectangle.Empty;
                }
                session.Update(PointToScreen(e.Location));
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                Point location = PointToScreen(e.Location);
                Capture = false;
                session.Finish(location);
            }

            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                var key=keyData&Keys.KeyCode;Point p=Cursor.Position;
                if(key==Keys.Escape) {session.Cancel();return true;}
                if(key==Keys.Enter) {if(!session.Pressed)session.Start(p);session.Finish(p);return true;}
                if(key==Keys.Space) {if(session.Pressed)session.Finish(p);else session.Start(p);return true;}
                int step=(keyData&Keys.Shift)!=0?10:1;
                switch(key) {
                    case Keys.Left:p.X-=step;break;case Keys.Right:p.X+=step;break;
                    case Keys.Up:p.Y-=step;break;case Keys.Down:p.Y+=step;break;
                    default:return base.ProcessCmdKey(ref msg,keyData);
                }
                Cursor.Position=p;session.Update(Cursor.Position);return true;
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                base.OnFormClosed(e);
                session.Cancel();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) {hintTimer.Dispose();if(Frame!=null) Frame.Dispose();}
                base.Dispose(disposing);
            }
        }
    }
}
