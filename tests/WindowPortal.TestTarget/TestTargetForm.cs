using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WindowPortal.TestTarget;

internal sealed class TestTargetForm : Form, IMessageFilter
{
    private readonly Button _backgroundClickButton;
    private readonly TestTargetOptions _options;
    private int _backgroundClickCount;

    private int _wheelDelta;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    internal TestTargetForm(TestTargetOptions options)
    {
        _options = options;
        Text = options.Label;
        ClientSize = new Size(900, 560);
        MinimumSize = options.Passive ? Size.Empty : new Size(500, 320);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = options.SolidColor ?? Color.FromArgb(19, 24, 38);
        ForeColor = Color.White;
        ResizeRedraw = true;

        var instructionLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text = options.Passive
                ? options.Label
                : "WINDOW REGION TEST TARGET\r\n\r\nThe center button accepts forwarded background clicks.",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 17, FontStyle.Regular),
            ForeColor = Color.White,
            BackColor = Color.Transparent
        };
        Controls.Add(instructionLabel);

        _backgroundClickButton = new Button
        {
            AutoSize = false,
            Size = new Size(360, 92),
            Text = "BACKGROUND CLICKS: 0",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            BackColor = Color.FromArgb(245, 158, 11),
            ForeColor = Color.FromArgb(17, 24, 39),
            FlatStyle = FlatStyle.Flat,
            TabStop = false
        };
        _backgroundClickButton.FlatAppearance.BorderSize = 0;
        _backgroundClickButton.Visible = !options.Passive;
        _backgroundClickButton.Click += (_, _) =>
        {
            _backgroundClickCount++;
            _backgroundClickButton.Text = $"BACKGROUND CLICKS: {_backgroundClickCount}";
            UpdateWindowTitle();
            BringToFront();
            Activate();
            _ = SetForegroundWindow(Handle);
        };
        Controls.Add(_backgroundClickButton);
        PositionButton();
        Resize += (_, _) => PositionButton();
        Application.AddMessageFilter(this);
        UpdateWindowTitle();
    }

    public bool PreFilterMessage(ref Message message)
    {
        const int wmMouseWheel = 0x020A;
        if (message.Msg != wmMouseWheel || Control.FromHandle(message.HWnd) is null)
        {
            return false;
        }

        _wheelDelta += unchecked((short)((long)message.WParam >> 16));
        UpdateWindowTitle();
        return false;
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        if (_options.SolidColor is { } solidColor)
        {
            eventArgs.Graphics.Clear(solidColor);
            return;
        }

        using var brush = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(15, 23, 42),
            Color.FromArgb(30, 64, 175),
            LinearGradientMode.ForwardDiagonal);
        eventArgs.Graphics.FillRectangle(brush, ClientRectangle);
    }

    private void PositionButton()
    {
        _backgroundClickButton.Location = new Point(
            (ClientSize.Width - _backgroundClickButton.Width) / 2,
            (ClientSize.Height - _backgroundClickButton.Height) / 2);
        _backgroundClickButton.BringToFront();
    }

    private void UpdateWindowTitle()
    {
        Text = $"{_options.Label} | Clicks: {_backgroundClickCount} | Wheel: {_wheelDelta}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(this);
        }

        base.Dispose(disposing);
    }
}
