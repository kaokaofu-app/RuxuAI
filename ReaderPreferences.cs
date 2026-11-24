using System;
using Microsoft.Win32;

namespace Jiyue
{
    internal static class ReaderPreferences
    {
        const string Path=@"HKEY_CURRENT_USER\Software\Jiyue";
        internal static int ReadInt(string name,int fallback)
        {
            try {object v=Registry.GetValue(Path,name,fallback); return v is int?(int)v:fallback;}
            catch(System.Security.SecurityException) {return fallback;}
            catch(UnauthorizedAccessException) {return fallback;}
            catch(System.IO.IOException) {return fallback;}
        }
        internal static void WriteInt(string name,int value) {Registry.SetValue(Path,name,value,RegistryValueKind.DWord);}
        static int fontSize=Math.Min(18,Math.Max(10,ReadInt("AnswerFontSize",12)));
        internal static event Action FontSizeChanged;
        internal static int FontSize {
            get {return fontSize;}
            set {
                int next=Math.Max(10,Math.Min(18,value));
                if(next==fontSize) return;
                try {WriteInt("AnswerFontSize",next);}
                catch(System.Security.SecurityException) { }
                catch(UnauthorizedAccessException) { }
                catch(System.IO.IOException) { }
                fontSize=next;
                var notify=FontSizeChanged; if(notify!=null) notify();
            }
        }
    }
}
