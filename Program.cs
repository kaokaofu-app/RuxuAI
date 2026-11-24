using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: System.Reflection.AssemblyTitle("如许AI")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyProduct("如许AI")]
[assembly: System.Reflection.AssemblyDescription("框选屏幕或读取整页，在原处查看 AI 文字解释并追问")]

namespace Jiyue
{
    internal static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            using (var mutex = new Mutex(false, "Local\\JiyueReader_693A"))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\JiyueReader_Show_693A"))
            using (var stopping = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\JiyueReader_Closing_693A"))
            {
                if (!AcquireOrNotify(mutex,show,stopping)) return;
                try
                {
                    stopping.Reset();
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var app = new ReaderApp(show,stopping)) Application.Run(app);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        static bool TryAcquire(Mutex mutex,int wait)
        {
            try { return mutex.WaitOne(wait); }
            catch (AbandonedMutexException) { return true; }
        }
        static bool AcquireOrNotify(Mutex mutex,EventWaitHandle show,EventWaitHandle stopping)
        {
            if (TryAcquire(mutex,0)) return true;
            var elapsed=Stopwatch.StartNew();
            for (;;)
            {
                if (stopping.WaitOne(0)) {
                    if (TryAcquire(mutex,100)) return true;
                    continue;
                }
                bool delivered;
                if (KeysWindow.NotifyExistingInstance(out delivered)) return false;
                // The owner may still be starting, already be exiting, or be an older version.
                if (TryAcquire(mutex,100)) return true;
                if (delivered || elapsed.ElapsedMilliseconds>=3000) {
                    show.Set(); // Older versions poll this event instead of replying to the window message.
                    if (TryAcquire(mutex,300)) return true;
                    if (!stopping.WaitOne(0)) return false;
                }
            }
        }
    }

    internal sealed class ReaderApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h,int command);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int size);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        readonly uint pid = (uint)Process.GetCurrentProcess().Id;
        readonly EventWaitHandle show,stopping;
        readonly KeysWindow keys;
        readonly NotifyIcon tray;
        readonly List<Home> homes=new List<Home>();
        readonly List<Dialog> talks=new List<Dialog>();
        readonly List<Form> hiddenForCapture=new List<Form>();
        Home captureOrigin;
        readonly Icon trayIcon;
        int pendingHomes;
        readonly List<ConversationRecord> recent=new List<ConversationRecord>();
        readonly HashSet<string> removedOpen=new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> removedWhileLoading=new HashSet<string>(StringComparer.Ordinal);
        readonly System.Windows.Forms.Timer saveTimer=new System.Windows.Forms.Timer {Interval=750};
        Task<List<ConversationRecord>> loadTask;
        Task writeTask;
        bool saveNeeded;
        string configError,loadWarning,saveWarning;
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 180 };
        ModelSettings settings;
        SettingsDialog settingsWindow;
        Form settingsHost;
        bool capturing, configuring, quitting;
        CancellationTokenSource articleRead;
        volatile bool finishScroll;
        bool collectingScroll;
        IntPtr previous;

        public ReaderApp(EventWaitHandle showEvent,EventWaitHandle stoppingEvent)
        {
            show = showEvent;
            stopping = stoppingEvent;
            try { settings = ModelSettings.Load(); }
            catch (Exception e) { settings = new ModelSettings(); configError = e.Message; }
            loadTask=Task.Run(new Func<List<ConversationRecord>>(ConversationStore.Load));
            saveTimer.Tick+=delegate { SaveHistoryTick(); };
            keys = new KeysWindow(() => StartRegion(), () => StartPage(), ()=>StartPage(null,true),OpenHome, delegate {
                if (quitting) return false;
                pendingHomes++; // Acknowledge the second launch before constructing a window.
                return true;
            });
            var menu = BuildTrayMenu();
            trayIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray = new NotifyIcon { Icon=trayIcon, Text="如许AI · 阅读助手", ContextMenuStrip=menu, Visible=true };
            // NotifyIcon emits MouseClick on the first release and MouseDoubleClick for the second.
            tray.MouseClick+=delegate(object sender,MouseEventArgs e) { if (e.Button==MouseButtons.Left) OpenHome(); };
            tick.Tick += delegate {
                if (loadTask!=null && loadTask.IsCompleted) FinishHistoryLoad();
                var h = GetForegroundWindow();
                if (IsSource(h)) previous = h;
                if (show.WaitOne(0)) pendingHomes++;
                if (pendingHomes>0 && !capturing) { pendingHomes--; OpenHome(); }
                if(!collectingScroll) {
                    string tip=talks.Exists(d=>d.IsGenerating)?"如许AI · 正在解释…":"如许AI · 阅读助手";
                    if(tray.Text!=tip) tray.Text=tip;
                }
            };
            var initial=GetForegroundWindow(); if (IsSource(initial)) previous=initial;
            tick.Start();
            OpenHome();
        }
        ContextMenuStrip BuildTrayMenu()
        {
            var menu=new ContextMenuStrip {ShowImageMargin=false};
            var capture=(ToolStripMenuItem)menu.Items.Add("框选解释",null,delegate {StartRegion();});
            var read=(ToolStripMenuItem)menu.Items.Add("读取整篇",null,delegate {StartPage();});
            var scroll=(ToolStripMenuItem)menu.Items.Add("滚轮长截图",null,delegate {StartPage(null,true);});
            menu.Items.Add(new ToolStripSeparator());
            var home=(ToolStripMenuItem)menu.Items.Add("打开如许AI",null,delegate {OpenHome();});
            var records=new ToolStripMenuItem("阅读记录");menu.Items.Add(records);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("设置",null,delegate {Configure(Form.ActiveForm);});
            menu.Items.Add("退出",null,delegate {ExitThread();});
            var renderer=new TrayRenderer();
            menu.Opening+=delegate {
                menu.Renderer=SystemInformation.HighContrast?(ToolStripRenderer)new ToolStripSystemRenderer():renderer;
                records.DropDown.Renderer=menu.Renderer;
                var items=new[]{capture,read,scroll,home};
                for(int i=0;i<items.Length;i++) items[i].ShortcutKeyDisplayString=keys.Label(i);
                while(records.DropDownItems.Count>0) records.DropDownItems[0].Dispose();
                for(int i=0;i<Math.Min(5,recent.Count);i++) {
                    var row=recent[i];
                    string title=HistoryWindow.ShortLine(row.Title,30);
                    records.DropDownItems.Add(title.Length==0?"阅读对话":title.Replace("&","&&"),null,
                        delegate {OpenConversation(row.Id,Cursor.Position);});
                }
                if(records.DropDownItems.Count>0) records.DropDownItems.Add(new ToolStripSeparator());
                records.DropDownItems.Add("查看全部…",null,delegate {OpenHistory();});
            };
            return menu;
        }
        static void Present(Form window)
        {
            if (window==null || window.IsDisposed || window.Disposing) return;
            var owner=window.Owner;
            if (owner!=null && owner.WindowState==FormWindowState.Minimized) ShowWindow(owner.Handle,9);
            if (!window.Visible) window.Show();
            if (window.WindowState==FormWindowState.Minimized) ShowWindow(window.Handle,9); // SW_RESTORE
            window.BringToFront();
            SetForegroundWindow(window.Handle);
            window.Activate();
        }
        void OpenHome()
        {
            if (quitting) return;
            if (settingsHost!=null) { Present(settingsHost); return; }
            if (capturing || configuring) { pendingHomes++; return; }
            Home home=null;
            home=new Home(()=>FromRail(home,()=>StartRegion(home)),()=>FromRail(home,()=>StartPage(home)),
                ()=>FromRail(home,()=>StartPage(home,true)),()=>FromRail(home,()=>home.HistoryPage.Focus()),
                ()=>Configure(home),(profile,fast)=> {
                    FromRail(home,delegate {
                        try {
                            settings=ModelSettings.SaveQuickChoice(profile,fast);
                            foreach(var item in homes) item.SyncOptions(settings);
                        } catch(Exception ex) { Notice("阅读偏好保存失败。"+ex.Message,ToastKind.Error); }
                    });
                    home.SyncOptions(settings);
                });
            home.HistoryPage.OpenRequested+=OpenConversation;
            home.HistoryPage.DeleteRequested+=id=>DeleteRecord(home,id);
            home.ConnectionRequested+=()=>Configure(home,SettingsPage.Connection);
            home.ShortcutsRequested+=()=>Configure(home,SettingsPage.Shortcuts);
            home.SetShortcuts(keys.Label(0),keys.Label(1),keys.Label(2));
            home.HistoryPage.SetRecords(recent);
            home.SyncOptions(settings);
            home.OpenFrom(Cursor.Position);
            home.Status(!keys.HasConflict,true,settings.Ready,configError);
            home.HistoryStatus(saveWarning ?? loadWarning);
            homes.Add(home);
            home.FormClosed+=delegate(object sender,FormClosedEventArgs e) {
                homes.Remove(home);
                if(!quitting && !capturing && e.CloseReason==CloseReason.UserClosing && homes.Count==0)
                    ShowTrayHint();
            };
            Present(home);
        }
        bool trayHintSeen;
        void ShowTrayHint()
        {
            if(trayHintSeen) return;
            trayHintSeen=true;
            const string path=@"HKEY_CURRENT_USER\Software\Jiyue";
            try {
                if(Object.Equals(Registry.GetValue(path,"TrayHintShown",0),1)) return;
                Registry.SetValue(path,"TrayHintShown",1,RegistryValueKind.DWord);
            } catch(System.Security.SecurityException) { }
              catch(UnauthorizedAccessException) { }
              catch(System.IO.IOException) { }
            string shortcut=keys.Label(0);
            Toast.Show("如许AI 仍在托盘运行 · "+(shortcut.Length==0?"点击托盘图标开始阅读":"随时按 "+shortcut+" 框选"));
        }
        void FromRail(Home home,Action action)
        {
            if(settingsWindow!=null) {
                if(settingsHost!=home) { Present(settingsHost); return; }
                settingsWindow.Close();
                if(settingsWindow!=null) return;
            }
            if(!quitting && !capturing) action();
        }
        void OpenHistory(Point? openingPoint=null)
        {
            if (quitting || capturing) return;
            var home=settingsHost as Home;
            if(home!=null) { FromRail(home,()=>Present(home)); return; }
            if (settingsHost!=null) { Present(settingsHost); return; }
            if (homes.Count==0) OpenHome();
            if (homes.Count>0) Present(homes[homes.Count-1]);
        }
        void OpenConversation(string id,Point openingPoint)
        {
            if (quitting || capturing) return;
            if (settingsHost!=null) { Present(settingsHost); return; }
            var open=talks.Find(window=>window.ConversationId==id && !window.IsDisposed);
            if (open!=null) { Present(open); return; }
            var saved=recent.Find(row=>row.Id==id);
            if (saved==null) return;
            var anchor=new Rectangle(openingPoint,new Size(1,1));
            var panel=NewTalk(anchor,"最近对话");
            panel.Restore(saved);
            panel.ShowAt(anchor,openingPoint);
            Present(panel);
        }
        void Remember(Dialog window)
        {
            if(removedOpen.Contains(window.ConversationId))return;
            var record=window.CaptureRecord();
            if (record==null) return;
            PutRecord(record);
        }
        void PutRecord(ConversationRecord record)
        {
            int index=recent.FindIndex(row=>row.Id==record.Id);
            if (index>=0 && recent[index].UpdatedUtc==record.UpdatedUtc) recent[index]=record;
            else {
                if (index<0 && recent.Count>=ConversationStore.Limit && recent[recent.Count-1].UpdatedUtc>=record.UpdatedUtc) return;
                if (index>=0 && recent[index].UpdatedUtc>record.UpdatedUtc) return;
                if (index>=0) recent.RemoveAt(index);
                int at=recent.FindIndex(row=>row.UpdatedUtc<=record.UpdatedUtc);
                recent.Insert(at<0 ? recent.Count : at,record);
                if (recent.Count>ConversationStore.Limit) recent.RemoveAt(ConversationStore.Limit);
            }
            for(int i=ConversationStore.ImageLimit;i<recent.Count;i++)recent[i]=recent[i].TextOnly();
            QueueHistorySave();
        }
        void DeleteRecord(Home home,string id)
        {
            var row=recent.Find(r=>r.Id==id);
            if(row==null)return;
            recent.Remove(row);
            if(loadTask!=null)removedWhileLoading.Add(id);
            if(talks.Exists(w=>w.ConversationId==id))removedOpen.Add(id);
            QueueHistorySave();
            Toast.Show("已删除",ToastKind.Info,home,"撤销",delegate {
                removedWhileLoading.Remove(id); removedOpen.Remove(id);
                var open=talks.Find(w=>w.ConversationId==id && !w.IsDisposed);
                PutRecord(open==null ? row : open.CaptureRecord() ?? row);
            });
        }
        void QueueHistorySave()
        {
            RefreshHistory(true);
            saveNeeded=true;
            if (!quitting) { saveTimer.Stop(); saveTimer.Start(); }
        }
        void RefreshHistory(bool recordsChanged)
        {
            if (quitting) return;
            foreach (var home in homes) {
                home.HistoryStatus(saveWarning ?? loadWarning);
                if(recordsChanged) home.HistoryPage.SetRecords(recent);
            }
            if (settingsWindow!=null && !settingsWindow.IsDisposed) settingsWindow.SetHistoryCount(recent.Count);
        }
        void FinishHistoryLoad()
        {
            if (loadTask==null) return;
            try
            {
                var loaded=loadTask.GetAwaiter().GetResult();
                loadWarning=ConversationStore.LastLoadWarning;
                // Captures taken during startup already lead the list. Keep their newer snapshots.
                foreach (var row in loaded) {
                    if (recent.Count>=ConversationStore.Limit) break;
                    if (!removedWhileLoading.Contains(row.Id) && !recent.Exists(item=>item.Id==row.Id))
                        recent.Add(recent.Count<ConversationStore.ImageLimit?row:row.TextOnly());
                }
            }
            catch { loadWarning="无法读取本机对话记录。"; }
            finally { loadTask=null;removedWhileLoading.Clear(); RefreshHistory(true); }
        }
        bool FinishHistoryWrite()
        {
            if (writeTask==null) return true;
            try { writeTask.GetAwaiter().GetResult(); saveWarning=null; return true; }
            catch { saveWarning="最近对话未保存到本机，仍可在此次使用中查看。"; return false; }
            finally { writeTask=null; RefreshHistory(false); }
        }
        void SaveHistoryTick()
        {
            if (loadTask!=null && !loadTask.IsCompleted) return;
            FinishHistoryLoad();
            if (writeTask!=null && !writeTask.IsCompleted) return;
            saveTimer.Stop();
            if (!FinishHistoryWrite() && !saveNeeded) { saveNeeded=true; return; }
            if (!saveNeeded) return;
            var batch=new List<ConversationRecord>(recent);
            saveNeeded=false;
            writeTask=Task.Run(delegate {ConversationStore.Save(batch);});
            saveTimer.Start();
        }
        bool IsSource(IntPtr h)
        {
            if (h==IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) return false;
            uint owner; GetWindowThreadProcessId(h,out owner);
            if (owner==pid) return false;
            var s=new System.Text.StringBuilder(128); GetClassName(h,s,s.Capacity);
            string c=s.ToString();
            return c!="Shell_TrayWnd" && c!="Shell_SecondaryTrayWnd" && c!="#32768" && c!="Progman" && c!="WorkerW" && c!="NotifyIconOverflowWindow";
        }
        void Configure(Form owner, SettingsPage page=SettingsPage.Reading, Action afterSave=null)
        {
            if (settingsHost!=null) { Present(settingsHost); return; }
            if (configuring || capturing || quitting) return;
            if (!(owner is Home || owner is Dialog)) {
                if (homes.Count==0) OpenHome();
                owner=homes[homes.Count-1];
            }
            Point? historyPoint=null;
            Home launcher=owner as Home;
            var container=launcher==null?(Control)owner:launcher.ContentArea;
            Action<Point> historyAction=delegate(Point point) {
                historyPoint=point;
                settingsWindow.Close();
            };
            var edit=new SettingsDialog(settings,page,launcher==null?historyAction:null);
            edit.PreferencesSaved+=delegate(ModelSettings updated) {
                settings=updated;
                foreach(var home in homes) {
                    home.SyncOptions(settings);
                    home.Status(!keys.HasConflict,true,settings.Ready,configError);
                }
            };
            edit.BindShortcuts(keys,delegate {
                foreach(var home in homes) {
                    home.SetShortcuts(keys.Label(0),keys.Label(1),keys.Label(2));
                    home.Status(!keys.HasConflict,true,settings.Ready,configError);
                }
            });
            edit.TopLevel=false;
            edit.FormBorderStyle=FormBorderStyle.None;
            edit.Dock=DockStyle.Fill;
            var previousFocus=owner.ActiveControl;
            var hidden=new List<Control>();
            foreach(Control child in container.Controls) if(child.Visible) {hidden.Add(child); child.Hide();}
            if(launcher!=null) launcher.SelectPage(true);
            settingsHost=owner; settingsWindow=edit; configuring=true;
            FormClosingEventHandler closing=delegate(object sender,FormClosingEventArgs e) {
                if(!quitting && e.CloseReason==CloseReason.UserClosing && !edit.CanLeave()) e.Cancel=true;
            };
            owner.FormClosing+=closing;
            edit.Disposed+=delegate {
                owner.FormClosing-=closing;
                settingsHost=null; settingsWindow=null; configuring=false;
                if(quitting || owner.Disposing || owner.IsDisposed) return;
                foreach(var child in hidden) if(!child.IsDisposed) child.Show();
                if(launcher!=null) {launcher.SelectPage(false);launcher.HistoryStatus(saveWarning ?? loadWarning);}
                if(previousFocus!=null && previousFocus.CanFocus) previousFocus.Focus();
                if(edit.Saved!=null) {
                    settings=edit.Saved;
                    if(edit.ConnectionSaved) configError=null;
                    foreach(var home in homes) {home.Status(!keys.HasConflict,true,settings.Ready,configError);home.SyncOptions(settings);}
                    if(edit.ConnectionSaved && afterSave!=null) afterSave();
                }
                if(historyPoint.HasValue) OpenHistory(historyPoint.Value);
            };
            Present(owner);
            container.Controls.Add(edit);
            edit.SetHistoryCount(recent.Count);
            edit.Show(); edit.BringToFront(); edit.Select();
        }
        IntPtr Prepare(Home launcher)
        {
            Toast.HideForCapture();
            var h=GetForegroundWindow(); if (IsSource(h)) previous=h;
            captureOrigin=launcher ?? homes.Find(home=>home.IsHandleCreated && home.Handle==h);
            // Hide only currently displayed reader windows. Do not cancel their requests,
            // dispose their controls or unminimize windows the reader put away.
            foreach (var home in homes) HideForCapture(home);
            foreach (var open in talks) {
                foreach (var preview in open.OwnedForms) HideForCapture(preview);
                HideForCapture(open);
            }
            if (IsSource(previous)) { SetForegroundWindow(previous); return previous; }
            h=GetForegroundWindow();
            return IsSource(h) ? h : IntPtr.Zero;
        }
        void HideForCapture(Form window)
        {
            if (window==null || window.IsDisposed || !window.IsHandleCreated) return;
            IntPtr h=window.Handle;
            if (!IsWindowVisible(h) || IsIconic(h)) return;
            hiddenForCapture.Add(window);
            ShowWindow(h,0); // SW_HIDE, keeps the managed window and its conversation alive.
        }
        void FinishCapture()
        {
            if (!quitting) foreach (var window in hiddenForCapture) {
                if (window.IsDisposed || !window.IsHandleCreated || IsWindowVisible(window.Handle)) continue;
                // Restore to the taskbar behind the document/result; never take focus on cancel.
                const uint flags=0x0001|0x0002|0x0010|0x0040; // NOSIZE|NOMOVE|NOACTIVATE|SHOWWINDOW
                if (!SetWindowPos(window.Handle,new IntPtr(1),0,0,0,0,flags))
                    ShowWindow(window.Handle,8); // SW_SHOWNA
            }
            hiddenForCapture.Clear();
            capturing=false;
            if(!quitting) Toast.FinishCapture();
            var origin=captureOrigin;
            captureOrigin=null;
            if (!quitting && origin!=null) Present(origin);
        }
        void ShowCaptureResult(Dialog panel,Rectangle bounds,Point? releasePoint=null,Point? openingPoint=null)
        {
            panel.ShowAt(bounds,releasePoint,openingPoint);
            // A shortcut may be used over the document while its launcher is still open.
            var launcher=captureOrigin ?? (hiddenForCapture.FindLast(window=>window is Home) as Home);
            captureOrigin=null;
            if (launcher!=null && !launcher.IsDisposed) launcher.Close();
        }
        async void StartRegion(Home launcher=null)
        {
            if (capturing || configuring || quitting) return;
            capturing=true;
            try
            {
                Prepare(launcher);
                // Let the compositor remove our own windows before capturing.
                await System.Threading.Tasks.Task.Delay(45);
                if (quitting) return;
                var capturedUtc=DateTime.UtcNow;
                Snipper.Begin(delegate(Bitmap bitmap, Rectangle bounds, Point releasePoint,bool questionFirst) {
                    try {
                        if (quitting) bitmap.Dispose();
                        else OpenTalk(bounds,releasePoint,bitmap,CaptureSource.RegionTitle(bounds),capturedUtc,questionFirst);
                    } finally { FinishCapture(); }
                }, FinishCapture);
            }
            catch (Exception e) { FinishCapture(); if (!quitting) Notice(e.Message,ToastKind.Error); }
        }
        async void StartPage(Home launcher=null, bool? useWheel=null)
        {
            if (articleRead!=null) {
                if (collectingScroll) finishScroll=true;
                else articleRead.Cancel();
                return;
            }
            if (capturing || configuring || quitting) return;
            capturing=true;
            var request=new CancellationTokenSource();
            var openingPoint=CapturePopup.TriggerPoint(launcher==null ? null : launcher.ActiveControl);
            articleRead=request;
            CaptureStatus status=null;
            Dialog panel=null;
            try
            {
                var source=Prepare(launcher);
                await Task.Delay(75,request.Token);
                if (quitting) return;
                if (useWheel ?? settings.ArticleAsLongImage) {
                    var selected=new TaskCompletionSource<Rectangle>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Point end=Point.Empty;
                    Rectangle area;
                    using (Snipper.Begin(delegate(Bitmap first,Rectangle rect,Point release,bool shift) {
                        first.Dispose(); end=release;
                        source=CaptureSource.SelectionWindow(rect);
                        if (IsSource(source)) SetForegroundWindow(source);
                        selected.TrySetResult(rect);
                    },delegate {selected.TrySetCanceled();},
                        "单击选中正文区域或拖动框选 · 向下滚动采集 · Enter 完成 · Esc 取消"))
                    using (request.Token.Register(delegate {selected.TrySetCanceled();})) area=await selected.Task;
                    await Task.Delay(100,request.Token);
                    if (CaptureSource.SelectionWindow(area)!=source || !CaptureSource.CanReadSelection(source,area)) {
                        Notice("请在文章窗口内框选正文，并让选区完整可见。"); return;
                    }
                    string selectedTitle=CaptureSource.WindowTitle(source);
                    var startedAt=DateTime.UtcNow;
                    collectingScroll=true; finishScroll=false;
                    bool canFinishByKey=keys.CaptureKeys(delegate {finishScroll=true;},request.Cancel);
                    status=new CaptureStatus("向下滚动 · 已采集约 1 屏",delegate {finishScroll=true;},request.Cancel);
                    if (!status.Place(area,true)) {
                        if (!canFinishByKey)
                            throw new InvalidOperationException("结束采集的快捷键被占用。请把选区框小一些，给完成按钮留出空间。");
                        tray.Text="如许AI · 正在采集滚轮长截图";
                    }
                    var monitor=status;
                    var progress=new Progress<int>(n=>{ if (!monitor.IsDisposed) monitor.SetProgress(n); });
                    var result=await Task.Run(()=>LongArticleImage.Capture(source,area,request.Token,
                        ()=>finishScroll,((IProgress<int>)progress).Report),request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    status.Hide(); keys.EndCaptureKeys(); collectingScroll=false;
                    if (!IsSource(source) || GetForegroundWindow()!=source) return;
                    panel=NewTalk(area,"滚轮长截图");
                    panel.SetCaptureSource("滚轮长截图",selectedTitle,startedAt,null);
                    ShowCaptureResult(panel,area,end);
                    panel.AttachArticle("滚轮长截图",null,result.Png,result.Notice);
                    return;
                }
                if (!IsSource(source) || GetForegroundWindow()!=source) {
                    string shortcut=keys.Label(1);
                    Notice(shortcut.Length>0?"先打开文章，再按 "+shortcut+" 读取整篇。":"请先打开文章，再从托盘选择「读取整篇」。"); return;
                }
                RECT r; if (!GetWindowRect(source,out r)) throw new InvalidOperationException("无法定位当前窗口。");
                var bounds=Rectangle.Intersect(SystemInformation.VirtualScreen,Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom));
                if (bounds.Width<2 || bounds.Height<2) throw new InvalidOperationException("当前窗口没有可见内容。");
                var capturedUtc=DateTime.UtcNow;
                var title=CaptureSource.WindowTitle(source);
                var preview=CaptureSource.VisiblePagePreview(source,bounds);
                var reading=PageReader.ReadArticleAsync(source,request.Token);
                if (await Task.WhenAny(reading,Task.Delay(220,request.Token))!=reading) {
                    request.Token.ThrowIfCancellationRequested();
                    status=new CaptureStatus("正在读取文章…",null,request.Cancel);
                    status.Place(bounds,false);
                }
                var article=await reading;
                request.Token.ThrowIfCancellationRequested();
                if (status!=null) status.Hide();
                if (quitting || GetForegroundWindow()!=source) return;
                panel=NewTalk(bounds,article.Scope);
                panel.SetCaptureSource(article.Scope,title,capturedUtc,preview);
                ShowCaptureResult(panel,bounds,openingPoint:openingPoint);
                if (article.Text!=null) panel.AttachArticle(article.Scope,article.Text,null,article.Notice);
                else panel.OfferScrollCapture((article.Notice ?? "没有读到正文。")+" 可以框住正文，用滚轮截取想读的部分。",
                    delegate { previous=source; StartPage(null,true); });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!quitting) { if (panel!=null && !panel.IsDisposed) panel.ShowFailure(e.Message); else Notice(e.Message,ToastKind.Error); } }
            finally {
                if (status!=null) status.Dispose();
                if (!quitting) { keys.EndCaptureKeys(); tray.Text="如许AI · 阅读助手"; }
                articleRead=null; collectingScroll=false; request.Dispose(); FinishCapture();
            }
        }
        Dialog NewTalk(Rectangle bounds,string label)
        {
            Dialog current=null;
            current=new Dialog(label,delegate {return settings;},done=>Configure(current,SettingsPage.Connection,done));
            current.AnswerPreferenceRequested+=delegate {Configure(current,SettingsPage.Answer);};
            current.ReadingOptionsRequested+=delegate {Configure(current,SettingsPage.Reading);};
            ImageImport.Bind(current,delegate(string title,byte[] png) {
                if(quitting || configuring || capturing) return;
                var source=current.Bounds;
                var imported=NewTalk(source,"导入图片");
                imported.SetCaptureSource("导入图片",title,DateTime.UtcNow,null);
                imported.ShowAt(source,null,Cursor.Position);
                imported.Attach("导入图片",null,png);
            });
            talks.Add(current);
            current.ConversationChanged+=delegate { Remember(current); };
            current.FormClosing+=delegate { Remember(current); };
            current.FormClosed+=delegate {talks.Remove(current);removedOpen.Remove(current.ConversationId);};
            return current;
        }
        async void OpenTalk(Rectangle bounds,Point releasePoint,Bitmap bitmap,string sourceTitle,DateTime capturedUtc,bool questionFirst)
        {
            Dialog panel=null;
            try
            {
                panel=NewTalk(bounds,"框选区域");
                panel.SetCaptureSource("框选区域",sourceTitle,capturedUtc,null);
                ShowCaptureResult(panel,bounds,releasePoint);
                byte[] bytes=await System.Threading.Tasks.Task.Run(delegate {
                    using(var ms=new MemoryStream()) { bitmap.Save(ms,ImageFormat.Png); return ms.ToArray(); }
                });
                if (!panel.IsDisposed) {
                    if(questionFirst) panel.AttachQuestionFirst("框选区域",null,bytes);
                    else panel.Attach("框选区域",null,bytes);
                }
            }
            catch(Exception e) {
                if (panel!=null && !panel.IsDisposed) panel.ShowFailure(e.Message);
                else if (!quitting) Notice(e.Message,ToastKind.Error);
            }
            finally { bitmap.Dispose(); }
        }
        void Notice(string message,ToastKind kind=ToastKind.Info) { Toast.Show(message,kind,Form.ActiveForm); }
        protected override void ExitThreadCore()
        {
            if (quitting) return;
            quitting=true; if (articleRead!=null) articleRead.Cancel();
            Toast.CloseAll();
            stopping.Set(); tick.Stop(); tick.Dispose(); keys.Dispose(); tray.Visible=false;
            var menu=tray.ContextMenuStrip; tray.Dispose(); menu.Dispose(); if (trayIcon!=null) trayIcon.Dispose();
            if (settingsWindow!=null && !settingsWindow.IsDisposed) settingsWindow.Dispose();
            foreach (var window in talks.ToArray()) window.Close();
            foreach (var home in homes.ToArray()) home.Close();
            saveTimer.Stop(); saveTimer.Dispose();
            FinishHistoryLoad();
            if (!FinishHistoryWrite()) saveNeeded=true;
            if (saveNeeded) {
                try { ConversationStore.Save(new List<ConversationRecord>(recent)); }
                catch { MessageBox.Show("最近对话未能写入本机，重新启动后可能看不到刚才的内容。","如许AI",MessageBoxButtons.OK,MessageBoxIcon.Error); }
            }
            base.ExitThreadCore();
        }
    }

    internal sealed partial class KeysWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h,int id);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string className,string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint msg,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
        const string WindowTitle="JiyueReader.Hotkeys.693A";
        const int ActivateMessage=0x8000+71; // private WM_APP message of our message-only window
        public static bool NotifyExistingInstance(out bool delivered)
        {
            delivered=false;
            var h=FindWindowEx(new IntPtr(-3),IntPtr.Zero,null,WindowTitle);
            uint pid;
            if (h==IntPtr.Zero || GetWindowThreadProcessId(h,out pid)==0) return false;
            AllowSetForegroundWindow(pid);
            UIntPtr reply;
            const uint block=1,abortIfHung=2,errorOnExit=32;
            delivered=SendMessageTimeout(h,ActivateMessage,IntPtr.Zero,IntPtr.Zero,block|abortIfHung|errorOnExit,250,out reply)!=IntPtr.Zero;
            return delivered && reply==new UIntPtr(1);
        }
        readonly Func<bool> activate;
        public bool Region {get {return bindings[0]==0 || activeIds[0]!=0;}}
        public bool Page {get {return bindings[1]==0 || activeIds[1]!=0;}}
        Action finishCapture,cancelCapture;
        public bool CaptureKeys(Action finish,Action cancel)
        {
            EndCaptureKeys();
            if (RegisterHotKey(Handle,3,0x4000,(uint)Keys.Enter)) finishCapture=finish;
            if (RegisterHotKey(Handle,4,0x4000,(uint)Keys.Escape)) cancelCapture=cancel;
            return finishCapture!=null || activeIds[1]!=0 || activeIds[2]!=0;
        }
        public void EndCaptureKeys()
        {
            if (finishCapture!=null) UnregisterHotKey(Handle,3);
            if (cancelCapture!=null) UnregisterHotKey(Handle,4);
            finishCapture=null; cancelCapture=null;
        }
        public KeysWindow(Action onRegion,Action onPage,Action onScroll,Action onHome,Func<bool> onActivate)
        {
            commands=new[]{onRegion,onPage,onScroll,onHome}; activate=onActivate;
            CreateHandle(new CreateParams { Parent=new IntPtr(-3),Caption=WindowTitle });
            LoadBindings();
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg==ActivateMessage) { m.Result=activate() ? new IntPtr(1) : IntPtr.Zero; return; }
            if(m.Msg==0x312) {
                switch (m.WParam.ToInt32()) {
                    case 3: if (finishCapture!=null) finishCapture(); break;
                    case 4: if (cancelCapture!=null) cancelCapture(); break;
                    default:
                        int at=Array.IndexOf(activeIds,m.WParam.ToInt32());
                        if(at>=0 && m.WParam!=IntPtr.Zero) {
                            if(Recording!=null) Recording(bindings[at]); else commands[at]();
                        }
                        break;
                }
            }
            base.WndProc(ref m);
        }
        public void Dispose() { EndCaptureKeys(); foreach(int id in activeIds) if(id!=0) UnregisterHotKey(Handle,id); DestroyHandle(); }
    }
}
