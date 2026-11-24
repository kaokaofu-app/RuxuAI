using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Jiyue
{
    internal enum AppearanceMode { System, Light, Dark }

    internal class AppearanceForm : Form
    {
        Point? requestedOrigin;
        Size? requestedSize;
        Point revealOrigin;
        Size seedSize;
        bool firstLoad;
        bool clipReveal, fadeReveal;
        Timer revealTimer;
        Stopwatch revealClock;

        public void OpenFrom(Point screenPoint)
        {
            if (!firstLoad) requestedOrigin=screenPoint;
        }
        public void OpenFrom(Rectangle source,Point screenPoint)
        {
            if(firstLoad) return;
            OpenFrom(screenPoint);
            requestedSize=source.Size;
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        internal static int WindowDpi(Control control)
        {
            if(!control.IsHandleCreated) return Math.Max(96,control.DeviceDpi);
            try {var value=GetDpiForWindow(control.Handle); if(value>0) return (int)value;}
            catch(EntryPointNotFoundException) { }
            using(var graphics=control.CreateGraphics()) return (int)Math.Round(graphics.DpiX);
        }

        protected override void OnLoad(EventArgs e)
        {
            // Window-specific Load handlers position and size the form before we clip it.
            base.OnLoad(e);
            if (firstLoad) return;
            firstLoad=true;
            if (IsDisposed || Disposing || !TopLevel || WindowState!=FormWindowState.Normal || !Motion.Enabled) return;
            clipReveal=requestedOrigin.HasValue && Region==null;
            fadeReveal=this is Dialog || this is CaptureStatus;
            if (!clipReveal && !fadeReveal) return;
            if (clipReveal) {
                Point source=requestedOrigin.Value;
                revealOrigin=new Point(source.X-Left,source.Y-Top);
                int dot=Math.Max(1,(int)Math.Round(10.0*WindowDpi(this)/96));
                seedSize=requestedSize ?? new Size(dot,dot);
                // Region clips a WPF child HWND without making the home or settings window layered.
                Region=CapturePopup.Reveal(Size,revealOrigin,seedSize,0);
            }
            if (fadeReveal) Opacity=0;
            revealTimer=new Timer {Interval=15};
            revealTimer.Tick+=AdvanceReveal;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (revealTimer==null) return;
            revealClock=Stopwatch.StartNew();
            revealTimer.Start();
        }

        void AdvanceReveal(object sender,EventArgs e)
        {
            double elapsed=revealClock.Elapsed.TotalMilliseconds;
            if(elapsed >= (clipReveal ? Motion.Normal : Motion.Fast) || !Motion.Enabled) {FinishReveal(); return;}
            if (fadeReveal) Opacity=Motion.EaseOutCubic(elapsed/Motion.Fast);
            if (clipReveal) Region=CapturePopup.Reveal(Size,revealOrigin,seedSize,elapsed/Motion.Normal);
        }

        void FinishReveal()
        {
            if (revealTimer==null) return;
            revealTimer.Dispose();
            revealTimer=null;
            revealClock=null;
            if (!IsDisposed && !Disposing) {
                if (clipReveal) Region=null;
                if (fadeReveal) Opacity=1;
            }
            clipReveal=fadeReveal=false;
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            if(fadeReveal && e.Control is SettingsDialog) FinishReveal();
            base.OnControlAdded(e);
        }

        protected override void WndProc(ref Message message)
        {
            const int ncLeftDown=0x00A1, systemCommand=0x0112, close=0x0010, dpiChanged=0x02E0, showWindow=0x0018;
            if (revealTimer!=null && (message.Msg==ncLeftDown || message.Msg==systemCommand ||
                message.Msg==close || message.Msg==dpiChanged || (message.Msg==showWindow && message.WParam==IntPtr.Zero)))
                FinishReveal();
            base.WndProc(ref message);
            if (message.Msg==0x001A || message.Msg==0x0015 || message.Msg==0x031A)
                AppAppearance.RefreshSystem();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) FinishReveal();
            base.Dispose(disposing);
        }
    }

    internal static class AppAppearance
    {
        const string AppKey=@"HKEY_CURRENT_USER\Software\Jiyue";
        const string WindowsKey=@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        static AppearanceMode mode=ReadMode();
        static bool contrast=SystemInformation.HighContrast;
        static bool dark=ResolveDark();
        static event Action Changed;

        public static AppearanceMode Mode { get { return mode; } }
        public static bool IsDark { get { return dark; } }

        static object Read(string key,string name)
        {
            try { return Registry.GetValue(key,name,null); }
            catch (SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }
        static AppearanceMode ReadMode()
        {
            string saved=Read(AppKey,"Appearance") as string;
            if (saved=="Light") return AppearanceMode.Light;
            if (saved=="Dark") return AppearanceMode.Dark;
            return AppearanceMode.System;
        }
        static bool ResolveDark()
        {
            bool fallback=SystemColors.Window.GetBrightness()<0.5f;
            if (SystemInformation.HighContrast) return fallback;
            if (mode!=AppearanceMode.System) return mode==AppearanceMode.Dark;
            object light=Read(WindowsKey,"AppsUseLightTheme");
            return light is int ? (int)light==0 : fallback;
        }
        public static void SetMode(AppearanceMode value)
        {
            if (value==mode) return;
            Registry.SetValue(AppKey,"Appearance",value.ToString(),RegistryValueKind.String);
            mode=value;
            Refresh(true);
        }
        internal static void RefreshSystem() { Refresh(false); }
        static void Refresh(bool modeChanged)
        {
            bool nextDark=ResolveDark(), nextContrast=SystemInformation.HighContrast;
            if (!modeChanged && dark==nextDark && contrast==nextContrast) return;
            dark=nextDark;
            contrast=nextContrast;
            var notify=Changed;
            if (notify!=null) notify();
        }

        public static void Bind(Form form,Action<bool> apply)
        {
            RefreshSystem();
            Action update=delegate {
                if (form.Disposing || form.IsDisposed) return;
                apply(dark);
                if (form.IsHandleCreated) TitleBar(form);
            };
            Changed+=update;
            form.HandleCreated+=delegate { RefreshSystem(); TitleBar(form); };
            form.Disposed+=delegate { Changed-=update; };
            update();
        }
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int bytes);
        [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr window,string theme,string classes);
        static void TitleBar(Form form)
        {
            int value=dark && !contrast ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle,20,ref value,sizeof(int))!=0)
                DwmSetWindowAttribute(form.Handle,19,ref value,sizeof(int));
        }
        public static void ScrollColors(Control control,bool useDark)
        {
            if (control.IsHandleCreated)
                SetWindowTheme(control.Handle,useDark && !contrast ? "DarkMode_Explorer" : null,null);
        }
        public static Color Background(bool d) {return Theme.Background(d);}
        public static Color Surface(bool d) {return Theme.Surface(d);}
        public static Color Ink(bool d) {return Theme.Ink(d);}
        public static Color Muted(bool d) {return Theme.Muted(d);}
        public static Color Tint(bool d) {return Theme.SurfaceHover(d);}
        public static Color Accent(bool d) {return Theme.Accent(d);}
        public static void ButtonColors(Button button,bool d,bool primary)
        {
            button.BackColor=primary ? Accent(d) : Tint(d);
            button.ForeColor=primary ? Theme.OnAccent :
                (contrast ? SystemColors.ControlText : Ink(d));
        }
    }
}
