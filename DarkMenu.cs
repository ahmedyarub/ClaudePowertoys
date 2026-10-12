namespace ClaudePowertoys;

/// <summary>Drop-down menus in Claude Desktop's colours, with Windows 11's rounded corners.</summary>
static class DarkMenu
{
    public static readonly ToolStripRenderer Renderer = new DarkRenderer();

    /// <summary>Rounds the corners of every drop-down this renderer draws, as it opens.</summary>
    sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void InitializeItem(ToolStripItem item)
        {
            base.InitializeItem(item);
            if (item is ToolStripDropDownItem { DropDown: { } dropDown }) Round(dropDown);
        }

        protected override void Initialize(ToolStrip toolStrip)
        {
            base.Initialize(toolStrip);
            if (toolStrip is ToolStripDropDown dropDown) Round(dropDown);
        }

        static void Round(ToolStripDropDown dropDown)
        {
            dropDown.Opened -= OnOpened;
            dropDown.Opened += OnOpened;
        }

        static void OnOpened(object? sender, EventArgs e)
        {
            if (sender is ToolStripDropDown d) Theme.RoundCorners(d.Handle);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.RowText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // A plain accent check mark instead of the boxed default.
            var r = e.ImageRectangle;
            using var pen = new Pen(Theme.Accent, Math.Max(1.5f, r.Height / 8f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, [new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f), new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)]);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuBorder => Theme.CardBorder;
        public override Color MenuItemBorder => Theme.Selected;
        public override Color MenuItemSelected => Theme.Selected;
        public override Color MenuItemSelectedGradientBegin => Theme.Selected;
        public override Color MenuItemSelectedGradientEnd => Theme.Selected;
        public override Color MenuItemPressedGradientBegin => Theme.Selected;
        public override Color MenuItemPressedGradientEnd => Theme.Selected;
        public override Color CheckBackground => Theme.Card;
        public override Color CheckSelectedBackground => Theme.Selected;
        public override Color CheckPressedBackground => Theme.Selected;
        public override Color SeparatorDark => Theme.CardBorder;
        public override Color SeparatorLight => Theme.CardBorder;
    }
}
