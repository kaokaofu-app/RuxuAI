using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Jiyue
{
    internal sealed class ReplyView : RichTextBox
    {
        const int WmSetRedraw = 0x000B, WmVScroll = 0x0115, ScrollBottom = 7;
        const int EmGetScrollPos = 0x0400 + 221, EmSetScrollPos = 0x0400 + 222;
        const int WmMouseWheel = 0x020A, EmLineScroll = 0x00B6, WheelDelta = 120;
        int wheelRemainder, zoomWheelRemainder;
        sealed class Bubble
        {
            public int Start, End, Width, PaddingX, PaddingY;
            public string Literal;
        }
        readonly List<Bubble> bubbles = new List<Bubble>();
        sealed class CodeSnippet
        {
            public int Heading;
            public string Source;
            public IconButton Copy;
        }
        readonly List<CodeSnippet> snippets=new List<CodeSnippet>();
        bool dark, highContrast, formatting, layoutQueued;
        static readonly Color[] Light=Theme.ReplyPalette(false), Dark=Theme.ReplyPalette(true);
        // Persisted RTF used these RGB values as role identifiers before the unified theme.
        static readonly int[] legacyLight={0x233735,0x255C4D,0xE6EFEB,0x55635F,0xDFECFD,0x19314F,0xFFFFFF,0xB22222};
        static readonly int[] legacyDark={0xE7E7E7,0xA2DCC7,0x2F3937,0xBAC1BE,0x193E73,0xF3F7FF,0x191919,0xFF9E94};
        static readonly Regex ColourTable = new Regex(@"\{\\colortbl[^{}]*\}");
        static readonly Regex ColourEntry = new Regex(@"\\red(\d+)\\green(\d+)\\blue(\d+);");
        Color[] Palette=Light;
        static readonly Regex DefaultFormat=new Regex(@"\\\\|\\(?:cf0(?![0-9])|plain(?![A-Za-z]))");
        static string ColourCode(Color c) {return @"\red"+c.R+@"\green"+c.G+@"\blue"+c.B+";";}

        string ThemeRtf(string source,bool restoreDefaults=false)
        {
            string themed=ColourTable.Replace(source, delegate(Match table) {
                return ColourEntry.Replace(table.Value,delegate(Match entry) {
                    int rgb=Color.FromArgb(Int32.Parse(entry.Groups[1].Value),Int32.Parse(entry.Groups[2].Value),
                        Int32.Parse(entry.Groups[3].Value)).ToArgb() & 0xFFFFFF;
                    int role=rgb==0x235EAF ? 4 : rgb==0 ? 0 : -1;
                    for(int i=0;role<0 && i<Light.Length;i++)
                        if(rgb==(Light[i].ToArgb() & 0xFFFFFF) || rgb==(Dark[i].ToArgb() & 0xFFFFFF) ||
                            rgb==legacyLight[i] || rgb==legacyDark[i]) role=i;
                    if(role<0) return entry.Value;
                    return ColourCode(Palette[role]);
                });
            },1);
            if(!restoreDefaults || !themed.StartsWith(@"{\rtf1",StringComparison.Ordinal)) return themed;
            // RichEdit interprets an implicit (or cf0) foreground as black, including on dark paper.
            // Give old transcripts an explicit default without touching escaped user text.
            string colour=ColourCode(Palette[0]);
            Match tableMatch=ColourTable.Match(themed);
            int index=1;
            if(tableMatch.Success) {
                string table=tableMatch.Value;
                int offset=table.IndexOf(colour,StringComparison.Ordinal);
                if(offset<0) offset=table.Length-1;
                index=0;
                for(int i=0;i<offset;i++) if(table[i]==';') index++;
                if(offset==table.Length-1) table=table.Insert(offset,colour);
                themed=themed.Remove(tableMatch.Index,tableMatch.Length).Insert(tableMatch.Index,table+@"\cf"+index+" ");
            } else themed=themed.Insert(6,@"{\colortbl;"+colour+@"}\cf1 ");
            return DefaultFormat.Replace(themed,delegate(Match m) {
                if(m.Value==@"\\") return m.Value;
                return (m.Value==@"\plain" ? m.Value : "")+@"\cf"+index;
            });
        }

        public void ApplyTheme(bool useDark)
        {
            bool contrast=SystemInformation.HighContrast;
            if(dark==useDark && highContrast==contrast && BackColor==Palette[6]) return;
            bool hasText=TextLength>0;
            string old=hasText ? Rtf : null;
            dark=useDark; highContrast=contrast;
            Palette=contrast ? new[]{SystemColors.WindowText,SystemColors.WindowText,SystemColors.Window,
                SystemColors.WindowText,SystemColors.Highlight,SystemColors.HighlightText,SystemColors.Window,SystemColors.WindowText}
                : dark ? Dark : Light;
            BackColor=Palette[6]; ForeColor=Palette[0];
            if(hasText) PreserveView(delegate {
                Rtf=ThemeRtf(old,true);
                MeasureBubbles();
            });
            Invalidate();
        }

        public void RestoreTranscript(string rtf)
        {
            bubbles.Clear();
            ClearCodeButtons(0);
            Rtf=ThemeRtf(rtf,true);
            // Old transcripts used a unique highlight for right-aligned user paragraphs.
            // Discover those runs without changing any character offsets used for retries.
            string text=Text;
            PreserveView(delegate {
                Bubble open=null;
                for(int start=0;start<text.Length;) {
                    int end=text.IndexOf('\n',start);
                    if(end<0) end=text.Length;
                    Select(start,1);
                    if(SelectionBackColor.ToArgb()==Palette[4].ToArgb()) {
                        if(open==null) {open=new Bubble {Start=start}; bubbles.Add(open);}
                        open.End=end;
                    } else open=null;
                    start=end+1;
                }
                foreach(var bubble in bubbles) {
                    bubble.Literal=text.Substring(bubble.Start,bubble.End-bubble.Start);
                    // Older records include paragraph spacing in addition to the painted bubble padding.
                    Select(bubble.Start,bubble.End-bubble.Start);
                    var spacing=new ParagraphSpacing { Size=Marshal.SizeOf(typeof(ParagraphSpacing)), Mask=0x40|0x80 };
                    SendMessage(Handle,0x0447,IntPtr.Zero,ref spacing); // EM_SETPARAFORMAT, before/after in twips
                }
                MeasureBubbles();
            });
        }

        public new void Clear() {bubbles.Clear(); ClearCodeButtons(0); base.Clear();}

        internal void SetReadingSize(int points)
        {
            if(!IsHandleCreated || IsDisposed) return;
            // RichEdit zoom preserves each heading, code span and saved transcript's original type scale.
            float zoom=Math.Max(10,Math.Min(18,points))/12f;
            if(Math.Abs(ZoomFactor-zoom)<.005f) return;
            PreserveView(delegate { ZoomFactor=zoom; MeasureBubbles(); });
            Invalidate(); CheckViewportSoon();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ScrollInfo
        {
            public int Size, Mask, Min, Max, Page, Position, TrackPosition;
        }

        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr window, int bar, ref ScrollInfo info);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr value, IntPtr data);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr value, ref Point position);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window, int message, ref Point position, IntPtr index);

        internal int ReplyStartY(int index)
        {
            // EM_POSFROMCHAR also locates the insertion point just beyond the last rendered character.
            var position=new Point();
            SendMessage(Handle,0x0426,ref position,new IntPtr(index));
            return position.Y;
        }

        public event EventHandler ViewportChanged;
        bool viewportQueued, wasAboveLatest;
        public bool CurrentlyAboveLatest
        {
            get {
                if (!IsHandleCreated || IsDisposed) return false;
                var info=new ScrollInfo { Size=Marshal.SizeOf(typeof(ScrollInfo)), Mask=7 };
                return GetScrollInfo(Handle,1,ref info) && info.Page>0 &&
                    (long)info.Position+info.Page < (long)info.Max-2;
            }
        }
        public void JumpToLatest()
        {
            if (!IsHandleCreated) return;
            Select(TextLength,0);
            SendMessage(Handle,WmVScroll,new IntPtr(ScrollBottom),IntPtr.Zero);
            CheckViewportSoon();
        }
        void CheckViewportSoon()
        {
            if (viewportQueued || formatting || !IsHandleCreated || IsDisposed || Disposing || ViewportChanged==null) return;
            viewportQueued=true;
            BeginInvoke((MethodInvoker)delegate {
                viewportQueued=false;
                if (IsDisposed || !IsHandleCreated) return;
                PlaceCodeButtons();
                bool above=CurrentlyAboveLatest;
                if (above==wasAboveLatest) return;
                wasAboveLatest=above;
                var changed=ViewportChanged;
                if (changed!=null) changed(this,EventArgs.Empty);
            });
        }
        protected override void OnVScroll(EventArgs e) { base.OnVScroll(e); CheckViewportSoon(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); CheckViewportSoon(); }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmMouseWheel)
            {
                long data = message.WParam.ToInt64();
                if((data & 8)!=0) {
                    wheelRemainder=0;
                    int delta=unchecked((short)(data>>16));
                    int total=zoomWheelRemainder+delta;
                    zoomWheelRemainder=total%WheelDelta;
                    int steps=total/WheelDelta;
                    if(steps!=0) ReaderPreferences.FontSize+=steps;
                    message.Result=IntPtr.Zero;
                    return;
                }
                zoomWheelRemainder=0;
                // Shift-wheel retains native horizontal scrolling.
                if ((data & 0x000C) == 0)
                {
                    if (SystemInformation.MouseWheelScrollLines != 0)
                    {
                        int delta = unchecked((short)(data >> 16));
                        int total = wheelRemainder + delta;
                        wheelRemainder = total % WheelDelta;
                        int lines = -(total / WheelDelta);
                        // One native display line per notch, including wrapped paragraphs.
                        if (lines != 0) SendMessage(Handle, EmLineScroll, IntPtr.Zero, new IntPtr(lines));
                    }
                    else wheelRemainder = 0;
                    message.Result = IntPtr.Zero;
                    return;
                }
                wheelRemainder = 0;
            }
            base.WndProc(ref message);
            if ((message.Msg==0x000F || message.Msg==0x0318 || message.Msg==0x0317) &&
                bubbles!=null && bubbles.Count!=0 && !formatting && IsHandleCreated) {
                using(var graphics=message.WParam==IntPtr.Zero ? CreateGraphics() : Graphics.FromHdc(message.WParam))
                    PaintBubbles(graphics);
            }
            if(message.Msg==WmVScroll || message.Msg==EmLineScroll || message.Msg==EmSetScrollPos || message.Msg==0x0101)
                CheckViewportSoon();
        }

        public void RenderAnswer(int start, string markdown)
        {
            var blocks=new List<string>();
            string rtf = ThemeRtf(MarkdownReply.ToRtf(markdown,blocks));
            EditTail(start, delegate { SelectedRtf = rtf; });
            if(blocks.Count!=0) IndexCodeBlocks(start,blocks);
            else ClearCodeButtons(start);
        }

        public void RestoreCodeButtons(List<KeyValuePair<string,string>> turns)
        {
            var blocks=new List<string>();
            foreach(var turn in turns)
                if(turn.Key=="assistant" && (turn.Value.Contains("```") || turn.Value.Contains("~~~")))
                    MarkdownReply.ToRtf(turn.Value,blocks);
            if(blocks.Count>0) IndexCodeBlocks(0,blocks);
        }

        void IndexCodeBlocks(int start,List<string> blocks)
        {
            // RichEdit maps soft line breaks to a single character. Keep indices in native text units.
            string text=Text.Replace('\v','\n').Replace('\r','\n');
            int cursor=start;
            var retained=new HashSet<CodeSnippet>();
            foreach(string code in blocks) {
                if(String.IsNullOrWhiteSpace(code)) continue;
                string target=MarkdownReply.CodeHeading+"\n"+code;
                int pos=text.IndexOf(target,cursor,StringComparison.Ordinal);
                if(pos<0) continue; // Older transcripts have code without the reserved title row.
                cursor=pos+target.Length;
                var snippet=snippets.Find(item=>item.Heading==pos);
                if(snippet==null) {
                    snippet=new CodeSnippet {Heading=pos};snippets.Add(snippet);
                }
                snippet.Source=code;retained.Add(snippet);
            }
            ClearCodeButtons(start,retained);PlaceCodeButtons();
        }
        void ClearCodeButtons(int from,HashSet<CodeSnippet> keep=null)
        {
            for(int i=snippets.Count-1;i>=0;i--) {
                var item=snippets[i];
                if(item.Heading<from || (keep!=null && keep.Contains(item))) continue;
                if(item.Copy!=null) item.Copy.Dispose();
                snippets.RemoveAt(i);
            }
        }
        void PlaceCodeButtons()
        {
            if(snippets.Count==0 || !IsHandleCreated || IsDisposed) return;
            int first=GetCharIndexFromPosition(Point.Empty);
            int last=GetCharIndexFromPosition(new Point(Math.Max(0,ClientSize.Width-1),Math.Max(0,ClientSize.Height-1)));
            float scale;using(var g=CreateGraphics()) scale=g.DpiX/96f;
            int size=(int)Math.Round(32*scale);
            foreach(var item in snippets) {
                bool visible=item.Heading>=first && item.Heading<=last;
                if(!visible) { if(item.Copy!=null) item.Copy.Visible=false; continue; }
                Point point=GetPositionFromCharIndex(item.Heading);
                if(item.Copy==null) {
                    var button=new IconButton("copy","复制代码块");item.Copy=button;
                    button.Click+=delegate {
                        try {Clipboard.SetText(item.Source);button.ShowCopied(); Toast.Show("已复制代码",ToastKind.Success,FindForm());}
                        catch(ExternalException) {Toast.Show("剪贴板正被占用，请重试。",ToastKind.Error,FindForm());}
                    };
                    Controls.Add(button);
                }
                item.Copy.SetBounds(Math.Max(0,ClientSize.Width-size-(int)(3*scale)),Math.Max(0,point.Y),size,size);
                item.Copy.Visible=true;
            }
        }

        public void RemoveTail(int start)
        {
            bubbles.RemoveAll(item=>item.End>start);
            ClearCodeButtons(start);
            EditTail(start, delegate { SelectedText = ""; });
        }

        public void AppendQuestion(string question)
        {
            EditTail(TextLength, delegate {
                if (TextLength > 0) SelectedText = "\n\n";
                int start=TextLength;
                SelectedRtf = ThemeRtf(MarkdownReply.QuestionRtf(question));
                var item=new Bubble {Start=start,End=TextLength-2,Literal=" "+question.Replace("\r\n","\n").Replace('\r','\n')+" "};
                bubbles.Add(item);
                using(var g=CreateGraphics()) MeasureBubble(item,g);
            });
        }

        public void AppendNote(string message, Color color, bool followedByAnswer)
        {
            EditTail(TextLength, delegate {
                if (TextLength > 0) SelectedText = "\n\n";
                SelectionFont = Font;
                SelectionColor = color.ToArgb()==Color.Firebrick.ToArgb() ? Palette[7] : color;
                SelectionBackColor = BackColor;
                SelectionAlignment = HorizontalAlignment.Left;
                SelectionBullet = false;
                SelectionIndent = 0;
                SelectionRightIndent = 0;
                SelectionHangingIndent = 0;
                SelectedText = message + (followedByAnswer ? "\n\n" : "");
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FormatRect { public int Left,Top,Right,Bottom; }
        // PARAFORMAT2 fields used by PFM_SPACEBEFORE and PFM_SPACEAFTER.
        [StructLayout(LayoutKind.Explicit,Size=188)]
        struct ParagraphSpacing {
            [FieldOffset(0)] public int Size;
            [FieldOffset(4)] public int Mask;
            [FieldOffset(156)] public int Before;
            [FieldOffset(160)] public int After;
        }
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window,int message,IntPtr value,ref ParagraphSpacing spacing);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr window,int message,IntPtr value,ref FormatRect rect);
        static readonly TextFormatFlags MeasureFlags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|
            TextFormatFlags.SingleLine|TextFormatFlags.ExpandTabs;

        void MeasureBubble(Bubble item,Graphics graphics)
        {
            var area=new FormatRect();
            SendMessage(Handle,0x00B2,IntPtr.Zero,ref area); // EM_GETRECT: excludes the scrollbar.
            int viewport=area.Right-area.Left;
            if(viewport<=0 || item.End<=item.Start) return;
            float zoom=ZoomFactor;
            float unit=graphics.DpiX/96f*zoom;
            item.PaddingX=Math.Max(2,(int)Math.Round(6*unit));
            item.PaddingY=Math.Max(2,(int)Math.Round(4*unit));
            int maxText=Math.Max(1,(int)(viewport*0.76)-item.PaddingX*2);
            int width=0;
            foreach(string line in item.Literal.Split('\n','\v')) {
                width=Math.Max(width,(int)Math.Ceiling(zoom*TextRenderer.MeasureText(graphics,line,Font,Size.Empty,MeasureFlags).Width));
                if(width>=maxText) break;
            }
            int rightSpace=item.PaddingX+(int)Math.Ceiling(3*unit);
            int textWidth=Math.Min(maxText,width+(int)Math.Ceiling(2*unit));
            int leftSpace=Math.Max(0,viewport-rightSpace-textWidth);
            Select(item.Start,item.End-item.Start);
            SelectionAlignment=HorizontalAlignment.Left;
            SelectionIndent=(int)Math.Round(leftSpace/zoom);
            SelectionRightIndent=(int)Math.Round(rightSpace/zoom);
            SelectionColor=Palette[5]; SelectionBackColor=Palette[4];
            item.Width=textWidth+2*item.PaddingX;
        }

        void MeasureBubbles()
        {
            using(var graphics=CreateGraphics())
                foreach(var bubble in bubbles) MeasureBubble(bubble,graphics);
        }
        void QueueBubbleLayout()
        {
            if(layoutQueued || bubbles==null || bubbles.Count==0 || !IsHandleCreated || IsDisposed) return;
            layoutQueued=true;
            BeginInvoke((MethodInvoker)delegate {
                layoutQueued=false;
                if(!IsDisposed && IsHandleCreated) PreserveView(MeasureBubbles);
            });
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); QueueBubbleLayout(); CheckViewportSoon(); }

        void PaintBubbles(Graphics canvas)
        {
            int lineHeight=Math.Max(1,(int)Math.Ceiling(Font.GetHeight(canvas)*ZoomFactor));
            int visibleFirst=GetLineFromCharIndex(GetCharIndexFromPosition(new Point(0,0)));
            int visibleLast=GetLineFromCharIndex(GetCharIndexFromPosition(new Point(0,Math.Max(0,ClientSize.Height-1))));
            using(var fill=new SolidBrush(Palette[4]))
            foreach(var item in bubbles) {
                if(item.End<=item.Start || item.End>TextLength || item.Width==0) continue;
                Point first=GetPositionFromCharIndex(item.Start), last=GetPositionFromCharIndex(item.End);
                // RichEdit includes paragraph spacing and font fallback in its line boxes.
                // Only its next display-line position describes the actual painted highlight.
                int afterY=item.End<TextLength ? GetPositionFromCharIndex(item.End+1).Y : last.Y;
                if(afterY<=last.Y) afterY=last.Y+lineHeight;
                var box=new Rectangle(first.X-item.PaddingX,first.Y-item.PaddingY,item.Width,
                    afterY-first.Y+2*item.PaddingY);
                if(box.Height<=0 || !box.IntersectsWith(ClientRectangle)) continue;
                var saved=canvas.Save();
                try {
                    // RichEdit still draws and selects every glyph. Fill the rounded padding
                    // around those native highlight runs without painting over the selection.
                    int firstLine=GetLineFromCharIndex(item.Start),lastLine=GetLineFromCharIndex(item.End);
                    for(int line=Math.Max(firstLine,visibleFirst-1);line<=Math.Min(lastLine,visibleLast+1);line++) {
                        int start=Math.Max(item.Start,GetFirstCharIndexFromLine(line));
                        int next=line==lastLine ? item.End : GetFirstCharIndexFromLine(line+1);
                        if(next<=start) continue;
                        int end=next;
                        while(end>start && end-1-item.Start<item.Literal.Length &&
                            "\r\n\v".IndexOf(item.Literal[end-1-item.Start])>=0) end--;
                        if(end<=start) continue;
                        Point begin=GetPositionFromCharIndex(start), finish=GetPositionFromCharIndex(end);
                        if(finish.Y!=begin.Y) {
                            finish=GetPositionFromCharIndex(end-1);
                            int n=end-1-item.Start;
                            if(n>=0 && n<item.Literal.Length)
                                finish.X+=(int)Math.Ceiling(ZoomFactor*TextRenderer.MeasureText(canvas,
                                    item.Literal[n].ToString(),Font,Size.Empty,MeasureFlags).Width);
                        }
                        int left=Math.Min(begin.X,finish.X),right=Math.Max(begin.X,finish.X);
                        int nextY=line==lastLine ? afterY : GetPositionFromCharIndex(next).Y;
                        canvas.ExcludeClip(new Rectangle(left,begin.Y,Math.Max(1,right-left),
                            nextY>begin.Y ? nextY-begin.Y : lineHeight));
                    }
                    float radius=Math.Min(Math.Min(box.Width,box.Height)/2f,Theme.RadiusMedium*canvas.DpiX/96f*ZoomFactor);
                    float diameter=2*radius;
                    using(var path=new GraphicsPath()) {
                        path.AddArc(box.Left,box.Top,diameter,diameter,180,90);
                        path.AddArc(box.Right-diameter,box.Top,diameter,diameter,270,90);
                        path.AddArc(box.Right-diameter,box.Bottom-diameter,diameter,diameter,0,90);
                        path.AddArc(box.Left,box.Bottom-diameter,diameter,diameter,90,90);
                        path.CloseFigure();
                        canvas.SmoothingMode=SmoothingMode.AntiAlias;
                        canvas.FillPath(fill,path);
                    }
                } finally {canvas.Restore(saved);}
            }
        }

        void EditTail(int start, Action write)
        {
            PreserveView(delegate { Select(start,TextLength-start); write(); });
        }
        // Preserve the reader's selected text and scroll position during formatting.
        void PreserveView(Action write)
        {
            int selection = SelectionStart, length = SelectionLength;
            bool follow = length == 0 && !CurrentlyAboveLatest;
            var scroll = new Point();
            SendMessage(Handle, EmGetScrollPos, IntPtr.Zero, ref scroll);
            SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            formatting=true;
            try
            {
                ReadOnly = false;
                write();
                int end = TextLength;
                selection = Math.Min(selection, end);
                Select(selection, Math.Min(length, end - selection));
                if (follow) SendMessage(Handle, WmVScroll, new IntPtr(ScrollBottom), IntPtr.Zero);
                else SendMessage(Handle, EmSetScrollPos, IntPtr.Zero, ref scroll);
            }
            finally
            {
                formatting=false;
                ClearUndo();
                ReadOnly = true;
                SendMessage(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
                Invalidate();
                CheckViewportSoon();
            }
        }
    }
}
