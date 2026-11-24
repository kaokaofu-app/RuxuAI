using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Windows.Markup;
using W=System.Windows;
using Media=System.Windows.Media;

namespace Jiyue
{
    internal static class Theme
    {
        internal const int RadiusSmall=6, RadiusMedium=10, RadiusLarge=14;
        internal const int Space4=4, Space8=8, Space12=12, Space16=16, Space24=24;
        internal const int MinTarget=32;

        internal enum Role { Background, Surface, SurfaceHover, Selected, Line, Ink, Muted, Accent, AccentHover, AccentPressed, Danger, Success, CodeBg }

        static readonly int[] light={0xF6F6F8,0xFFFFFF,0xF0F1F4,0xE4ECFD,0xE2E3E8,0x202126,0x5E606B,0x315FD9,0x2A52BD,0x2448A6,0xC93A2E,0x1F8F5F,0xF2F3F6};
        static readonly int[] dark={0x19191B,0x242427,0x2E2E32,0x2B3A5C,0x38393F,0xF1F1F4,0xA9AAB4,0x4C7DF0,0x5E8BF5,0x3F6DDB,0xFF7A6E,0x4CC38A,0x2C2C30};
        static Color Design(Role role,bool isDark)
        {
            return Color.FromArgb(unchecked((int)0xFF000000) | (isDark ? dark : light)[(int)role]);
        }
        internal static Color Background(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Window : Design(Role.Background,isDark);}
        internal static Color Surface(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Window : Design(Role.Surface,isDark);}
        internal static Color SurfaceHover(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Control : Design(Role.SurfaceHover,isDark);}
        internal static Color Selected(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Highlight : Design(Role.Selected,isDark);}
        internal static Color Line(bool isDark) {return SystemInformation.HighContrast ? SystemColors.WindowText : Design(Role.Line,isDark);}
        internal static Color Ink(bool isDark) {return SystemInformation.HighContrast ? SystemColors.WindowText : Design(Role.Ink,isDark);}
        internal static Color Muted(bool isDark) {return SystemInformation.HighContrast ? SystemColors.WindowText : Design(Role.Muted,isDark);}
        internal static Color Accent(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Highlight : Design(Role.Accent,isDark);}
        internal static Color AccentHover(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Highlight : Design(Role.AccentHover,isDark);}
        internal static Color AccentPressed(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Highlight : Design(Role.AccentPressed,isDark);}
        internal static Color Danger(bool isDark) {return SystemInformation.HighContrast ? SystemColors.WindowText : Design(Role.Danger,isDark);}
        internal static Color Success(bool isDark) {return SystemInformation.HighContrast ? SystemColors.WindowText : Design(Role.Success,isDark);}
        internal static Color CodeBg(bool isDark) {return SystemInformation.HighContrast ? SystemColors.Window : Design(Role.CodeBg,isDark);}
        internal static Color OnAccent { get {return SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White;} }
        internal static Color OnSelected(bool isDark) {return SystemInformation.HighContrast ? SystemColors.HighlightText : Ink(isDark);}

        internal static Color[] ReplyPalette(bool isDark)
        {
            return new[]{Design(Role.Ink,isDark),Design(Role.Accent,isDark),Design(Role.CodeBg,isDark),Design(Role.Muted,isDark),
                Design(Role.Selected,isDark),Design(Role.Ink,isDark),Design(Role.Surface,isDark),Design(Role.Danger,isDark)};
        }

        internal static void ApplyWpf(W.FrameworkElement root)
        {
            var baseUri=new Uri(Style.FontRoot+Path.DirectorySeparatorChar);
            W.Documents.TextElement.SetFontFamily(root,new Media.FontFamily(baseUri,"./Fonts/#Source Sans 3, ./Fonts/#Source Han Sans CN, Microsoft YaHei UI"));
            using(var stream=typeof(Theme).Assembly.GetManifestResourceStream("Jiyue.ScrollChrome.xaml"))
                root.Resources.MergedDictionaries.Add((W.ResourceDictionary)XamlReader.Load(stream));
        }

        internal static Media.Brush Brush(Color color)
        {
            var brush=new Media.SolidColorBrush(Media.Color.FromArgb(color.A,color.R,color.G,color.B));
            brush.Freeze();
            return brush;
        }
    }
}
