using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Jiyue
{
    internal sealed partial class KeysWindow
    {
        const string HotkeyPath=@"HKEY_CURRENT_USER\Software\Jiyue\Hotkeys";
        static readonly string[] Names={"Region","Article","Scroll","Home"};
        internal static readonly string[] ActionNames={"框选解释","读取整篇","滚轮长截图","打开主窗口"};
        static readonly int[] Defaults={3<<16|(int)Keys.Q,3<<16|(int)Keys.E,0,0};
        readonly int[] bindings=new int[4],activeIds=new int[4];
        readonly Action[] commands;
        internal Action<int> Recording;

        internal static string Describe(int binding)
        {
            if(binding==0) return "";
            int modifiers=binding>>16;
            var parts=new List<string>();
            if((modifiers&2)!=0) parts.Add("Ctrl");
            if((modifiers&1)!=0) parts.Add("Alt");
            if((modifiers&4)!=0) parts.Add("Shift");
            if((modifiers&8)!=0) parts.Add("Win");
            parts.Add(new KeysConverter().ConvertToString((Keys)(binding&255)));
            return String.Join("+",parts);
        }
        internal string Label(int action) {return Describe(bindings[action]);}
        internal bool Conflict(int action) {return bindings[action]!=0 && activeIds[action]==0;}
        internal bool HasConflict { get {for(int i=0;i<bindings.Length;i++) if(Conflict(i)) return true; return false;} }
        static bool Valid(int binding)
        {
            int key=binding&65535,mods=binding>>16;
            return binding==0 || (mods>0 && mods<=15 && key>=8 && key<255 && key!=16 && key!=17 && key!=18 && key!=91 && key!=92);
        }
        void LoadBindings()
        {
            for(int i=0;i<Names.Length;i++) {
                int b=Defaults[i];
                try {
                    object value=Registry.GetValue(HotkeyPath,Names[i],b);
                    if(value is int && Valid((int)value)) b=(int)value;
                } catch(System.Security.SecurityException) { }
                  catch(UnauthorizedAccessException) { }
                  catch(System.IO.IOException) { }
                bindings[i]=b;
                if(b!=0 && RegisterHotKey(Handle,10+i,(uint)((b>>16)|0x4000),(uint)(b&255))) activeIds[i]=10+i;
            }
        }
        internal string Change(int action,int binding)
        {
            if(!Valid(binding)) return "请按下包含修饰键的组合键。";
            if(bindings[action]==binding && !Conflict(action)) return null;
            for(int i=0;i<bindings.Length;i++)
                if(i!=action && binding!=0 && bindings[i]==binding) return "已用于「"+ActionNames[i]+"」。";
            int old=activeIds[action],id=old==10+action?20+action:10+action;
            if(binding!=0 && !RegisterHotKey(Handle,id,(uint)((binding>>16)|0x4000),(uint)(binding&255)))
                return "已被其他程序占用，请换一组。";
            try {Registry.SetValue(HotkeyPath,Names[action],binding,RegistryValueKind.DWord);}
            catch(Exception ex) {
                if(binding!=0) UnregisterHotKey(Handle,id);
                if(!(ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is System.IO.IOException)) throw;
                return "快捷键未能保存。"+ex.Message;
            }
            activeIds[action]=binding==0?0:id;
            bindings[action]=binding;
            if(old!=0) UnregisterHotKey(Handle,old);
            return null;
        }
    }
}
