using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Jiyue
{
    internal sealed partial class Dialog
    {
        public event Action ReadingOptionsRequested;
        IconButton pin, copy, regenerate, more;
        Panel pulse, cardLine;
        FlowLayoutPanel chips;
        TableLayoutPanel card;
        Label cardText, headerTitle;
        Button primaryAction, secondaryAction, translate;
        Action firstAction, secondAction;
        bool cardError;
        ContextMenuStrip moreMenu, replyMenu;

        static Size ReadWindowSize()
        {
            int w=ReaderPreferences.ReadInt("DialogWidth",0), h=ReaderPreferences.ReadInt("DialogHeight",0);
            return w>=370 && h>=290 ? new Size(w,h) : Size.Empty;
        }
        static void SavePreference(string key,int value)
        {
            try {ReaderPreferences.WriteInt(key,value);}
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        static void SaveWindowSize(Size value)
        {
            SavePreference("DialogWidth",value.Width);
            SavePreference("DialogHeight",value.Height);
        }

        Control MakeHeader(string title)
        {
            var row=new TableLayoutPanel {ColumnCount=4,RowCount=1,Dock=DockStyle.Fill,
                Height=44,Margin=Padding.Empty,Padding=Padding.Empty};
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerTitle=new Label {Text=title,AutoSize=true,Anchor=AnchorStyles.Left,Font=Style.Font(11,true),
                Margin=new Padding(0,0,6,0)};
            pulse=new Panel {Size=new Size(12,10),Margin=Padding.Empty,Anchor=AnchorStyles.Left};
            pulse.Paint+=delegate(object sender,PaintEventArgs e) {
                if(active==null) return;
                int alpha=Motion.Enabled && !SystemInformation.HighContrast ? 178+(int)(76*Math.Sin(Environment.TickCount*Math.PI/600.0)) : 255;
                using(var brush=new SolidBrush(Color.FromArgb(alpha,Theme.Accent(darkReading)))) {
                    e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    float d=8*e.Graphics.DpiX/96f;
                    e.Graphics.FillEllipse(brush,0,(pulse.Height-d)/2,d,d);
                }
            };
            var tools=new FlowLayoutPanel {AutoSize=true,WrapContents=false,Anchor=AnchorStyles.Right,Margin=Padding.Empty};
            TopMost=ReaderPreferences.ReadInt("DialogPinned",0)!=0;
            pin=new IconButton(TopMost?"pin_off":"pin",TopMost?"取消置顶":"置顶窗口");
            copy=new IconButton("copy","复制最新回答","Ctrl+Shift+C");
            regenerate=new IconButton("arrow_clockwise","重新生成","Ctrl+R");
            more=new IconButton("more_horizontal","更多操作");
            pin.Click+=delegate {
                TopMost=!TopMost; SavePreference("DialogPinned",TopMost?1:0);
                pin.SetIcon(TopMost?"pin_off":"pin"); pin.SetLabel(TopMost?"取消置顶":"置顶窗口");
            };
            copy.Click+=delegate { CopyLatest(); };
            regenerate.Click+=delegate { Regenerate(); };
            more.Click+=delegate { moreMenu.Show(more,new Point(0,more.Height)); };
            foreach(var b in new[]{pin,copy,regenerate,more}) { b.Margin=new Padding(2,0,0,0); tools.Controls.Add(b); }
            row.Controls.Add(headerTitle,0,0); row.Controls.Add(pulse,1,0); row.Controls.Add(state,2,0); row.Controls.Add(tools,3,0);
            return row;
        }
        static Button SmallButton(string text)
        {
            var b=Style.Button(text,0,0,80,32);
            b.Font=Style.Font(9.5f);
            b.AutoSize=true; b.AutoSizeMode=AutoSizeMode.GrowAndShrink;
            b.MinimumSize=new Size(48,32); b.Padding=new Padding(7,0,7,0); b.Margin=new Padding(0,0,4,0);
            return b;
        }
        Control MakeChips()
        {
            var host=new Panel {Dock=DockStyle.Fill,Height=52,Margin=new Padding(0,4,0,0),Visible=false};
            chips=new FlowLayoutPanel {Dock=DockStyle.Fill,Margin=Padding.Empty,AutoScroll=true,WrapContents=false};
            host.Controls.Add(chips);
            string[] texts={"说得更简单","举个例子","翻译成英文","展开讲讲","列成要点"};
            string[] prompts={"请用更简单、容易理解的语言重述上面的内容。","请用一个具体、贴近生活的例子说明上面的内容。",
                null,"请展开讲讲上面的内容，说明重要细节和原因。","请把上面的内容整理成简明的要点列表。"};
            for(int i=0;i<texts.Length;i++) {
                var b=SmallButton(texts[i]); string prompt=prompts[i];
                if(i==2) translate=b;
                b.Click+=delegate { if(active==null && attached) StartAnswer(prompt ?? ("请将上面的回答"+translate.Text+"。")); };
                chips.Controls.Add(b);
            }
            return host;
        }
        void InstallStateCard()
        {
            card=new TableLayoutPanel {Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
                ColumnCount=2,RowCount=2,Visible=false,Padding=new Padding(0,6,8,6),Margin=Padding.Empty};
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,3)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            cardLine=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty};
            card.Controls.Add(cardLine,0,0); card.SetRowSpan(cardLine,2);
            cardText=new Label {Dock=DockStyle.Fill,AutoSize=true,Margin=new Padding(10,1,4,4),AccessibleRole=AccessibleRole.StatusBar};
            card.Controls.Add(cardText,1,0);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true,AutoSize=true,Margin=new Padding(10,0,0,0)};
            primaryAction=SmallButton(""); secondaryAction=SmallButton("");
            primaryAction.Click+=delegate {if(firstAction!=null) firstAction();};
            secondaryAction.Click+=delegate {if(secondAction!=null) secondAction();};
            actions.Controls.Add(primaryAction); actions.Controls.Add(secondaryAction); card.Controls.Add(actions,1,1);
            paper.Controls.Add(card); card.SendToBack();
        }
        void ShowCard(string message,string first=null,Action action=null,string second=null,Action alternate=null,bool error=false)
        {
            cardError=error;
            cardText.Text=message;
            firstAction=action; secondAction=alternate;
            primaryAction.Text=first; primaryAction.Visible=action!=null;
            secondaryAction.Text=second; secondaryAction.Visible=alternate!=null;
            card.Visible=true; ColorCard(darkReading);
        }
        void ColorCard(bool dark)
        {
            card.BackColor=Theme.Surface(dark); cardLine.BackColor=cardError?Theme.Danger(dark):Theme.Line(dark);
            cardText.ForeColor=cardError?Theme.Ink(dark):Theme.Muted(dark);
            foreach(var button in new[]{primaryAction,secondaryAction}) {button.ForeColor=Theme.Ink(dark);button.BackColor=Theme.Surface(dark);}
        }
        void ApplyToolsTheme(bool dark)
        {
            ColorCard(dark); headerTitle.ForeColor=Theme.Ink(dark);
            foreach(Control b in chips.Controls) {b.ForeColor=Theme.Ink(dark); b.BackColor=Theme.Surface(dark);}
            foreach(var menu in new[]{moreMenu,replyMenu}) {
                menu.BackColor=Theme.Surface(dark); menu.ForeColor=Theme.Ink(dark);
                menu.Renderer=new ToolStripProfessionalRenderer(new DialogMenuColors(dark));
            }
            pin.Parent.Invalidate(true); pulse.Invalidate();
        }
        sealed class DialogMenuColors : ProfessionalColorTable
        {
            readonly bool dark;
            internal DialogMenuColors(bool dark) {this.dark=dark;}
            public override Color ToolStripDropDownBackground {get{return Theme.Surface(dark);}}
            public override Color MenuItemSelected {get{return Theme.SurfaceHover(dark);}}
            public override Color MenuItemBorder {get{return Theme.Line(dark);}}
            public override Color MenuBorder {get{return Theme.Line(dark);}}
        }
        string LatestAnswer()
        {
            for(int i=history.Count-1;i>=0;i--) if(history[i].Key=="assistant") return history[i].Value;
            return null;
        }
        void RefreshTools()
        {
            string latestAnswer=LatestAnswer();
            bool ready=attached && settings().Ready && !awaitingPartialApproval;
            copy.Enabled=latestAnswer!=null;
            regenerate.Enabled=ready && active==null && (pendingStart>=0 || latestAnswer!=null);
            chips.Parent.Visible=ready && active==null && latestAnswer!=null;
            if(latestAnswer!=null) {
                int han=0,latin=0;
                foreach(char c in latestAnswer) { if(c>=0x3400 && c<=0x9fff) han++; else if(c<128 && Char.IsLetter(c)) latin++; }
                translate.Text=han*3>=latin?"翻译成英文":"翻译成中文";
            }
            pulse.Visible=active!=null; pulse.Invalidate();
            RefreshSendButton();
        }
        void ApplyFontSize() { if(!output.IsDisposed) output.SetReadingSize(ReaderPreferences.FontSize); }
        void StopAnswer()
        {
            if(active==null || stopRequested) return;
            stopRequested=true; waitingDots.Stop(); state.Text="正在停止…"; active.Cancel();
        }
        string DefaultQuestion()
        {
            return articleCapture?"请简要解读这篇材料。":image!=null?"请直接解释框选内容。":"请概括这页的主题和最值得理解的要点。";
        }
        void Regenerate()
        {
            if(active!=null || !regenerate.Enabled) return;
            if(pendingStart>=0) {StartAnswer(retry);return;}
            int last=history.Count-2;
            if(last<0 || history[last].Key!="user" || history[last+1].Key!="assistant") return;
            string prompt=history[last].Value;
            history.RemoveRange(last,2);
            sectionHistoryStart=Math.Min(sectionHistoryStart,history.Count);
            output.Clear();
            for(int i=0;i<history.Count;i++) {
                if(history[i].Key=="assistant") output.RenderAnswer(output.TextLength,history[i].Value);
                else if(i!=0 || history[i].Value!=DefaultQuestion()) output.AppendQuestion(history[i].Value);
            }
            StartAnswer(last==0 && prompt==DefaultQuestion()?null:prompt);
        }
        void CopyText(string value,bool latestAnswer=false)
        {
            if(String.IsNullOrWhiteSpace(value)) return;
            try {
                Clipboard.SetText(value);
                if(latestAnswer) copy.ShowCopied();
                Toast.Show("已复制",ToastKind.Success,this);
            } catch (System.Runtime.InteropServices.ExternalException) {Toast.Show("剪贴板正被占用，请重试。",ToastKind.Error,this);}
        }
        void CopyLatest() {CopyText(LatestAnswer(),true);}
        string Markdown(bool answersOnly)
        {
            var result=new StringBuilder();
            if(!answersOnly) result.Append("# ").AppendLine(ConversationTitle()).AppendLine();
            foreach(var pair in history) {
                if(answersOnly && pair.Key!="assistant") continue;
                if(!answersOnly) result.AppendLine(pair.Key=="user"?"## 你":"## 如许AI").AppendLine();
                result.AppendLine(pair.Value).AppendLine();
            }
            return result.ToString().TrimEnd();
        }
        void ExportConversation()
        {
            using(var dialog=new SaveFileDialog {Filter="Markdown 文件 (*.md)|*.md",DefaultExt="md",AddExtension=true,
                FileName="如许AI对话-"+DateTime.Now.ToString("yyyyMMdd-HHmm"),RestoreDirectory=true})
                if(dialog.ShowDialog(this)==DialogResult.OK) {
                    try {File.WriteAllText(dialog.FileName,Markdown(false),new UTF8Encoding(false));Toast.Show("已导出 Markdown",ToastKind.Success,this);}
                    catch(IOException ex) {Toast.Show(ex.Message,ToastKind.Error,this);}
                    catch(UnauthorizedAccessException ex) {Toast.Show(ex.Message,ToastKind.Error,this);}
                }
        }
        static ToolStripMenuItem Item(ContextMenuStrip menu,string label,Action callback)
        {
            var item=new ToolStripMenuItem(label);
            item.Click+=delegate {callback();}; menu.Items.Add(item);return item;
        }
        void InstallMenus()
        {
            moreMenu=new ContextMenuStrip {ShowImageMargin=false};
            var copyAll=Item(moreMenu,"复制整段对话（Markdown）",delegate {CopyText(Markdown(false));});
            var export=Item(moreMenu,"导出为 Markdown…",ExportConversation);
            moreMenu.Items.Add(new ToolStripSeparator());
            Item(moreMenu,"回答偏好",delegate {if(AnswerPreferenceRequested!=null) AnswerPreferenceRequested();});
            Item(moreMenu,"模型设置",delegate {configure(null);});
            moreMenu.Items.Add(new ToolStripSeparator());
            var bigger=Item(moreMenu,"放大字号    Ctrl +",delegate {ReaderPreferences.FontSize++;});
            var smaller=Item(moreMenu,"缩小字号    Ctrl −",delegate {ReaderPreferences.FontSize--;});
            moreMenu.Opening+=delegate {copyAll.Enabled=export.Enabled=history.Count>0; bigger.Enabled=ReaderPreferences.FontSize<18; smaller.Enabled=ReaderPreferences.FontSize>10;};
            replyMenu=new ContextMenuStrip {ShowImageMargin=false};
            var selected=Item(replyMenu,"复制",delegate {CopyText(output.SelectedText);});
            var answers=Item(replyMenu,"复制全部回答",delegate {CopyText(Markdown(true));});
            var quote=Item(replyMenu,"追问这句",delegate {
                input.Text="> "+output.SelectedText.Replace("\r\n","\n").Replace("\n","\r\n> ")+"\r\n\r\n"+input.Text;
                input.SelectionStart=input.TextLength;input.Focus();
            });
            var explain=Item(replyMenu,"解释这个词",delegate {StartAnswer("请解释『"+output.SelectedText.Trim()+"』在这里的意思。");});
            replyMenu.Opening+=delegate {
                string s=output.SelectedText.Trim();
                selected.Visible=s.Length>0; quote.Visible=s.Length>0 && input.Enabled;
                explain.Visible=s.Length>0 && s.Length<30 && active==null && input.Enabled;
                answers.Enabled=LatestAnswer()!=null;
            };
            output.ContextMenuStrip=replyMenu;
            Disposed+=delegate { moreMenu.Dispose(); replyMenu.Dispose(); };
        }
    }
}
