namespace WindowPortal;

internal sealed class SettingsForm : Form
{
    private readonly Func<UserSettings, bool> _saveSettings;
    private readonly Label _radiusLabel = new();
    private readonly Label _radiusHint = new();
    private readonly NumericUpDown _radiusInput = new();
    private readonly Label _languageLabel = new();
    private readonly ComboBox _languageInput = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();
    private bool _updatingLanguage;

    internal SettingsForm(
        UserSettings settings,
        Icon applicationIcon,
        Func<UserSettings, bool> saveSettings)
    {
        _saveSettings = saveSettings;

        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 224);
        MinimumSize = new Size(400, 224);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        Icon = applicationIcon;

        BuildLayout();
        ApplySettings(settings);
    }

    internal void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
    }

    private string Language =>
        _languageInput.SelectedIndex == 1 ? Localizer.English : Localizer.Chinese;

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(24, 22, 24, 18)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        _radiusLabel.AutoSize = true;
        _radiusLabel.Anchor = AnchorStyles.Left;
        _radiusLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_radiusLabel, 0, 0);

        _radiusInput.Minimum = UserSettings.MinimumRadius;
        _radiusInput.Maximum = UserSettings.MaximumRadius;
        _radiusInput.Increment = 10;
        _radiusInput.Width = 96;
        _radiusInput.TextAlign = HorizontalAlignment.Right;
        _radiusInput.Anchor = AnchorStyles.Right;
        root.Controls.Add(_radiusInput, 1, 0);

        _radiusHint.AutoSize = true;
        _radiusHint.ForeColor = SystemColors.GrayText;
        _radiusHint.Margin = new Padding(0, 2, 0, 18);
        root.SetColumnSpan(_radiusHint, 2);
        root.Controls.Add(_radiusHint, 0, 1);

        _languageLabel.AutoSize = true;
        _languageLabel.Anchor = AnchorStyles.Left;
        _languageLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_languageLabel, 0, 2);

        _languageInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageInput.Width = 140;
        _languageInput.Anchor = AnchorStyles.Right;
        _languageInput.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingLanguage)
            {
                UpdateTexts();
            }
        };
        root.Controls.Add(_languageInput, 1, 2);

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 26, 0, 0)
        };
        _saveButton.AutoSize = true;
        _saveButton.Padding = new Padding(10, 3, 10, 3);
        _saveButton.Click += (_, _) => SaveAndClose();
        AcceptButton = _saveButton;
        _cancelButton.AutoSize = true;
        _cancelButton.Padding = new Padding(10, 3, 10, 3);
        _cancelButton.DialogResult = DialogResult.Cancel;
        CancelButton = _cancelButton;
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_cancelButton);
        root.SetColumnSpan(buttons, 2);
        root.Controls.Add(buttons, 0, 3);
    }

    private void ApplySettings(UserSettings settings)
    {
        var normalized = settings.Normalize();
        _radiusInput.Value = normalized.Radius;
        _updatingLanguage = true;
        _languageInput.Items.Clear();
        _languageInput.Items.Add(Localizer.Get(Localizer.Chinese).ChineseLanguage);
        _languageInput.Items.Add(Localizer.Get(Localizer.English).EnglishLanguage);
        _languageInput.SelectedIndex = normalized.Language == Localizer.English ? 1 : 0;
        _updatingLanguage = false;
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        var text = Localizer.Get(Language);
        Text = text.SettingsTitle;
        _radiusLabel.Text = text.RadiusLabel;
        _radiusHint.Text = text.RadiusHint;
        _languageLabel.Text = text.LanguageLabel;
        _saveButton.Text = text.Save;
        _cancelButton.Text = text.Cancel;
        _radiusInput.AccessibleName = text.RadiusLabel;
        _languageInput.AccessibleName = text.LanguageLabel;
    }

    private void SaveAndClose()
    {
        var settings = new UserSettings(
            decimal.ToInt32(_radiusInput.Value),
            Language);
        if (!_saveSettings(settings))
        {
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
