namespace ClaudePowertoys;

/// <summary>Dark drop-down menus, in the session switcher's colours.</summary>
static class DarkMenu
{
    static readonly Color Background = Color.FromArgb(38, 38, 36);
    static readonly Color Hover = Color.FromArgb(70, 70, 66);
    static readonly Color Border = Color.FromArgb(70, 70, 66);
    static readonly Color Text = Color.FromArgb(240, 238, 230);
    static readonly Color DimText = Color.FromArgb(160, 158, 150);

    public static readonly ToolStripRenderer Renderer = new DarkRenderer();

    sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : DimText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
