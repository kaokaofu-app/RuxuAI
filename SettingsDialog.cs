using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;
using Automation = System.Windows.Automation.AutomationProperties;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Markup;
using W = System.Windows;
using C = System.Windows.Controls;
using Media = System.Windows.Media;

namespace Jiyue
{
    internal enum SettingsPage { Connection, Reading, Answer, Shortcuts, Appearance, About }

    internal sealed class SettingsDialog : AppearanceForm
    {
        public ModelSettings Saved { get; private set; }
        public bool ConnectionSaved { get; private set; }
        internal event Action<ModelSettings> PreferencesSaved;
        ModelSettings original;
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval=400 };
        readonly System.Windows.Forms.Timer savedNotice = new System.Windows.Forms.Timer { Interval=1500 };
        CancellationTokenSource pendingTest;
        AppearanceMode? pendingMode;
        int? pendingSize;
        bool ready, reveal, settingProvider;
        string credentialOrigin;
        const string RunPath=@"Software\Microsoft\Windows\CurrentVersion\Run";
        sealed class Provider
        {
            internal readonly string Label, Address, Model, Link;
            internal Provider(string label,string address,string model,string link) { Label=label; Address=address; Model=model; Link=link; }
            public override string ToString() { return Label; }
        }
        static readonly Provider[] providers={
            new Provider("自定义","","",null),
            new Provider("OpenAI","https://api.openai.com/v1","gpt-5-mini","https://platform.openai.com/api-keys"),
            new Provider("DeepSeek","https://api.deepseek.com/v1","deepseek-v4-flash","https://platform.deepseek.com/api_keys"),
            new Provider("通义千问 · 北京","https://dashscope.aliyuncs.com/compatible-mode/v1","qwen-plus","https://bailian.console.aliyun.com/?tab=model#/api-key"),
            new Provider("Kimi · Moonshot","https://api.moonshot.cn/v1","kimi-k3.1-pro","https://platform.moonshot.cn/console/api-keys"),
            new Provider("智谱 GLM","https://open.bigmodel.cn/api/paas/v4","glm-4.7-flash","https://bigmodel.cn/usercenter/proj-mgmt/apikeys"),
            new Provider("硅基流动","https://api.siliconflow.cn/v1","Qwen/Qwen3.5-397B-A17B","https://cloud.siliconflow.cn/account/ak"),
            new Provider("Ollama 本地","http://localhost:11434/v1","qwen3:4b",null)
        };
        readonly C.Grid view;
        readonly C.TextBox address, model, prompt;
        readonly C.PasswordBox key;
        readonly C.ComboBox thinking;
        readonly C.RadioButton fastReply;
        readonly C.RadioButton useImage, useText, articleText, articleImage;
        readonly C.TextBlock error;
        bool syncing, syncingAppearance, discardConfirmed;
        SettingsPage page;
        ReadingProfile selectedProfile;
        static readonly string[] modes = { "default", "enable_thinking", "thinking_disabled", "reasoning_none" };

