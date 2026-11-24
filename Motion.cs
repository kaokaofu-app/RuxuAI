using System;
using System.Runtime.InteropServices;

namespace Jiyue
{
    internal static class Motion
    {
        internal const int Fast=120, Normal=180, Slow=240;
        [DllImport("user32.dll", SetLastError=true)]
        static extern bool SystemParametersInfo(uint action,uint param,out int enabled,uint flags);
        public static bool Enabled {
            get {
                int enabled;
                return SystemParametersInfo(0x1042,0,out enabled,0) && enabled!=0;
            }
        }
        public static double EaseOutCubic(double progress)
        {
            double remaining=1-Math.Max(0,Math.Min(1,progress));
            return 1-remaining*remaining*remaining;
        }
    }
}
