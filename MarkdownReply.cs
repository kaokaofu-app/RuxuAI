using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Jiyue
{
    internal static class MarkdownReply
    {
        static readonly string Header=BuildHeader();
        static string BuildHeader()
        {
            var header=new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fnil\fcharset134 Source Han Sans CN;}{\f1\fmodern Consolas;}{\f2\fswiss\fcharset0 Source Sans 3;}}{\colortbl;");
            foreach(var color in Theme.ReplyPalette(false))
                header.Append(@"\red").Append(color.R).Append(@"\green").Append(color.G).Append(@"\blue").Append(color.B).Append(';');
            return header.Append('}').ToString();
        }
        const string Body = @"\pard\ql\plain\f0\fs24\cf1";
        static readonly Regex Heading = new Regex(@"^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$");
        static readonly Regex Item = new Regex(@"^([ \t]*)([-+*]|[0-9]{1,9}[.)])(?:[ \t]+|$)(.*)$");
        static readonly Regex Separator = new Regex(@"^:?-{1,}:?$");
        static readonly Regex SectionLabel = new Regex(@"^(?:\*\*)?(总结|解释)(?:\*\*)?(?:[：:](?:\*\*)?\s*(.*)|$)$");

        // Keep the text selectable. ReplyView adds the rounded padding around the native glyphs.
        public static string QuestionRtf(string question)
        {
            var rtf = new StringBuilder(Header);
            rtf.Append(Body).Append(@"\qr\sb0\sa0 {\highlight5\cf6\enspace ");
            string[] lines = (question ?? "").Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            for (int i=0; i<lines.Length; i++)
            {
                if (i!=0) rtf.Append(@"\line ");
                Literal(rtf,lines[i],0,lines[i].Length);
            }
            rtf.Append(@"\enspace }\par ").Append(Body).Append(@"\sa85 \par }");
            return rtf.ToString();
        }

        internal const string CodeHeading="代码";
        public static string ToRtf(string markdown,List<string> snippets=null)
        {
            var rtf = new StringBuilder(Header);
            var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool hasBlock = false, afterTable = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (hasBlock && !afterTable) rtf.Append(@"\par ");
                afterTable = false;
                hasBlock = true;

                if (line == "$$" || line == @"\[")
                {
                    rtf.Append(Body).Append(@"\sa85 ");
                    Literal(rtf,raw,0,raw.Length);
                    string end = line == "$$" ? "$$" : @"\]";
                    while (++i < lines.Length)
                    {
                        rtf.Append(@"\line ");
                        Literal(rtf,lines[i],0,lines[i].Length);
                        if (lines[i].Trim() == end) break;
                    }
                    continue;
                }
                int fence = Fence(line);
                if (fence != 0)
                {
                    rtf.Append(Body).Append(@"\li150\sl600\slmult0\cbpat3\cf4\fs18 ");
                    Literal(rtf,CodeHeading,0,CodeHeading.Length);
                    rtf.Append(@"\par ");
                    rtf.Append(Body).Append(@"\li150\sa90\cbpat3\f1\fs22 ");
                    int start = i + 1;
                    for (i = start; i < lines.Length; i++)
                    {
                        var candidate = lines[i].Trim();
                        if (candidate.Length >= fence && candidate.Trim(line[0]).Length == 0) break;
                        if (i > start) rtf.Append(@"\line ");
                        Literal(rtf, lines[i], 0, lines[i].Length, false);
                    }
                    if(snippets!=null && i>start) snippets.Add(String.Join("\n",lines,start,i-start));
                    continue;
                }
                // A short table fits the narrow reading window. More complex tables stay readable as source.
                List<string> titles, rules;
                if (line.IndexOf('|') >= 0 && i + 1 < lines.Length &&
                    Cells(line, out titles) && titles.Count <= 4 && Cells(lines[i+1], out rules) &&
                    titles.Count == rules.Count && rules.TrueForAll(c => Separator.IsMatch(c)))
                {
                    TableRow(rtf, titles, true);
                    i++;
                    List<string> data;
                    while (i + 1 < lines.Length && Cells(lines[i+1], out data) && data.Count == titles.Count)
                    {
                        TableRow(rtf, data, false);
                        i++;
                    }
                    rtf.Append(Body).Append(' ');
                    afterTable = true;
                    continue;
                }
                if (Rule(line))
                {
                    rtf.Append(Body).Append(@"\brdrb\brdrs\brdrw8\brdrcf3\fs4\sa65 ");
                    continue;
                }
                var heading = Heading.Match(raw);
                var section = SectionLabel.Match(heading.Success ? heading.Groups[2].Value.Trim() : line);
                if (section.Success)
                {
                    bool summary=section.Groups[1].Value=="总结";
                    rtf.Append(Body).Append(summary ? @"\sb100\sa80\cf2\b\fs30 " : @"\sb190\sa65\b\fs27 ");
                    Inline(rtf,section.Groups[1].Value,0);
                    if(section.Groups[2].Value.Length>0) {
                        rtf.Append(@"\par ").Append(Body).Append(@"\sa85 ");
                        Paragraph(rtf,section.Groups[2].Value,lines,ref i,false);
                    }
                    continue;
                }
                if (heading.Success)
                {
                    int level = heading.Groups[1].Length;
                    rtf.Append(Body).Append(@"\sb105\sa75\b\fs").Append(level == 1 ? 32 : level == 2 ? 29 : 26).Append(' ');
                    Inline(rtf, heading.Groups[2].Value, 0);
                    continue;
                }
                var item = Item.Match(raw);
                if (item.Success)
                {
                    int indent = 0;
                    foreach (char c in item.Groups[1].Value) indent += c == '\t' ? 4 - indent % 4 : 1;
                    int stop = 330 + 95 * Math.Min(indent, 16);
                    rtf.Append(Body).Append(@"\li").Append(stop).Append(@"\fi-300\tx").Append(stop).Append(@"\sa45 ");
                    var marker = item.Groups[2].Value;
                    var shown = Char.IsDigit(marker[0]) ? marker : indent == 0 ? "•" : "◦";
                    Literal(rtf, shown, 0, shown.Length);
                    rtf.Append(@"\tab ");
                    Paragraph(rtf, item.Groups[3].Value, lines, ref i, true);
                    continue;
                }
                bool quote = line.StartsWith(">", StringComparison.Ordinal);
                rtf.Append(Body).Append(quote ? @"\li200\sa65\cf4\brdrl\brdrs\brdrw14\brdrcf3 " : @"\sa85 ");
                if (quote) Inline(rtf,line.Substring(1).TrimStart(),0);
                else Paragraph(rtf, raw, lines, ref i, false);
            }
            return rtf.Append('}').ToString();
        }

        static bool Rule(string s)
        {
            return s.Length >= 3 && (s.Trim('-').Length == 0 || s.Trim('*').Length == 0 || s.Trim('_').Length == 0);
        }

        static void Paragraph(StringBuilder rtf, string first, string[] lines, ref int i, bool list)
        {
            string current = first;
            while (true)
            {
                Inline(rtf,current.TrimEnd(),0);
                if (i+1 >= lines.Length) return;
                string next = lines[i+1], s = next.Trim();
                if (s.Length == 0 || s[0] == '>' || s.IndexOf('|') >= 0 || s == "$$" || s == @"\[" ||
                    Fence(s) != 0 || Rule(s) || Heading.IsMatch(next) || SectionLabel.IsMatch(s) || Item.IsMatch(next) ||
                    (list && !Char.IsWhiteSpace(next[0]))) return;
                if (current.EndsWith("  ",StringComparison.Ordinal)) rtf.Append(@"\line ");
                else if (current.Length != 0 && (current[current.Length-1] < 128 || s[0] < 128)) rtf.Append(' ');
                current = next.TrimStart();
                i++;
            }
        }

        static int Fence(string line)
        {
            if (line.Length < 3 || (line[0] != '`' && line[0] != '~')) return 0;
            int n = Run(line, 0);
            return n >= 3 && (line[0] != '`' || line.IndexOf('`', n) < 0) ? n : 0;
        }

        static bool Cells(string line, out List<string> cells)
        {
            cells = new List<string>();
            string s = line.Trim();
            if (s.IndexOf('|') < 0) return false;
            int start = s.StartsWith("|", StringComparison.Ordinal) ? 1 : 0;
            int end = s.EndsWith("|", StringComparison.Ordinal) ? s.Length - 1 : s.Length;
            var value = new StringBuilder();
            int code = 0;
            for (int i = start; i < end; i++)
            {
                if (s[i] == '\\' && i + 1 < end && s[i+1] == '|') { value.Append('|'); i++; }
                else if (s[i] == '`')
                {
                    int n = Run(s, i);
                    if (code == 0) code = n; else if (code == n) code = 0;
                    value.Append('`', n);
                    i += n-1;
                }
                else if (s[i] == '|' && code == 0) { cells.Add(value.ToString().Trim()); value.Clear(); }
                else value.Append(s[i]);
            }
            cells.Add(value.ToString().Trim());
            return cells.Count >= 2;
        }

        static void TableRow(StringBuilder rtf, List<string> cells, bool header)
        {
            rtf.Append(@"\trowd\trgaph65\trleft0");
            for (int i = 1; i <= cells.Count; i++)
            {
                if (header) rtf.Append(@"\clcbpat3");
                rtf.Append(@"\clbrdrb\brdrs\brdrw8\brdrcf3\cellx").Append(4200 * i / cells.Count);
            }
            foreach (var cell in cells)
            {
                rtf.Append(Body).Append(header ? @"\intbl\b\fs22 " : @"\intbl\fs22 ");
                Inline(rtf, cell, 0);
                rtf.Append(@"\cell ");
            }
            rtf.Append(@"\row ");
        }

        static int Run(string s, int start)
        {
            int end = start+1;
            while (end < s.Length && s[end] == s[start]) end++;
            return end-start;
        }

        static void Inline(StringBuilder rtf, string s, int depth)
        {
            if (depth >= 5) { Literal(rtf,s,0,s.Length); return; }
            int plain = 0;
            var unmatched = new HashSet<string>();
            for (int i = 0; i < s.Length;)
            {
                char ch = s[i];
                if (ch == '\\' && i+1 < s.Length && (s[i+1] == '*' || s[i+1] == '_' || s[i+1] == '`'))
                {
                    Literal(rtf,s,plain,i-plain); Literal(rtf,s,i+1,1);
                    i += 2; plain = i; continue;
                }
                // Preserve math delimiters and operators exactly (including '*' and '_').
                if (ch == '$' || (ch == '\\' && i+1 < s.Length && (s[i+1] == '(' || s[i+1] == '[')))
                {
                    string close = ch == '$' ? new string('$', Math.Min(Run(s,i),2)) : s[i+1] == '(' ? @"\)" : @"\]";
                    int end = unmatched.Contains(close) ? -1 : s.IndexOf(close, i+close.Length, StringComparison.Ordinal);
                    if (end >= 0) { i = end + close.Length; continue; }
                    unmatched.Add(close);
                }
                if (ch != '`' && ch != '*' && ch != '_') { i++; continue; }
                int n = Run(s, i), content = i+n;
                if (content >= s.Length || (ch != '`' && (n > 3 || Char.IsWhiteSpace(s[content]) ||
                    (ch == '_' && i > 0 && Char.IsLetterOrDigit(s[i-1]))))) { i = content; continue; }
                string marker = new string(ch,n);
                if (unmatched.Contains(marker)) { i = content; continue; }
                int endAt = s.IndexOf(marker, content, StringComparison.Ordinal);
                while (endAt >= 0)
                {
                    int length = Run(s,endAt);
                    if (length == n && endAt > content && (ch == '`' || (s[endAt-1] != '\\' && !Char.IsWhiteSpace(s[endAt-1]) &&
                        (ch != '_' || endAt+n == s.Length || !Char.IsLetterOrDigit(s[endAt+n]))))) break;
                    endAt = s.IndexOf(marker,endAt+length,StringComparison.Ordinal);
                }
                if (endAt < 0) { unmatched.Add(marker); i = content; continue; }
                Literal(rtf,s,plain,i-plain);
                rtf.Append(ch == '`' ? @"{\f1\fs22\cf2\highlight3 " : n == 1 ? @"{\i " : n == 2 ? @"{\b " : @"{\b\i ");
                if (ch == '`') Literal(rtf,s,content,endAt-content,false);
                else Inline(rtf,s.Substring(content,endAt-content),depth+1);
                rtf.Append('}');
                i = endAt+n; plain = i;
            }
            Literal(rtf,s,plain,s.Length-plain);
        }

        static void Literal(StringBuilder rtf,string text,int start,int count,bool useLatin=true)
        {
            bool latinRun=false;
            for (int i=start;i<start+count;i++)
            {
                char c=text[i];
                bool latin=useLatin && c>=32 && c<=255;
                if(latin!=latinRun) {rtf.Append(latin ? @"{\f2 " : "}"); latinRun=latin;}
                if (c=='\\' || c=='{' || c=='}') rtf.Append('\\').Append(c);
                else if (c=='\t') rtf.Append(@"\tab ");
                else if (c>=32 && c<127) rtf.Append(c);
                else if (c>=32) rtf.Append(@"\u").Append(unchecked((short)c)).Append('?');
            }
            if(latinRun) rtf.Append('}');
        }
    }
}
