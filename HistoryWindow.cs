using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using W=System.Windows;
using C=System.Windows.Controls;
using M=System.Windows.Media;
using I=System.Windows.Input;
using A=System.Windows.Media.Animation;
using B=System.Windows.Media.Imaging;

namespace Jiyue
{
    internal sealed class HistoryWindow : AppearanceForm
    {
        readonly Panel scroll;
        readonly TableLayoutPanel frame;
        readonly ElementHost guide=new ElementHost(), empty=new ElementHost(), recordsHost=new ElementHost();
        readonly C.StackPanel content=new C.StackPanel(), rows=new C.StackPanel();
        readonly C.TextBox search=new C.TextBox();
        readonly C.TextBlock hint=new C.TextBlock();
        readonly Timer debounce=new Timer {Interval=200};
        readonly Dictionary<string,HistoryCard> cards=new Dictionary<string,HistoryCard>(StringComparer.Ordinal);
        List<ConversationRecord> recent=new List<ConversationRecord>();
        List<HistoryCard> visibleCards=new List<HistoryCard>();
        bool showGuide,measuring,extentQueued;

        public bool HasRecords {get; private set;}
        public event Action RecordsChanged;
        public event Action<string,Point> OpenRequested;
        public event Action<string> DeleteRequested;

        public HistoryWindow()
        {
            Style.Form(this,"如许AI · 阅读历史",540,500);
            MinimumSize=new Size(350,270);
            frame=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
                Padding=new Padding(16),ColumnCount=1,RowCount=4};
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            for(int i=0;i<4;i++) frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading=new Label {Text="阅读记录",AutoSize=true,Dock=DockStyle.Fill,Font=Style.Font(15,true),
                ForeColor=Style.Ink,Margin=new Padding(0,0,0,12)};
            scroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};
            foreach(var host in new[]{guide,empty,recordsHost}) {host.Dock=DockStyle.Top;host.Margin=Padding.Empty;host.Visible=false;}
            BuildSearch();
            content.Children.Add(rows);
            recordsHost.Child=content;
            frame.Controls.Add(guide,0,0);frame.Controls.Add(heading,0,1);
            frame.Controls.Add(empty,0,2);frame.Controls.Add(recordsHost,0,3);
            scroll.Controls.Add(frame);Controls.Add(scroll);
            frame.SizeChanged+=delegate {
                SizeHomeViews();
                QueueScrollExtent();
            };
            scroll.SizeChanged+=delegate {SizeHomeViews();};
            scroll.HandleCreated+=delegate {AppAppearance.ScrollColors(scroll,AppAppearance.IsDark);};
            debounce.Tick+=delegate {debounce.Stop();RefreshRows();scroll.AutoScrollPosition=Point.Empty;};
            content.PreviewMouseWheel+=delegate(object sender,I.MouseWheelEventArgs e) {
                if(!scroll.VerticalScroll.Visible || SystemInformation.MouseWheelScrollLines==0) return;
                var pos=scroll.AutoScrollPosition;
                var step=SystemInformation.MouseWheelScrollLines<0?scroll.ClientSize.Height:Math.Min(48,16*SystemInformation.MouseWheelScrollLines);
                scroll.AutoScrollPosition=new Point(-pos.X,Math.Max(0,-pos.Y-(int)Math.Round(step*e.Delta/120d)));
                e.Handled=true;
            };
            AppAppearance.Bind(this,delegate(bool dark) {
                BackColor=AppAppearance.Background(dark);ForeColor=heading.ForeColor=Theme.Ink(dark);
                guide.BackColor=empty.BackColor=recordsHost.BackColor=BackColor;
                var color=Theme.SurfaceHover(dark);
                content.Resources["Soft"]=new M.SolidColorBrush(M.Color.FromRgb(color.R,color.G,color.B));
                AppAppearance.ScrollColors(scroll,dark);
            });
            ResumeLayout(true);
        }

        void BuildSearch()
        {
            var box=new C.Border {CornerRadius=new W.CornerRadius(Theme.RadiusMedium),BorderThickness=new W.Thickness(1),Padding=new W.Thickness(12,8,12,8),Margin=new W.Thickness(0,0,0,8)};
            box.SetResourceReference(C.Border.BackgroundProperty,"Surface");box.SetResourceReference(C.Border.BorderBrushProperty,"Line");
            var line=new C.DockPanel();
            var icon=Home.RailIcon("search",16,"Muted");icon.Margin=new W.Thickness(0,0,8,0);
            C.DockPanel.SetDock(icon,C.Dock.Left);line.Children.Add(icon);
            var overlay=new C.Grid();
            search.Background=M.Brushes.Transparent;search.BorderThickness=new W.Thickness(0);search.FontSize=14;
            search.SetResourceReference(C.Control.ForegroundProperty,"Ink");search.SetResourceReference(C.Primitives.TextBoxBase.CaretBrushProperty,"Ink");
            W.Automation.AutomationProperties.SetName(search,"搜索标题、原文或提问");
            hint.Text="搜索标题、原文或提问";hint.IsHitTestVisible=false;hint.FontSize=14;hint.Margin=new W.Thickness(2,0,0,0);
            hint.SetResourceReference(C.TextBlock.ForegroundProperty,"Muted");
            search.TextChanged+=delegate {hint.Visibility=String.IsNullOrEmpty(search.Text)?W.Visibility.Visible:W.Visibility.Hidden;debounce.Stop();debounce.Start();};
            search.PreviewKeyDown+=delegate(object sender,I.KeyEventArgs e) {
                if(e.Key==I.Key.Down && visibleCards.Count>0) {FocusCard(visibleCards[0]);e.Handled=true;}
            };
            overlay.Children.Add(search);overlay.Children.Add(hint);line.Children.Add(overlay);box.Child=line;content.Children.Add(box);
        }

        internal void AttachViews(W.FrameworkElement welcome,W.FrameworkElement emptyView)
        {
            guide.Child=welcome;empty.Child=emptyView;
            content.Resources.MergedDictionaries.Add(welcome.Resources);
            content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,welcome.GetValue(System.Windows.Documents.TextElement.FontFamilyProperty));
            empty.Visible=!HasRecords;SizeHomeViews();
        }
        internal void ShowWelcome(bool show)
        {
            showGuide=show;guide.Visible=show && guide.Child!=null;SizeHomeViews();
        }
        void QueueScrollExtent()
        {
            if(extentQueued || !IsHandleCreated || IsDisposed || Disposing) return;
            extentQueued=true;
            BeginInvoke(new Action(delegate {
                extentQueued=false;
                if(IsDisposed || Disposing || scroll.IsDisposed) return;
                var extent=new Size(0,frame.Height);
                if(scroll.AutoScrollMinSize!=extent) scroll.AutoScrollMinSize=extent;
                scroll.PerformLayout();
            }));
        }
        internal void SizeHomeViews()
        {
            if(measuring || guide.Child==null || empty.Child==null || !IsHandleCreated || Disposing || IsDisposed) return;
            measuring=true;
            try {
                using(var g=CreateGraphics()) {
                    double scale=g.DpiX/96d;
                    int width=Math.Max(1,frame.ClientSize.Width-frame.Padding.Horizontal);
                    foreach(var host in new[]{guide,empty,recordsHost}) {
                        var view=(W.FrameworkElement)host.Child;
                        view.Measure(new W.Size(width/scale,Double.PositiveInfinity));
                        int h=(int)Math.Ceiling(view.DesiredSize.Height*scale);
                        if(host==empty) {
                            var title=frame.GetControlFromPosition(0,1);
                            if(title!=null) h=Math.Max(h,scroll.ClientSize.Height-frame.Padding.Vertical-title.Height-title.Margin.Vertical-(showGuide?guide.Height:0));
                        }
                        if(host.Height!=h) host.Height=Math.Max(1,h);
                    }
                }
            } finally {measuring=false;}
        }
        protected override void OnShown(EventArgs e) {base.OnShown(e);SizeHomeViews();}
        protected override void Dispose(bool disposing) {if(disposing) debounce.Dispose();base.Dispose(disposing);}

        public void SetRecords(IList<ConversationRecord> records)
        {
            if(IsDisposed || Disposing) return;
            if(InvokeRequired) {
                var snapshot=records==null?null:records.ToList();
                try {BeginInvoke(new Action(delegate {SetRecords(snapshot);}));}
                catch(InvalidOperationException) {if(!IsDisposed && !Disposing) throw;}
                return;
            }
            recent=(records??new List<ConversationRecord>()).Where(r=>r!=null && !String.IsNullOrWhiteSpace(r.Id))
                .OrderByDescending(r=>r.UpdatedUtc).GroupBy(r=>r.Id).Select(g=>g.First()).Take(ConversationStore.Limit).ToList();
            var ids=new HashSet<string>(recent.Select(r=>r.Id),StringComparer.Ordinal);
            foreach(var id in cards.Keys.Where(id=>!ids.Contains(id)).ToArray()) cards.Remove(id);
            HasRecords=recent.Count!=0;recordsHost.Visible=HasRecords;empty.Visible=!HasRecords && empty.Child!=null;
            RefreshRows();
            var handler=RecordsChanged;if(handler!=null) handler();
        }

        static string LastQuestion(ConversationRecord r)
        {
            if(!String.IsNullOrWhiteSpace(r.RetryQuestion)) return r.RetryQuestion;
            if(r.History==null) return "";
            var question=r.History.LastOrDefault(p=>p!=null && p.Length==2 && p[0]=="user" && !String.IsNullOrWhiteSpace(p[1]) &&
                p[1]!="请直接解释框选内容。" && p[1]!="请概括这页的主题和最值得理解的要点。");
            return question==null?"":question[1];
        }
        static bool Matches(ConversationRecord r,string query)
        {
            return query.Length==0 || new[]{r.Title,r.SourceTitle,r.Context,r.RecognizedText,LastQuestion(r)}
                .Any(text=>text!=null && text.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0);
        }
        internal static string DayGroup(DateTime value,DateTime today)
        {
            double days=(today.Date-value.ToLocalTime().Date).TotalDays;
            return days<=0?"今天":days==1?"昨天":days<7?"最近 7 天":"更早";
        }
        void RefreshRows()
        {
            string query=search.Text.Trim();
            var next=new List<HistoryCard>();
            var children=new List<W.UIElement>();
            string group=null;
            foreach(var record in recent.Where(r=>Matches(r,query))) {
                string day=DayGroup(record.UpdatedUtc,DateTime.Today);
                if(day!=group) {
                    group=day;
                    var header=LabelText(day,12,"Muted");header.Margin=new W.Thickness(2,9,0,5);children.Add(header);
                }
                HistoryCard card;
                if(!cards.TryGetValue(record.Id,out card)) cards.Add(record.Id,card=new HistoryCard(this));
                card.Bind(record);next.Add(card);children.Add(card);
            }
            var oldFocus=visibleCards.FirstOrDefault(c=>c.IsKeyboardFocusWithin);
            bool same=next.SequenceEqual(visibleCards) && rows.Children.OfType<C.TextBlock>().Select(x=>x.Text)
                .SequenceEqual(children.OfType<C.TextBlock>().Select(x=>x.Text));
            if(!same || (HasRecords && next.Count==0 && rows.Children.Count==0)) {
                rows.Children.Clear();
                if(HasRecords && next.Count==0) {
                    var none=LabelText("没有找到相关记录",14,"Muted");none.Margin=new W.Thickness(12,20,0,0);rows.Children.Add(none);
                } else foreach(var child in children) rows.Children.Add(child);
                if(oldFocus!=null && next.Count>0)
                    next[Math.Max(0,Math.Min(next.Count-1,visibleCards.IndexOf(oldFocus)))].OpenButton.Focus();
            }
            visibleCards=next;SizeHomeViews();
        }
        static C.TextBlock LabelText(string value,double size,string brush)
        {
            var label=new C.TextBlock {Text=value,FontSize=size,TextTrimming=W.TextTrimming.CharacterEllipsis};
            label.SetResourceReference(C.TextBlock.ForegroundProperty,brush);return label;
        }
        void FocusCard(HistoryCard card)
        {
            card.OpenButton.Focus();
            var point=card.TranslatePoint(new W.Point(0,0),content);
            using(var g=CreateGraphics()) {
                double scale=g.DpiY/96d;
                int top=scroll.PointToClient(recordsHost.PointToScreen(new Point(0,(int)(point.Y*scale)))).Y;
                int bottom=top+(int)Math.Ceiling(card.ActualHeight*scale);
                var pos=scroll.AutoScrollPosition;
                if(top<0 || bottom>scroll.ClientSize.Height)
                    scroll.AutoScrollPosition=new Point(-pos.X,Math.Max(0,-pos.Y+(top<0?top:bottom-scroll.ClientSize.Height)));
            }
        }
        void Open(string id)
        {
            var handler=OpenRequested;if(handler!=null) handler(id,CapturePopup.TriggerPoint(recordsHost));
        }
        void Remove(HistoryCard card)
        {
            if(card.Deleting) return;
            string id=card.Id;card.Deleting=true;
            Action finish=delegate {
                if(IsDisposed || Disposing) return;
                var handler=DeleteRequested;if(handler!=null) handler(id);
                card.BeginAnimation(W.FrameworkElement.HeightProperty,null);card.Deleting=false;
            };
            if(!Motion.Enabled || card.ActualHeight<1) {finish();return;}
            var collapse=new A.DoubleAnimation(card.ActualHeight,0,TimeSpan.FromMilliseconds(150)) {EasingFunction=new A.CubicEase {EasingMode=A.EasingMode.EaseOut}};
            collapse.Completed+=delegate {finish();};card.BeginAnimation(W.FrameworkElement.HeightProperty,collapse);
        }

        internal static string ShortLine(string value, int limit)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var output = new StringBuilder();
            var it = StringInfo.GetTextElementEnumerator(value);
            int count = 0;
            bool gap = false;
            while (it.MoveNext())
            {
                string part = it.GetTextElement();
                if (String.IsNullOrWhiteSpace(part)) { gap = true; continue; }
                if (count++ >= limit) { output.Append('…'); break; }
                if (gap && output.Length > 0) output.Append(' ');
                output.Append(part);
                gap = false;
            }
            return output.ToString();
        }

        static string Preview(ConversationRecord record, string title)
        {
            string question=LastQuestion(record);
            if (!String.IsNullOrWhiteSpace(question)) return "提问 · " + ShortLine(question,60);
            string source = ShortLine(String.IsNullOrWhiteSpace(record.Context) ? record.RecognizedText : record.Context,110);
            string prefix = title.TrimEnd('…');
            if (prefix.Length > 0 && source.StartsWith(prefix,StringComparison.Ordinal))
                source = source.Substring(prefix.Length).TrimStart();
            return source.Length == 0 ? "" : "原文 · " + ShortLine(source,60);
        }

        sealed class HistoryCard : C.Grid
        {
            internal readonly C.Button OpenButton=new C.Button();
            readonly C.Button remove=new C.Button();
            readonly C.Image thumb=new C.Image {Width=58,Height=42,Stretch=M.Stretch.Uniform,Margin=new W.Thickness(0,0,10,0)};
            readonly C.TextBlock title=LabelText("",15,"Ink"),source=LabelText("",12,"Muted"),excerpt=LabelText("",13,"Ink");
            byte[] imageBytes;
            string fullTitle;
            internal string Id {get;private set;}
            internal bool Deleting;

            internal HistoryCard(HistoryWindow owner)
            {
                ClipToBounds=true;Margin=new W.Thickness(0,0,0,6);
                OpenButton.SetResourceReference(C.Control.StyleProperty,"Pill");
                OpenButton.SetResourceReference(C.Control.BackgroundProperty,"Surface");
                OpenButton.Padding=new W.Thickness(12,8,36,8);
                title.FontWeight=W.FontWeights.SemiBold;
                source.Margin=new W.Thickness(0,2,0,0);excerpt.Margin=new W.Thickness(0,4,0,0);
                var line=new C.DockPanel();C.DockPanel.SetDock(thumb,C.Dock.Left);line.Children.Add(thumb);
                var stack=new C.StackPanel();stack.Children.Add(title);stack.Children.Add(source);stack.Children.Add(excerpt);
                line.Children.Add(stack);OpenButton.Content=line;Children.Add(OpenButton);
                OpenButton.Click+=delegate {owner.Open(Id);};
                remove.Content="×";remove.ToolTip="删除记录";remove.FontSize=16;remove.Width=28;remove.Height=28;remove.MinHeight=0;
                remove.Margin=new W.Thickness(0,4,4,0);remove.Padding=new W.Thickness(0);
                remove.SetResourceReference(C.Control.StyleProperty,"Pill");
                remove.HorizontalContentAlignment=W.HorizontalAlignment.Center;remove.HorizontalAlignment=W.HorizontalAlignment.Right;
                remove.VerticalAlignment=W.VerticalAlignment.Top;remove.Visibility=W.Visibility.Hidden;
                W.Automation.AutomationProperties.SetName(remove,"删除记录");
                remove.Click+=delegate {owner.Remove(this);};Children.Add(remove);
                MouseEnter+=delegate {remove.Visibility=W.Visibility.Visible;};MouseLeave+=delegate {if(!IsKeyboardFocusWithin) remove.Visibility=W.Visibility.Hidden;};
                IsKeyboardFocusWithinChanged+=delegate {remove.Visibility=IsKeyboardFocusWithin || IsMouseOver ? W.Visibility.Visible:W.Visibility.Hidden;};
                var menu=new C.ContextMenu();
                Action<string,Action> item=delegate(string caption,Action action) {
                    var entry=new C.MenuItem {Header=caption};entry.Click+=delegate {action();};menu.Items.Add(entry);
                };
                item("打开",delegate {owner.Open(Id);});
                item("复制标题",delegate {
                    try {System.Windows.Forms.Clipboard.SetText(fullTitle);}
                    catch(ExternalException) {Toast.Show("复制失败，请重试",ToastKind.Error,owner.TopLevelControl as Form);}
                });
                item("删除",delegate {owner.Remove(this);});OpenButton.ContextMenu=menu;
                PreviewKeyDown+=delegate(object sender,I.KeyEventArgs e) {
                    if(e.Key==I.Key.Delete) {owner.Remove(this);e.Handled=true;return;}
                    int index=owner.visibleCards.IndexOf(this);
                    if(index<0 || (e.Key!=I.Key.Down && e.Key!=I.Key.Up)) return;
                    index=Math.Max(0,Math.Min(owner.visibleCards.Count-1,index+(e.Key==I.Key.Down?1:-1)));
                    owner.FocusCard(owner.visibleCards[index]);e.Handled=true;
                };
            }
            internal void Bind(ConversationRecord record)
            {
                Id=record.Id;fullTitle=String.IsNullOrWhiteSpace(record.Title)?"阅读对话":record.Title;
                title.Text=ShortLine(fullTitle,80);W.Automation.AutomationProperties.SetName(OpenButton,title.Text);
                if(!Object.ReferenceEquals(imageBytes,record.Image)) {imageBytes=record.Image;thumb.Source=Thumbnail(imageBytes);}
                thumb.Visibility=thumb.Source==null?W.Visibility.Collapsed:W.Visibility.Visible;
                string scope=SourceCard.Caption(record.Scope,ShortLine(record.SourceTitle,60),thumb.Source!=null,
                    !String.IsNullOrWhiteSpace(record.Context) || !String.IsNullOrWhiteSpace(record.RecognizedText));
                string time=SourceCard.LocalTime(record.CapturedUtc);
                time=time.Length==0?SourceCard.LocalTime(record.UpdatedUtc):time;
                source.Text=String.Join("  ·  ",new[]{scope,time}.Where(v=>v.Length>0));
                excerpt.Text=Preview(record,title.Text);excerpt.Visibility=excerpt.Text.Length==0?W.Visibility.Collapsed:W.Visibility.Visible;
            }
            static M.ImageSource Thumbnail(byte[] bytes)
            {
                if(bytes==null || bytes.Length==0) return null;
                try {
                    using(var stream=new MemoryStream(bytes,false)) {
                        var header=B.BitmapFrame.Create(stream,B.BitmapCreateOptions.DelayCreation,B.BitmapCacheOption.None);
                        bool tall=header.PixelHeight>header.PixelWidth;stream.Position=0;
                        var thumb=new B.BitmapImage();thumb.BeginInit();thumb.CacheOption=B.BitmapCacheOption.OnLoad;
                        if(tall) thumb.DecodePixelHeight=96;else thumb.DecodePixelWidth=128;
                        thumb.StreamSource=stream;thumb.EndInit();thumb.Freeze();return thumb;
                    }
                } catch(FormatException) {return null;} catch(NotSupportedException) {return null;}
                catch(ArgumentException) {return null;} catch(IOException) {return null;} catch(COMException) {return null;}
            }
        }
    }
}
