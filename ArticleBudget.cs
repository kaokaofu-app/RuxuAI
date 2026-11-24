using System;
using System.Collections.Generic;
using System.Text;

namespace Jiyue
{
    internal static class ArticleBudget
    {
        // Units are quarters of an estimated token. Providers use different tokenizers.
        const int SectionUnits = 4000 * 4;
        const int HistoryUnits = 2000 * 4;

        static int Cost(char c) { return c < 128 ? 1 : 5; }

        public static List<string> Sections(string text)
        {
            var sections = new List<string>();
            int start = 0;
            while (start < text.Length) {
                int end = start, units = 0, paragraph = start;
                while (end < text.Length && units + Cost(text[end]) <= SectionUnits) {
                    units += Cost(text[end++]);
                    if (units > SectionUnits * 3 / 4 && text[end-1] == '\n') paragraph = end;
                }
                if (end < text.Length) {
                    if (paragraph > start) end = paragraph;
                    else if (Char.IsLowSurrogate(text[end])) end--;
                    if (end > start && text[end-1] == '\r' && text[end] == '\n') end--;
                }
                sections.Add(text.Substring(start,end-start));
                start = end;
            }
            return sections;
        }

        public static string Overview(string text)
        {
            // A short first pass includes the end even when the document spans many sections.
            var brief = new StringBuilder("以下是全文四处节选。只根据节选作简短概述。\n");
            string[] labels = {"开头","约三分之一处","约三分之二处","结尾"};
            for (int part = 0; part < labels.Length; part++) {
                int anchor = (int)((long)(text.Length-1)*part/3);
                int end = anchor, start = anchor, units = 0;
                if (part == 3) {
                    end = text.Length; start = end;
                    while (start > 0 && units + Cost(text[start-1]) <= 1600) units += Cost(text[--start]);
                } else {
                    if (part > 0) {
                        int boundary = text.IndexOf('\n', anchor, Math.Min(130, text.Length-anchor));
                        if (boundary >= 0) start = boundary+1;
                    }
                    end = start;
                    while (end < text.Length && units + Cost(text[end]) <= 1600) units += Cost(text[end++]);
                }
                if (start < end && Char.IsLowSurrogate(text[start])) start++;
                if (end > start && Char.IsHighSurrogate(text[end-1])) end--;
                brief.Append("\n【").Append(labels[part]).Append("】\n").Append(text,start,end-start).AppendLine();
            }
            return brief.ToString();
        }

        public static List<KeyValuePair<string,string>> ForRequestHistory(List<KeyValuePair<string,string>> history)
        {
            if (history == null) return new List<KeyValuePair<string,string>>();
            int first = history.Count, remaining = HistoryUnits;
            while (first >= 2 && history[first-2].Key == "user" && history[first-1].Key == "assistant") {
                for (int index = first-2; index < first; index++)
                    foreach (char c in history[index].Value ?? "") { remaining -= Cost(c); if (remaining < 0) break; }
                if (remaining < 0) break;
                first -= 2;
            }
            return history.GetRange(first,history.Count-first);
        }
    }
}
