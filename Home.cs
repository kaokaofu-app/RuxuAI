using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Markup;
using System.Xml;
using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;

namespace Jiyue
{
    internal sealed class Home : AppearanceForm
    {
        internal readonly Panel ContentArea=new Panel {Dock=DockStyle.Fill};
        internal readonly HistoryWindow HistoryPage=new HistoryWindow();
        readonly LinkLabel status;
        readonly Label warning;
        readonly C.Border rail;
        readonly ElementHost sidebar;
        readonly Dictionary<string,C.RadioButton> profiles=new Dictionary<string,C.RadioButton>();
        readonly Dictionary<string,C.TextBlock> shortcutLabels=new Dictionary<string,C.TextBlock>();
        readonly W.FrameworkElement welcome, empty;
        readonly C.TextBlock[] stepNumbers=new C.TextBlock[3];
        bool ready, conflict, inSettings;
        string configError;
        public event Action ConnectionRequested, ShortcutsRequested;

        public Home(Action region,Action article,Action longImage,Action history,Action settings,Action<string,bool?> change)
        {
            Style.Form(this,"如许AI · 屏幕解释助手",790,500);
            MinimumSize=new Size(465,350);
            using(var stream=typeof(Home).Assembly.GetManifestResourceStream("Jiyue.HomeRail.xaml"))
                rail=(C.Border)XamlReader.Load(stream);
            Theme.ApplyWpf(rail);
            sidebar=new ElementHost {Dock=DockStyle.Left,Width=200,Child=rail,AccessibleName="阅读侧栏"};
            Wire("Region","框选解释","screenshot",region);
            Wire("Article","读取整篇","document",article);
            Wire("LongImage","滚轮长截图","arrow_down",longImage);
            Wire("History","阅读记录","history",history);
            Wire("Settings","设置","settings",settings);
            foreach(var item in ReadingProfile.All) {
                var id=item.Id;
                var button=new C.RadioButton {Content=item.Label,ToolTip=item.Description,GroupName="Profile",Style=(W.Style)rail.FindResource("Chip")};
                System.Windows.Automation.AutomationProperties.SetName(button,item.Label);
                button.Click+=delegate {change(id,null);};
                ((C.Primitives.UniformGrid)rail.FindName("Profiles")).Children.Add(button);
                profiles.Add(id,button);
            }
            Speed(false).Click+=delegate {change(null,false);};
            Speed(true).Click+=delegate {change(null,true);};
            Speed(false).ToolTip="保留较完整的背景和解释";
            Speed(true).ToolTip="优先给出简短结论，减少等待";
            welcome=LoadView("Welcome"); empty=LoadView("EmptyHistory");
            string[] steps={"连接模型","框选一段内容","读整篇文章"};
            var stepList=(C.WrapPanel)welcome.FindName("Steps");
            for(int i=0;i<steps.Length;i++) {
                var row=new C.StackPanel {Orientation=C.Orientation.Horizontal,Margin=new W.Thickness(0,4,12,0)};
                var marker=new C.Border {Width=20,Height=20,CornerRadius=new W.CornerRadius(10)};
                marker.SetResourceReference(C.Border.BackgroundProperty,"Selected");
                stepNumbers[i]=new C.TextBlock {FontSize=12,HorizontalAlignment=W.HorizontalAlignment.Center,VerticalAlignment=W.VerticalAlignment.Center};
                marker.Child=stepNumbers[i]; row.Children.Add(marker);
                row.Children.Add(new C.TextBlock {Text=steps[i],Margin=new W.Thickness(4,0,0,0),VerticalAlignment=W.VerticalAlignment.Center});
                stepList.Children.Add(row);
            }
            ((C.Button)welcome.FindName("Connect")).Click+=delegate {var h=ConnectionRequested; if(h!=null) h();else settings();};
            ((C.Button)welcome.FindName("LearnKeys")).Click+=delegate {var h=ShortcutsRequested;if(h!=null) h();else settings();};
            ((C.Button)empty.FindName("TryRegion")).Click+=delegate {region();};
            ((C.ContentControl)empty.FindName("HistoryIcon")).Content=RailIcon("history",48,"Muted");
            HistoryPage.TopLevel=false; HistoryPage.FormBorderStyle=FormBorderStyle.None; HistoryPage.MinimumSize=Size.Empty; HistoryPage.Dock=DockStyle.Fill;
            HistoryPage.AttachViews(welcome,empty);
            HistoryPage.RecordsChanged+=RefreshGuide;
            SetShortcuts("Ctrl+Alt+Q","Ctrl+Alt+E");
            status=new LinkLabel {Dock=DockStyle.Bottom,AutoSize=true,Visible=false,Padding=new Padding(16,4,12,12),Font=Style.Font(9),
                Text="快捷键被占用，点此修改",AccessibleName="修改被占用的快捷键",LinkBehavior=LinkBehavior.HoverUnderline};
            status.LinkClicked+=delegate {var h=ShortcutsRequested;if(h!=null) h();else settings();};
            warning=new Label {Dock=DockStyle.Bottom,AutoSize=true,Padding=new Padding(20,3,12,3),Visible=false};
            ContentArea.Controls.Add(HistoryPage); ContentArea.Controls.Add(warning); ContentArea.Controls.Add(status);
            Controls.Add(ContentArea); Controls.Add(sidebar); HistoryPage.Show(); SelectPage(false);
            AppAppearance.Bind(this,delegate(bool dark) {
                Color bg=AppAppearance.Background(dark),ink=AppAppearance.Ink(dark),muted=AppAppearance.Muted(dark);
                BackColor=ContentArea.BackColor=bg; status.LinkColor=status.ActiveLinkColor=status.VisitedLinkColor=SystemInformation.HighContrast?SystemColors.HotTrack:AppAppearance.Accent(dark); warning.ForeColor=SystemInformation.HighContrast?ink:Theme.Danger(dark);
                Brush("OnAccent",SystemInformation.HighContrast?SystemColors.HighlightText:Color.White);
                Brush("Surface",AppAppearance.Surface(dark)); Brush("Ink",ink); Brush("Muted",muted);
                Brush("Line",Theme.Line(dark)); Brush("ScrollLine",Theme.Line(dark)); Brush("ScrollMuted",muted);
                Brush("Soft",AppAppearance.Tint(dark)); Brush("Accent",AppAppearance.Accent(dark));
                Brush("Selected",SystemInformation.HighContrast?SystemColors.Control:Theme.Selected(dark));
            });
            ResumeLayout(true);
        }
        W.FrameworkElement LoadView(string key)
        {
            var view=(W.FrameworkElement)((W.DataTemplate)rail.FindResource(key)).LoadContent();
            view.Resources.MergedDictionaries.Add(rail.Resources);
            System.Windows.Documents.TextElement.SetFontFamily(view,System.Windows.Documents.TextElement.GetFontFamily(rail));
            return view;
        }
        C.RadioButton Speed(bool fast) {return (C.RadioButton)rail.FindName(fast?"Fast":"Normal");}
        void Brush(string key,Color value) {rail.Resources[key]=new M.SolidColorBrush(M.Color.FromRgb(value.R,value.G,value.B));}
        void Wire(string name,string title,string icon,Action action)
        {
            var button=(C.Button)rail.FindName(name);
            var line=new C.DockPanel();
            if(name=="Region" || name=="Article" || name=="LongImage") {
                var label=new C.TextBlock {FontSize=10.5,VerticalAlignment=W.VerticalAlignment.Center,Margin=new W.Thickness(2,0,0,0),
                    MaxWidth=74,TextTrimming=W.TextTrimming.CharacterEllipsis};
                label.SetResourceReference(C.TextBlock.ForegroundProperty,"Muted");
                C.DockPanel.SetDock(label,C.Dock.Right);line.Children.Add(label);shortcutLabels.Add(name,label);
            }
            var image=RailIcon(icon,16,"Ink"); image.Margin=new W.Thickness(0,0,6,0);
            C.DockPanel.SetDock(image,C.Dock.Left);line.Children.Add(image);
            line.Children.Add(new C.TextBlock {Text=title,VerticalAlignment=W.VerticalAlignment.Center,TextTrimming=W.TextTrimming.CharacterEllipsis});
            System.Windows.Automation.AutomationProperties.SetName(button,title);
            button.Content=line; button.Click+=delegate {action();};
        }
        internal static System.Windows.Shapes.Path RailIcon(string name,int size,string brush)
        {
            var xml=new XmlDocument {XmlResolver=null};
            using(var stream=typeof(Home).Assembly.GetManifestResourceStream("Fluent."+name+".svg")) xml.Load(stream);
            var group=new M.GeometryGroup {FillRule=M.FillRule.Nonzero};
            foreach(XmlElement element in xml.GetElementsByTagName("path")) group.Children.Add(M.Geometry.Parse(element.GetAttribute("d")));
            var image=new System.Windows.Shapes.Path {Width=size,Height=size,Stretch=M.Stretch.Uniform,Data=group};
            image.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,brush);
            return image;
        }
        internal void SelectPage(bool settings)
        {
            inSettings=settings; status.Visible=conflict && !settings;
            ((C.Button)rail.FindName("Settings")).Tag=settings?"active":null;
            ((C.Button)rail.FindName("History")).Tag=settings?null:"active";
        }
        internal void SyncOptions(ModelSettings s) {profiles[ReadingProfile.Find(s.ReadingProfileId).Id].IsChecked=true;Speed(s.FastReply).IsChecked=true;}
        public void HistoryStatus(string text) {warning.Text=text??""; warning.Visible=!String.IsNullOrEmpty(text);}
        public void Status(bool q,bool e,bool ready,string error)
        {
            this.ready=ready;configError=error;conflict=!q || !e;
            status.Visible=conflict && !inSettings; RefreshGuide();
        }
        void RefreshGuide()
        {
            stepNumbers[0].Text=ready && configError==null?"✓":"1";
            stepNumbers[1].Text=HistoryPage.HasRecords?"✓":"2"; stepNumbers[2].Text="3";
            ((C.TextBlock)welcome.FindName("GuideHint")).Text=configError!=null?"模型配置读取失败，请重新连接。":"三步开始阅读";
            HistoryPage.ShowWelcome(!ready || configError!=null || !HistoryPage.HasRecords);
        }
        public void SetShortcuts(string region,string page) {SetShortcuts(region,page,null);}
        public void SetShortcuts(string region,string page,string longImage)
        {
            string[] keys={region,page,longImage}, names={"Region","Article","LongImage"}, titles={"框选解释","读取整篇","滚轮长截图"};
            var rows=(C.StackPanel)empty.FindName("KeyRows");rows.Children.Clear();
            for(int i=0;i<keys.Length;i++) {
                string key=(keys[i]??"").Replace(" ","");
                shortcutLabels[names[i]].Text=key;
                shortcutLabels[names[i]].Visibility=key.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
                ((C.Button)rail.FindName(names[i])).ToolTip=key.Length>0?titles[i]+" · "+key:titles[i];
                if(i==2 || key.Length==0) continue;
                var row=new C.WrapPanel {Margin=new W.Thickness(0,2,0,2)};
                foreach(string part in key.Split('+')) {
                    var cap=new C.Border {BorderThickness=new W.Thickness(1,1,1,2),CornerRadius=new W.CornerRadius(6),
                        Padding=new W.Thickness(4,1,4,1),Margin=new W.Thickness(0,0,3,0),Child=new C.TextBlock {Text=part,FontSize=12.5}};
                    cap.SetResourceReference(C.Border.BackgroundProperty,"Surface");cap.SetResourceReference(C.Border.BorderBrushProperty,"Line");
                    row.Children.Add(cap);
                }
                row.Children.Add(new C.TextBlock {Text=titles[i],FontSize=12.5,VerticalAlignment=W.VerticalAlignment.Center,Margin=new W.Thickness(4,0,0,0)});
                rows.Children.Add(row);
            }
            ((C.TextBlock)empty.FindName("KeyHint")).Visibility=rows.Children.Count>0?W.Visibility.Visible:W.Visibility.Collapsed;
            HistoryPage.SizeHomeViews();
        }
    }
}
