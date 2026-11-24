using System.Drawing;
using System.Windows.Forms;

namespace Jiyue
{
    internal sealed class TrayRenderer : ToolStripProfessionalRenderer
    {
        sealed class Colors : ProfessionalColorTable
        {
            bool Dark {get {return AppAppearance.IsDark;}}
            public override Color ToolStripDropDownBackground {get{return Theme.Surface(Dark);}}
            public override Color MenuBorder {get{return Theme.Line(Dark);}}
            public override Color MenuItemBorder {get{return Theme.SurfaceHover(Dark);}}
            public override Color MenuItemSelected {get{return Theme.SurfaceHover(Dark);}}
            public override Color MenuItemSelectedGradientBegin {get{return MenuItemSelected;}}
            public override Color MenuItemSelectedGradientEnd {get{return MenuItemSelected;}}
            public override Color ImageMarginGradientBegin {get{return ToolStripDropDownBackground;}}
            public override Color ImageMarginGradientMiddle {get{return ToolStripDropDownBackground;}}
            public override Color ImageMarginGradientEnd {get{return ToolStripDropDownBackground;}}
            public override Color SeparatorDark {get{return Theme.Line(Dark);}}
            public override Color SeparatorLight {get{return ToolStripDropDownBackground;}}
        }
        internal TrayRenderer():base(new Colors()) {RoundedEdges=false;}
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor=SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : Theme.Ink(AppAppearance.IsDark);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor=Theme.Muted(AppAppearance.IsDark);
            base.OnRenderArrow(e);
        }
    }
}