        public SettingsDialog(ModelSettings current, SettingsPage initialPage, Action<Point> openHistory)
        {
            original = current;
            Text = "如许AI · 设置";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; // The WPF view uses device-independent units.
            ClientSize = new Size(776, 568);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = true;
            ShowInTaskbar = true;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jiyue.SettingsView.xaml"))
                view = (C.Grid)XamlReader.Load(resource);
            Theme.ApplyWpf(view);
            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = view, TabStop = true });

            address = Part<C.TextBox>("Address");
            model = Part<C.TextBox>("Model");
            key = Part<C.PasswordBox>("Key");
            thinking = Part<C.ComboBox>("Thinking");
            fastReply = Part<C.RadioButton>("FastReply");
            useImage = Part<C.RadioButton>("UseImage");
            useText = Part<C.RadioButton>("UseText");
            articleText = Part<C.RadioButton>("ArticleText");
            articleImage = Part<C.RadioButton>("ArticleImage");
            prompt = Part<C.TextBox>("Prompt");
            error = Part<C.TextBlock>("Error");
            address.Text = current.BaseUrl ?? "";
            model.Text = current.Model ?? "";
            key.Password = current.ApiKey ?? "";
            thinking.ItemsSource = new[] { "跟随模型默认设置", "关闭思考 · enable_thinking = false",
                "关闭思考 · thinking.type = disabled", "关闭思考 · reasoning_effort = none" };
            thinking.SelectedIndex = Math.Max(0, Array.IndexOf(modes, current.ThinkingMode));
            Part<C.RadioButton>(current.FastReply ? "FastReply" : "StandardReply").IsChecked = true;
            useText.IsChecked = current.ReadRegionAsText;
            useImage.IsChecked = !current.ReadRegionAsText;
            articleImage.IsChecked = current.ArticleAsLongImage;
            articleText.IsChecked = !current.ArticleAsLongImage;
            prompt.Text = current.AnswerPreference ?? "";
            selectedProfile = ReadingProfile.Find(current.ReadingProfileId);
            var profiles = Part<C.WrapPanel>("Profiles");
            foreach (var option in ReadingProfile.All) {
                var profile = option;
                var choice = new C.RadioButton {
                    Content = profile.Label, Tag = profile.Id, GroupName = "ReadingProfile",
                    Style = (W.Style)view.FindResource("Choice"), IsChecked = profile == selectedProfile
                };
                choice.Checked += delegate {
                    selectedProfile = profile;
                    Part<C.TextBlock>("ProfileDescription").Text = profile.Description;
                    UpdateFooter();
                };
                profiles.Children.Add(choice);
            }
            Part<C.TextBlock>("ProfileDescription").Text = selectedProfile.Description;

            credentialOrigin=CredentialAddress(address.Text);
            BindConnection();
            BindAbout();
            BindFontSize();
            address.TextChanged += delegate {
                string origin=CredentialAddress(address.Text);
                if(key.Password.Length!=0 && !String.Equals(origin,credentialOrigin,StringComparison.Ordinal)) key.Clear();
                credentialOrigin=origin;
                if(!settingProvider) SyncProvider();
                ResetTest(); UpdateHints();
            };
            model.TextChanged += delegate { ResetTest(); UpdateHints(); };
            key.PasswordChanged += delegate {
                credentialOrigin=CredentialAddress(address.Text);
                if(reveal && Part<C.TextBox>("ClearKey").Text!=key.Password) Part<C.TextBox>("ClearKey").Text=key.Password;
                ResetTest(); UpdateHints();
            };
            prompt.TextChanged += delegate { UpdateHints(); };
            thinking.SelectionChanged += delegate { UpdateFooter(); };
            fastReply.Checked += delegate { UpdateSpeed(); };
            fastReply.Unchecked += delegate { UpdateSpeed(); };
            UpdateSpeed();
            useText.Checked += delegate { UpdateFooter(); };
            useImage.Checked += delegate { UpdateFooter(); };
            articleText.Checked += delegate { UpdateFooter(); };
            articleImage.Checked += delegate { UpdateFooter(); };
            UpdateHints();
            foreach (SettingsPage entry in Enum.GetValues(typeof(SettingsPage))) {
                ConnectTab(entry + "Nav", entry);
                ConnectTab("Small" + entry, entry);
            }
            foreach (AppearanceMode entry in Enum.GetValues(typeof(AppearanceMode))) ConnectMode(entry);
            Part<C.Button>("ClearPrompt").Click += delegate { prompt.Clear(); prompt.Focus(); };
            foreach (string name in new[] {"History", "SmallHistory"}) {
                var history = Part<C.Button>(name);
                history.Visibility = VisibilityFor(openHistory != null);
                history.Click += delegate {
                    var point=Cursor.Position;
                    if (history.IsVisible && !history.IsMouseOver) {
                        var center=history.PointToScreen(new W.Point(history.ActualWidth/2,history.ActualHeight/2));
                        point=new Point((int)Math.Round(center.X),(int)Math.Round(center.Y));
                    }
                    if (ConfirmDiscard()) {
                        discardConfirmed = true;
                        openHistory(point);
                    }
                };
            }
            if(!current.Ready) {
                First("ConnectionNav","ReadingNav"); First("SmallConnection","SmallReading");
                if(initialPage==SettingsPage.Reading) initialPage=SettingsPage.Connection;
            }
            debounce.Tick+=delegate {FlushPreferences();};
            savedNotice.Tick+=delegate {savedNotice.Stop(); Part<C.TextBlock>("Feedback").Text="";};
            ready=true;
            SwitchPage(initialPage);
            view.SizeChanged += delegate { SetColumns(); };
            Part<C.Button>("Back").Click += delegate { Close(); };
            Part<C.Button>("Save").Click += delegate { SaveSettings(); };
            view.KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) {
                if (e.Handled) return;
                if (e.Key == System.Windows.Input.Key.Escape) {
                    e.Handled = true;
                    Close();
                } else if (page==SettingsPage.Connection && e.Key == System.Windows.Input.Key.Enter &&
                    !Object.ReferenceEquals(e.OriginalSource, prompt) &&
                    (e.OriginalSource is C.TextBox || e.OriginalSource is C.PasswordBox)) {
                    e.Handled = true;
                    SaveSettings();
                }
            };
            AppAppearance.Bind(this, ApplyTheme);
            Disposed+=delegate {
                ready=false; CancelTest(); debounce.Dispose(); savedNotice.Dispose();
                ReaderPreferences.FontSizeChanged-=SyncFontSize;
            };
        }

        T Part<T>(string name) where T : class { return (T)view.FindName(name); }

        void First(string itemName,string before)
        {
            var item=Part<C.RadioButton>(itemName); var panel=(C.Panel)item.Parent;
            panel.Children.Remove(item);
            panel.Children.Insert(panel.Children.IndexOf(Part<C.RadioButton>(before)),item);
        }

        static string CredentialAddress(string text)
        {
            Uri endpoint;
            return new ModelSettings {BaseUrl=text}.TryEndpoint(out endpoint) ? endpoint.AbsoluteUri : "";
        }

        void SyncProvider()
        {
            var combo=Part<C.ComboBox>("Provider");
            string current=address.Text.Trim().TrimEnd('/');
            int index=Array.FindIndex(providers,p=>p.Address.Length!=0 && String.Equals(p.Address,current,StringComparison.OrdinalIgnoreCase));
            settingProvider=true;
            try { combo.SelectedIndex=Math.Max(0,index); }
            finally { settingProvider=false; }
            Part<C.Button>("KeyLink").Visibility=VisibilityFor(providers[combo.SelectedIndex].Link!=null);
        }

        void BindConnection()
        {
            var combo=Part<C.ComboBox>("Provider"); combo.ItemsSource=providers; SyncProvider();
            combo.SelectionChanged+=delegate {
                if(settingProvider || combo.SelectedIndex<0) return;
                var p=(Provider)combo.SelectedItem;
                settingProvider=true;
                try {
                    if(p.Address.Length!=0) {
                        address.Text=p.Address; model.Text=p.Model;
                    }
                } finally {settingProvider=false;}
                Part<C.Button>("KeyLink").Visibility=VisibilityFor(p.Link!=null);
            };
            Part<C.Button>("KeyLink").Click+=delegate {
                var selected=(Provider)combo.SelectedItem;
                if(selected!=null && selected.Link!=null) OpenLink(selected.Link);
            };
            var clear=Part<C.TextBox>("ClearKey");
            clear.TextChanged+=delegate { if(reveal && key.Password!=clear.Text) key.Password=clear.Text; };
            var eye=Part<C.Button>("RevealKey");
            eye.Click+=delegate {
                reveal=!reveal;
                key.Visibility=VisibilityFor(!reveal); clear.Visibility=VisibilityFor(reveal);
                Part<W.Shapes.Path>("EyeSlash").Visibility=VisibilityFor(reveal);
                if(reveal) {clear.Text=key.Password;clear.Focus();clear.CaretIndex=clear.Text.Length;}
                else {clear.Clear();key.Focus();}
                eye.ToolTip=reveal?"隐藏密钥":"显示密钥"; Automation.SetName(eye,(string)eye.ToolTip);
            };
            Part<C.Button>("Test").Click+=async delegate { await TestConnection(); };
        }

        void OpenLink(string target)
        {
            try { Process.Start(new ProcessStartInfo(target){UseShellExecute=true}); }
            catch(Exception ex) { ShowError("打开失败。"+ex.Message); }
        }

        void BindAbout()
        {
            var assembly=typeof(SettingsDialog).Assembly;
            var version=FileVersionInfo.GetVersionInfo(assembly.Location);
            Part<C.TextBlock>("Version").Text="版本 "+(String.IsNullOrWhiteSpace(version.ProductVersion) ? assembly.GetName().Version.ToString() : version.ProductVersion);
            var panel=Part<C.WrapPanel>("Licenses");
            string[] paths={@"Fonts\LICENSE-SourceHanSans.txt",@"Fonts\LICENSE-SourceSans.md",@"FluentIcons\LICENSE"};
            string[] labels={"思源黑体","Source Sans 3","Fluent 图标"};
            for(int i=0;i<paths.Length;i++) {
                string path=Path.Combine(Style.FontRoot,paths[i]);
                var b=new C.Button {Content=labels[i]+" ›",Style=(W.Style)view.FindResource("Action"),Margin=new W.Thickness(0,0,6,6)};
                Automation.SetName(b,labels[i]+"许可证");
                b.Click+=delegate {
                    try { var text=File.ReadAllText(path); Part<C.TextBox>("LicenseText").Text=text; Part<C.TextBox>("LicenseText").Visibility=W.Visibility.Visible; }
                    catch(IOException ex) {ShowError("无法读取许可证。"+ex.Message);}
                    catch(UnauthorizedAccessException ex) {ShowError("无法读取许可证。"+ex.Message);}
                }; panel.Children.Add(b);
            }
            var toggle=Part<C.CheckBox>("Startup");
            try {using(var run=Registry.CurrentUser.OpenSubKey(RunPath)) toggle.IsChecked=run!=null && run.GetValue("RuxuAI")!=null;}
            catch(Exception ex) {toggle.IsEnabled=false; toggle.ToolTip=ex.Message;}
            toggle.Click+=delegate {
                try {
                    using(var run=Registry.CurrentUser.CreateSubKey(RunPath)) {
                        if(toggle.IsChecked==true) run.SetValue("RuxuAI","\""+Application.ExecutablePath+"\"",RegistryValueKind.String);
                        else run.DeleteValue("RuxuAI",false);
                    }
                    ShowSaved();
                } catch(Exception ex) {toggle.IsChecked=toggle.IsChecked!=true;ShowError("启动选项未能保存。"+ex.Message);}
            };
        }

        void BindFontSize()
        {
            var slider=Part<C.Slider>("AnswerSize");
            SyncFontSize();
            ReaderPreferences.FontSizeChanged+=SyncFontSize;
            slider.ValueChanged+=delegate {
                int value=(int)Math.Round(slider.Value);
                pendingSize=value!=ReaderPreferences.FontSize?(int?)value:null;
                Part<C.TextBlock>("SizeValue").Text=value+" pt";
                UpdateFooter();
            };
        }

        void SyncFontSize()
        {
            pendingSize=null;
            Part<C.Slider>("AnswerSize").Value=ReaderPreferences.FontSize;
            Part<C.TextBlock>("SizeValue").Text=ReaderPreferences.FontSize+" pt";
        }

        internal void BindShortcuts(KeysWindow keys,Action changed)
        {
            var rows=Part<C.StackPanel>("ShortcutRows");
            var feedback=Part<C.TextBlock>("ShortcutMessage");
            for(int i=0;i<KeysWindow.ActionNames.Length;i++) {
                int index=i;
                var row=new C.DockPanel {Margin=new W.Thickness(0,4,0,4)};
                var title=new C.TextBlock {Text=KeysWindow.ActionNames[i],VerticalAlignment=W.VerticalAlignment.Center};
                var record=new C.Button {MinWidth=128,MinHeight=36,Style=(W.Style)view.FindResource("Action")};
                System.Windows.Automation.AutomationProperties.SetName(record,"录制"+title.Text+"快捷键");
                C.DockPanel.SetDock(record,C.Dock.Right); row.Children.Add(record);row.Children.Add(title);rows.Children.Add(row);
                Action updateLabel=delegate {string label=keys.Label(index);record.Content=label.Length==0?"未设置":label;};
                updateLabel();
                Action<int> apply=delegate(int combination) {
                    keys.Recording=null;
                    string issue=keys.Change(index,combination);updateLabel();
                    feedback.Text=issue??"✓ 已保存";feedback.SetResourceReference(C.TextBlock.ForegroundProperty,issue==null?"InkBrush":"ErrorBrush");
                    if(issue==null) changed();
                };
                record.Click+=delegate {record.Focus();record.Content="请按下组合键…";feedback.Text="";keys.Recording=apply;};
                record.LostKeyboardFocus+=delegate {if(keys.Recording==apply){keys.Recording=null;updateLabel();}};
                record.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e) {
                    if(keys.Recording!=apply) return;
                    e.Handled=true;
                    var k=e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key;
                    if(k==System.Windows.Input.Key.Escape) {keys.Recording=null;updateLabel();return;}
                    if(k==System.Windows.Input.Key.Back) {apply(0);return;}
                    int vk=System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
                    if(vk==0 || vk==91 || vk==92 || (vk>=160 && vk<=165)) return;
                    int mods=(int)System.Windows.Input.Keyboard.Modifiers;
                    if(mods==0){feedback.Text="请同时按住 Ctrl、Alt、Shift 或 Win。";return;}
                    apply((mods<<16)|vk);
                };
            }
            Disposed+=delegate {keys.Recording=null;};
        }

        public void SetHistoryCount(int count)
        {
            string title = "阅读记录（" + count + "）";
            Part<C.Button>("History").Content = title;
            Part<C.Button>("SmallHistory").Content = title;
        }

        void ConnectMode(AppearanceMode mode)
        {
            Part<C.RadioButton>(mode + "Mode").Checked += delegate {
                if (syncingAppearance) return;
                pendingMode=mode!=AppAppearance.Mode?(AppearanceMode?)mode:null;
                UpdateFooter();
            };
        }

        void SyncAppearance()
        {
            syncingAppearance = true;
            try { Part<C.RadioButton>((pendingMode??AppAppearance.Mode) + "Mode").IsChecked = true; }
            finally { syncingAppearance = false; }
        }

        void SetBrush(string name, Color color)
        {
            view.Resources[name] = Theme.Brush(color);
        }

        void SetGlassBrush(string name, Color upper, Color lower)
        {
            var brush = new Media.LinearGradientBrush(
                Media.Color.FromArgb(upper.A,upper.R,upper.G,upper.B),
                Media.Color.FromArgb(lower.A,lower.R,lower.G,lower.B), 90);
            brush.Freeze();
            view.Resources[name] = brush;
        }

        void ApplyTheme(bool dark)
        {
            bool contrast = SystemInformation.HighContrast;
            BackColor = AppAppearance.Background(dark);
            SetBrush("PageBrush", BackColor);
            SetBrush("InkBrush", AppAppearance.Ink(dark));
            SetBrush("MutedBrush", AppAppearance.Muted(dark));
            SetBrush("SurfaceBrush", AppAppearance.Surface(dark));
            if (contrast) {
                SetBrush("ActionBrush", SystemColors.Control);
                SetBrush("SideBrush", SystemColors.Window);
            } else {
                SetGlassBrush("ActionBrush",Theme.Surface(dark),Theme.SurfaceHover(dark));
                SetGlassBrush("SideBrush",Theme.Surface(dark),Theme.Background(dark));
            }
            SetBrush("BorderBrush", Theme.Line(dark));
            SetBrush("ScrollLine",Theme.Line(dark));
            SetBrush("ScrollMuted",Theme.Muted(dark));
            SetBrush("HoverBrush",Theme.SurfaceHover(dark));
            SetBrush("ActiveBrush",Theme.Selected(dark));
            SetBrush("ActiveTextBrush",Theme.OnSelected(dark));
            SetBrush("FocusBrush",Theme.Accent(dark));
            SetBrush("PrimaryBrush", AppAppearance.Accent(dark));
            SetBrush("PrimaryTextBrush",Theme.OnAccent);
            SetBrush("ErrorBrush",Theme.Danger(dark));
            SetBrush("SuccessBrush",Theme.Success(dark));
            SetBrush("LightPreview",Theme.Background(false)); SetBrush("DarkPreview",Theme.Background(true));
            SyncAppearance();
        }

        void UpdateHints()
        {
            Part<C.TextBlock>("AddressHint").Visibility = VisibilityFor(address.Text.Length == 0);
            Part<C.TextBlock>("ModelHint").Visibility = VisibilityFor(model.Text.Length == 0);
            Part<C.TextBlock>("KeyHint").Visibility = VisibilityFor(key.Password.Length == 0);
            Part<C.TextBlock>("PromptHint").Visibility = VisibilityFor(prompt.Text.Length == 0);
            Part<C.Button>("ClearPrompt").IsEnabled = prompt.Text.Length != 0;
            ValidateConnection();
            UpdateFooter();
        }

        static W.Visibility VisibilityFor(bool visible)
        {
            return visible ? W.Visibility.Visible : W.Visibility.Collapsed;
        }

        void ConnectTab(string name, SettingsPage target)
        {
            Part<C.RadioButton>(name).Checked += delegate { SwitchPage(target); };
        }

        void SwitchPage(SettingsPage target)
        {
            if (syncing) return;
            syncing = true;
            try {
                page = target;
                if(target!=SettingsPage.Connection) ResetTest();
                Part<C.RadioButton>(target + "Nav").IsChecked = true;
                Part<C.RadioButton>("Small" + target).IsChecked = true;
                foreach (SettingsPage entry in Enum.GetValues(typeof(SettingsPage)))
                    Part<C.StackPanel>(entry + "Page").Visibility = VisibilityFor(entry == target);
                Part<C.ScrollViewer>("Body").ScrollToTop();
                UpdateFooter();
            } finally { syncing = false; }
        }

        void SetColumns()
        {
            bool narrow = view.ActualWidth < 650 || (Parent != null && Parent.FindForm() is Home);
            Part<C.ColumnDefinition>("Rail").Width = new W.GridLength(narrow ? 0 : 172);
            Part<C.Border>("Sidebar").Visibility = VisibilityFor(!narrow);
            Part<C.Border>("CompactNav").Visibility = VisibilityFor(narrow);
            Part<C.StackPanel>("PageInsets").Margin = narrow ? new W.Thickness(16, 12, 16, 16) : new W.Thickness(32, 24, 32, 24);
            ArrangeFeedback();
        }

        bool ConnectionEdited()
        {
            return address.Text.Trim() != (original.BaseUrl ?? "") || model.Text.Trim() != (original.Model ?? "") ||
                key.Password.Trim() != (original.ApiKey ?? "");
        }

        void UpdateSpeed()
        {
            Part<C.TextBlock>("SpeedDescription").Text = fastReply.IsChecked == true
                ? "优先用一两句给出结论。DeepSeek 官方接口会自动关闭思考，手动指定的思考参数优先。"
                : "按阅读模式和回答偏好解释，使用下方所选的思考模式。";
            UpdateFooter();
        }

        bool ReadingOptionsEdited()
        {
            return (fastReply.IsChecked == true) != original.FastReply ||
                (useText.IsChecked == true) != original.ReadRegionAsText ||
                (articleImage.IsChecked == true) != original.ArticleAsLongImage ||
                thinking.SelectedIndex != Math.Max(0, Array.IndexOf(modes, original.ThinkingMode));
        }

        bool ProfileEdited()
        {
            return selectedProfile != ReadingProfile.Find(original.ReadingProfileId);
        }

        static string ComparablePrompt(string text)
        {
            return String.IsNullOrWhiteSpace(text) ? "" : text.Replace("\r\n","\n").Replace('\r','\n');
        }

        bool PromptEdited()
        {
            return ComparablePrompt(prompt.Text) != ComparablePrompt(original.AnswerPreference);
        }

        void UpdateFooter()
        {
            Part<C.WrapPanel>("ConnectionActions").Visibility=VisibilityFor(page==SettingsPage.Connection);
            ArrangeFeedback();
            if(!ready) return;
            debounce.Stop();
            if(pendingMode.HasValue || pendingSize.HasValue || ReadingOptionsEdited() || ProfileEdited() || PromptEdited()) debounce.Start();
        }

        void ArrangeFeedback()
        {
            var status=Part<C.TextBlock>("Feedback");
            bool connection=page==SettingsPage.Connection, below=connection && view.ActualWidth<560;
            C.Grid.SetColumn(status,connection && !below?1:0);
            C.Grid.SetColumnSpan(status,connection && !below?1:2);
            C.Grid.SetRow(status,below?1:0);
            status.Margin=new W.Thickness(connection && !below?12:0,below?4:0,0,0);
        }

        internal bool CanLeave() { return discardConfirmed || ConfirmDiscard(); }

        bool ConfirmDiscard()
        {
            if(!FlushPreferences()) return false;
            if (!ConnectionEdited()) return true;
            return MessageBox.Show(this, "模型连接还有未保存的修改。放弃修改并离开？", "如许AI",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if(ready) FlushPreferences();
            base.OnFormClosing(e);
            if (e.Cancel || DialogResult == DialogResult.OK || discardConfirmed ||
                e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall ||
                e.CloseReason == CloseReason.FormOwnerClosing || e.CloseReason == CloseReason.TaskManagerClosing) return;
            if (!ConfirmDiscard()) e.Cancel = true;
        }

        void ShowError(string message)
        {
            savedNotice.Stop();
            Part<C.TextBlock>("Feedback").Text="";
            error.Text = message;
            error.Visibility = W.Visibility.Visible;
        }

        void ShowSaved()
        {
            error.Visibility=W.Visibility.Collapsed;
            Feedback("✓ 已保存",true);
            savedNotice.Stop(); savedNotice.Start();
        }

        void Feedback(string message,bool success)
        {
            var label=Part<C.TextBlock>("Feedback");
            label.SetResourceReference(C.TextBlock.ForegroundProperty,success?"SuccessBrush":"ErrorBrush");
            label.Text=message;
        }

        ModelSettings Draft()
        {
            return new ModelSettings {
                BaseUrl=address.Text.Trim(), Model=model.Text.Trim(), ApiKey=key.Password.Trim(),
                ThinkingMode=modes[Math.Max(0,thinking.SelectedIndex)], ReadRegionAsText=useText.IsChecked==true,
                FastReply=fastReply.IsChecked==true, ReadingProfileId=selectedProfile.Id,
                AnswerPreference=prompt.Text, ArticleAsLongImage=articleImage.IsChecked==true
            };
        }

        bool FlushPreferences()
        {
            debounce.Stop();
            bool options=ReadingOptionsEdited(),profile=ProfileEdited(),answer=PromptEdited();
            if(!options && !profile && !answer && !pendingMode.HasValue && !pendingSize.HasValue) return true;
            try {
                if(pendingMode.HasValue) {AppAppearance.SetMode(pendingMode.Value);pendingMode=null;}
                if(pendingSize.HasValue) {ReaderPreferences.FontSize=pendingSize.Value;pendingSize=null;}
                if(options || profile || answer) {
                    var candidate=Draft();
                    Saved=candidate.MergeChanges(false,options,profile,answer,original);
                    // Preserve the form baseline when another open window changes a quick option.
                    candidate.BaseUrl=original.BaseUrl; candidate.Model=original.Model; candidate.ApiKey=original.ApiKey;
                    original=candidate;
                    var notify=PreferencesSaved; if(notify!=null) notify(Saved);
                }
                ShowSaved(); return true;
            } catch(Exception ex) { ShowError("偏好未能保存。"+ex.Message); return false; }
        }

        void SaveSettings()
        {
            if(!FlushPreferences() || !ValidateConnection()) return;
            try {
                Saved=Draft().SaveChanges(true,false,false,false);
                original=Saved; ConnectionSaved=true;
                DialogResult = DialogResult.OK;
                if (!TopLevel) Close();
            } catch (Exception ex) {
                ShowError(ex.Message);
            }
        }

        void MarkField(string name,bool invalid)
        {
            var frame=Part<C.Border>(name+"Frame");
            if(invalid) frame.SetResourceReference(C.Border.BorderBrushProperty,"ErrorBrush");
            else frame.ClearValue(C.Border.BorderBrushProperty);
        }

        bool ValidateConnection()
        {
            Uri endpoint;
            bool validAddress=new ModelSettings {BaseUrl=address.Text}.TryEndpoint(out endpoint);
            bool validModel=!String.IsNullOrWhiteSpace(model.Text);
            foreach(var name in new[]{"Address","Model"}) {
                bool valid=name=="Address"?validAddress:validModel;
                var issue=Part<C.TextBlock>(name+"Issue");
                issue.Text=valid?"":name=="Address"?"请输入 HTTPS 地址。本机服务也可以使用 HTTP。":"请输入服务商提供的模型名称。";
                issue.Visibility=VisibilityFor(!valid); MarkField(name,!valid);
            }
            Part<C.Button>("Test").IsEnabled=validAddress && validModel && pendingTest==null;
            Part<C.Button>("Save").IsEnabled=validAddress && validModel;
            return validAddress && validModel;
        }

        void CancelTest()
        {
            var pending=pendingTest; pendingTest=null;
            if(pending!=null) pending.Cancel();
        }

        void ResetTest()
        {
            CancelTest(); MarkField("Key",false);
            Part<C.Button>("Test").Content="测试连接";
            if(page==SettingsPage.Connection) {
                Part<C.TextBlock>("Feedback").Text="";
                error.Visibility=W.Visibility.Collapsed;
            }
            ValidateConnection();
        }

        async System.Threading.Tasks.Task TestConnection()
        {
            if(pendingTest!=null || !ValidateConnection()) return;
            ResetTest(); savedNotice.Stop();
            var test=Part<C.Button>("Test");
            var pending=new CancellationTokenSource(); pendingTest=pending;
            var content=new C.StackPanel {Orientation=C.Orientation.Horizontal};
            var transform=new Media.RotateTransform();
            var ring=new W.Shapes.Path {Width=14,Height=14,Stretch=Media.Stretch.Uniform,StrokeThickness=2,
                Data=Media.Geometry.Parse("M 7,1 A 6,6 0 1 1 1,7"),Margin=new W.Thickness(0,0,6,0),
                RenderTransform=transform,RenderTransformOrigin=new W.Point(.5,.5)};
            ring.SetResourceReference(W.Shapes.Shape.StrokeProperty,"MutedBrush");
            content.Children.Add(ring);content.Children.Add(new C.TextBlock{Text="测试中…"});
            Action refreshMotion=delegate {
                if(pendingTest!=pending || IsDisposed) return;
                var spin=Motion.Enabled ? new Media.Animation.DoubleAnimation(0,360,new W.Duration(TimeSpan.FromMilliseconds(850))) {
                    RepeatBehavior=Media.Animation.RepeatBehavior.Forever
                } : null;
                transform.BeginAnimation(Media.RotateTransform.AngleProperty,spin);
            };
            System.ComponentModel.PropertyChangedEventHandler motionChanged=delegate(object sender,System.ComponentModel.PropertyChangedEventArgs e) {
                if(String.IsNullOrEmpty(e.PropertyName) || e.PropertyName=="ClientAreaAnimation") {
                    if(view.Dispatcher.CheckAccess()) refreshMotion(); else view.Dispatcher.BeginInvoke(refreshMotion);
                }
            };
            W.SystemParameters.StaticPropertyChanged+=motionChanged; refreshMotion();
            test.Content=content;test.IsEnabled=false;
            var watch=Stopwatch.StartNew();
            try {
                await ModelClient.TestConnectionAsync(Draft(),pending.Token);
                if(pendingTest==pending && !IsDisposed) Feedback("✓ 连接成功 · "+watch.ElapsedMilliseconds+" ms",true);
            } catch(OperationCanceledException) { }
            catch(Exception ex) {
                if(pendingTest==pending && !IsDisposed) {
                    Feedback(ex.Message,false);
                    string field=ex.Message.Contains("401") || ex.Message.Contains("403") ? "Key" :
                        ex.Message.Contains("404") || ex.Message.IndexOf("model",StringComparison.OrdinalIgnoreCase)>=0 ? "Model":"Address";
                    MarkField(field,true);
                }
            } finally {
                W.SystemParameters.StaticPropertyChanged-=motionChanged;
                transform.BeginAnimation(Media.RotateTransform.AngleProperty,null);
                if(pendingTest==pending && !IsDisposed) {
                    pendingTest=null;test.Content="测试连接";test.IsEnabled=true;
                }
                pending.Dispose();
            }
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr handle);

        protected override void OnLoad(EventArgs e)
        {
            if (!TopLevel) {base.OnLoad(e); return;}
            var area = (Owner == null ? Screen.FromPoint(Cursor.Position) : Screen.FromControl(Owner)).WorkingArea;
            Location = area.Location;
            double scale;
            try { scale = Math.Max(96u, GetDpiForWindow(Handle)) / 96.0; }
            catch (EntryPointNotFoundException) { using (var g = CreateGraphics()) scale = g.DpiX / 96.0; }
            int margin = (int)Math.Round(14 * scale);
            int w = Math.Max(1, area.Width - margin * 2), h = Math.Max(1, area.Height - margin * 2);
            MinimumSize = new Size(Math.Min(w,(int)(410 * scale)), Math.Min(h,(int)(340 * scale)));
            var preferred = SizeFromClientSize(new Size((int)(776 * scale), (int)(568 * scale)));
            Size = new Size(Math.Min(w,preferred.Width), Math.Min(h,preferred.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            base.OnLoad(e);
        }
    }
}
