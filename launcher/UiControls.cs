using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace FreeBuffLauncher;

internal static class UiGeometry
{
    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 12;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.Transparent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float BorderWidth { get; set; } = 1F;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.White;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        using var path = UiGeometry.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (BorderColor == Color.Transparent || BorderWidth <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiGeometry.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class ModernButton : Button
{
    private Color _normalColor = Color.White;
    private Color _hoverColor = Color.WhiteSmoke;
    private Color _pressedColor = Color.Gainsboro;
    private Color _borderColor = Color.Transparent;
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 10;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float BorderWidth { get; set; } = 1F;

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleCenter;
        TabStop = true;
    }

    public void SetPalette(Color normal, Color hover, Color border, Color foreground)
    {
        _normalColor = normal;
        _hoverColor = hover;
        _pressedColor = ControlPaint.Dark(hover, 0.04F);
        _borderColor = border;
        ForeColor = foreground;
        BackColor = normal;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiGeometry.RoundedRectangle(bounds, CornerRadius);
        var fill = !Enabled
            ? Color.FromArgb(235, 239, 245)
            : _pressed ? _pressedColor : _hovered ? _hoverColor : _normalColor;
        using var brush = new SolidBrush(fill);
        pevent.Graphics.FillPath(brush, path);

        if (_borderColor != Color.Transparent && BorderWidth > 0)
        {
            using var pen = new Pen(Enabled ? _borderColor : Color.FromArgb(218, 224, 233), BorderWidth);
            pevent.Graphics.DrawPath(pen, path);
        }

        var textColor = Enabled ? ForeColor : Color.FromArgb(148, 158, 174);
        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            ClientRectangle,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        if (Focused && ShowFocusCues)
        {
            var focusBounds = Rectangle.Inflate(bounds, -4, -4);
            ControlPaint.DrawFocusRectangle(pevent.Graphics, focusBounds, textColor, fill);
        }
    }
}

internal sealed class InputBox : UserControl
{
    private readonly TextBox _textBox;
    private bool _focused;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 9;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(220, 226, 236);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusBorderColor { get; set; } = Color.FromArgb(37, 99, 235);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledBackColor { get; set; } = Color.FromArgb(245, 247, 250);

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [AllowNull]
    public override string Text
    {
        get => _textBox.Text;
        set => _textBox.Text = value ?? string.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _textBox.ReadOnly;
        set => _textBox.ReadOnly = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseSystemPasswordChar
    {
        get => _textBox.UseSystemPasswordChar;
        set => _textBox.UseSystemPasswordChar = value;
    }

    /// <summary>
    /// Sets a non-sensitive accessibility name for the composite control and
    /// its child TextBox.  InputBox overrides Text for data binding, so the
    /// default WinForms accessibility name would otherwise mirror the actual
    /// value (for example an API key or proxy password).
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SafeAccessibleName
    {
        get => AccessibleName ?? string.Empty;
        set
        {
            AccessibleName = value ?? string.Empty;
            if (_textBox is not null)
            {
                _textBox.AccessibleName = value ?? string.Empty;
                _textBox.AccessibleDescription = "输入内容已隐藏";
            }
        }
    }

    public InputBox()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        Height = 40;
        MinimumSize = new Size(80, 40);
        BackColor = Color.White;
        TabStop = false;

        _textBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            BackColor = BackColor,
            ForeColor = Color.FromArgb(30, 41, 59),
            TabStop = true,
            AccessibleRole = AccessibleRole.Text,
            AccessibleDescription = "输入内容已隐藏"
        };
        _textBox.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _textBox.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Controls.Add(_textBox);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutTextBox();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_textBox is not null) _textBox.BackColor = Enabled ? BackColor : DisabledBackColor;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _textBox.Enabled = Enabled;
        _textBox.BackColor = Enabled ? BackColor : DisabledBackColor;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        _textBox.Focus();
        base.OnClick(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        using var path = UiGeometry.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var brush = new SolidBrush(Enabled ? BackColor : DisabledBackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiGeometry.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
        using var pen = new Pen(_focused && Enabled ? FocusBorderColor : BorderColor, _focused && Enabled ? 1.5F : 1F);
        e.Graphics.DrawPath(pen, path);
    }

    private void LayoutTextBox()
    {
        if (_textBox is null) return;
        const int horizontalPadding = 12;
        var preferredHeight = _textBox.PreferredHeight;
        _textBox.SetBounds(
            horizontalPadding,
            Math.Max(1, (Height - preferredHeight) / 2),
            Math.Max(20, Width - horizontalPadding * 2),
            preferredHeight);
    }
}
