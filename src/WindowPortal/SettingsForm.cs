namespace WindowPortal;

internal sealed class SettingsForm : Form
{
    private readonly Func<UserSettings, bool> _saveSettings;
    private readonly Label _shapeLabel = new();
    private readonly ComboBox _shapeInput = new();
    private readonly Label _radiusLabel = new();
    private readonly Label _radiusHint = new();
    private readonly NumericUpDown _radiusInput = new();
    private readonly Label _rectangleSizeLabel = new();
    private readonly Label _rectangleSizeHint = new();
    private readonly NumericUpDown _rectangleWidthInput = new();
    private readonly NumericUpDown _rectangleHeightInput = new();
    private readonly Label _featherWidthLabel = new();
    private readonly Label _featherWidthHint = new();
    private readonly NumericUpDown _featherWidthInput = new();
    private readonly Label _languageLabel = new();
    private readonly ComboBox _languageInput = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();
    private bool _updatingUi;

    internal SettingsForm(
        UserSettings settings,
        Icon applicationIcon,
        Func<UserSettings, bool> saveSettings)
    {
        _saveSettings = saveSettings;

        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 414);
        MinimumSize = new Size(460, 414);
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

    private string PortalMode =>
        _shapeInput.SelectedIndex == 1 ? UserSettings.RectangleMode : UserSettings.CircleMode;

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(24, 22, 24, 18)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 8; row++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        _shapeLabel.AutoSize = true;
        _shapeLabel.Anchor = AnchorStyles.Left;
        _shapeLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_shapeLabel, 0, 0);

        _shapeInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _shapeInput.Width = 190;
        _shapeInput.Anchor = AnchorStyles.Right;
        _shapeInput.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingUi)
            {
                UpdateModeEnabledState();
            }
        };
        root.Controls.Add(_shapeInput, 1, 0);

        _radiusLabel.AutoSize = true;
        _radiusLabel.Anchor = AnchorStyles.Left;
        _radiusLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_radiusLabel, 0, 1);

        _radiusInput.Minimum = UserSettings.MinimumRadius;
        _radiusInput.Maximum = UserSettings.MaximumRadius;
        _radiusInput.Increment = 10;
        _radiusInput.Width = 96;
        _radiusInput.TextAlign = HorizontalAlignment.Right;
        _radiusInput.Anchor = AnchorStyles.Right;
        root.Controls.Add(_radiusInput, 1, 1);

        _radiusHint.AutoSize = true;
        _radiusHint.ForeColor = SystemColors.GrayText;
        _radiusHint.Margin = new Padding(0, 2, 0, 18);
        root.SetColumnSpan(_radiusHint, 2);
        root.Controls.Add(_radiusHint, 0, 2);

        _rectangleSizeLabel.AutoSize = true;
        _rectangleSizeLabel.Anchor = AnchorStyles.Left;
        _rectangleSizeLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_rectangleSizeLabel, 0, 3);

        ConfigureRectangleDimension(_rectangleWidthInput, UserSettings.MinimumRectangleWidth, UserSettings.MaximumRectangleWidth);
        ConfigureRectangleDimension(_rectangleHeightInput, UserSettings.MinimumRectangleHeight, UserSettings.MaximumRectangleHeight);
        _rectangleWidthInput.ValueChanged += (_, _) => UpdateFeatherMaximum();
        _rectangleHeightInput.ValueChanged += (_, _) => UpdateFeatherMaximum();
        var rectangleSizePanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty
        };
        rectangleSizePanel.Controls.Add(_rectangleWidthInput);
        rectangleSizePanel.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "×",
            Anchor = AnchorStyles.None,
            Margin = new Padding(5, 6, 5, 0)
        });
        rectangleSizePanel.Controls.Add(_rectangleHeightInput);
        root.Controls.Add(rectangleSizePanel, 1, 3);

        _rectangleSizeHint.AutoSize = true;
        _rectangleSizeHint.ForeColor = SystemColors.GrayText;
        _rectangleSizeHint.Margin = new Padding(0, 2, 0, 18);
        root.SetColumnSpan(_rectangleSizeHint, 2);
        root.Controls.Add(_rectangleSizeHint, 0, 4);

        _featherWidthLabel.AutoSize = true;
        _featherWidthLabel.Anchor = AnchorStyles.Left;
        _featherWidthLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_featherWidthLabel, 0, 5);

        _featherWidthInput.Minimum = UserSettings.MinimumFeatherWidth;
        _featherWidthInput.Maximum = UserSettings.MaximumFeatherWidth;
        _featherWidthInput.Increment = 2;
        _featherWidthInput.Width = 96;
        _featherWidthInput.TextAlign = HorizontalAlignment.Right;
        _featherWidthInput.Anchor = AnchorStyles.Right;
        root.Controls.Add(_featherWidthInput, 1, 5);

        _featherWidthHint.AutoSize = true;
        _featherWidthHint.ForeColor = SystemColors.GrayText;
        _featherWidthHint.Margin = new Padding(0, 2, 0, 18);
        root.SetColumnSpan(_featherWidthHint, 2);
        root.Controls.Add(_featherWidthHint, 0, 6);

        _languageLabel.AutoSize = true;
        _languageLabel.Anchor = AnchorStyles.Left;
        _languageLabel.Margin = new Padding(0, 4, 16, 4);
        root.Controls.Add(_languageLabel, 0, 7);

        _languageInput.DropDownStyle = ComboBoxStyle.DropDownList;
        _languageInput.Width = 140;
        _languageInput.Anchor = AnchorStyles.Right;
        _languageInput.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingUi)
            {
                UpdateTexts();
            }
        };
        root.Controls.Add(_languageInput, 1, 7);

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
        root.Controls.Add(buttons, 0, 8);
    }

    private static void ConfigureRectangleDimension(
        NumericUpDown input,
        int minimum,
        int maximum)
    {
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Increment = 10;
        input.Width = 82;
        input.TextAlign = HorizontalAlignment.Right;
    }

    private void ApplySettings(UserSettings settings)
    {
        var normalized = settings.Normalize();
        _radiusInput.Value = normalized.Radius;
        _rectangleWidthInput.Value = normalized.RectangleWidth;
        _rectangleHeightInput.Value = normalized.RectangleHeight;
        UpdateFeatherMaximum();
        _featherWidthInput.Value = Math.Min(
            normalized.FeatherWidth,
            decimal.ToInt32(_featherWidthInput.Maximum));
        _updatingUi = true;
        _shapeInput.Items.Clear();
        _shapeInput.Items.Add(string.Empty);
        _shapeInput.Items.Add(string.Empty);
        _shapeInput.SelectedIndex = normalized.PortalMode == UserSettings.RectangleMode ? 1 : 0;
        _languageInput.Items.Clear();
        _languageInput.Items.Add(Localizer.Get(Localizer.Chinese).ChineseLanguage);
        _languageInput.Items.Add(Localizer.Get(Localizer.English).EnglishLanguage);
        _languageInput.SelectedIndex = normalized.Language == Localizer.English ? 1 : 0;
        _updatingUi = false;
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        var text = Localizer.Get(Language);
        var selectedShape = Math.Max(0, _shapeInput.SelectedIndex);
        Text = text.SettingsTitle;
        _shapeLabel.Text = text.PortalShapeLabel;
        _updatingUi = true;
        _shapeInput.Items[0] = text.CircleShape;
        _shapeInput.Items[1] = text.RectangleShape;
        _shapeInput.SelectedIndex = selectedShape;
        _updatingUi = false;
        _radiusLabel.Text = text.RadiusLabel;
        _radiusHint.Text = text.RadiusHint;
        _rectangleSizeLabel.Text = text.RectangleSizeLabel;
        _rectangleSizeHint.Text = text.RectangleSizeHint;
        _featherWidthLabel.Text = text.FeatherWidthLabel;
        _featherWidthHint.Text = text.FeatherWidthHint;
        _languageLabel.Text = text.LanguageLabel;
        _saveButton.Text = text.Save;
        _cancelButton.Text = text.Cancel;
        _shapeInput.AccessibleName = text.PortalShapeLabel;
        _radiusInput.AccessibleName = text.RadiusLabel;
        _rectangleWidthInput.AccessibleName = text.RectangleSizeLabel + " width";
        _rectangleHeightInput.AccessibleName = text.RectangleSizeLabel + " height";
        _featherWidthInput.AccessibleName = text.FeatherWidthLabel;
        _languageInput.AccessibleName = text.LanguageLabel;
        UpdateModeEnabledState();
    }

    private void UpdateModeEnabledState()
    {
        var rectangle = PortalMode == UserSettings.RectangleMode;
        _radiusLabel.Enabled = !rectangle;
        _radiusHint.Enabled = !rectangle;
        _radiusInput.Enabled = !rectangle;
        _rectangleSizeLabel.Enabled = rectangle;
        _rectangleSizeHint.Enabled = rectangle;
        _rectangleWidthInput.Enabled = rectangle;
        _rectangleHeightInput.Enabled = rectangle;
        _featherWidthLabel.Enabled = rectangle;
        _featherWidthHint.Enabled = rectangle;
        _featherWidthInput.Enabled = rectangle;
    }

    private void UpdateFeatherMaximum()
    {
        var geometryLimit = Math.Min(
            (decimal.ToInt32(_rectangleWidthInput.Value) - 1) / 2,
            (decimal.ToInt32(_rectangleHeightInput.Value) - 1) / 2);
        var maximum = Math.Min(UserSettings.MaximumFeatherWidth, geometryLimit);
        if (_featherWidthInput.Value > maximum)
        {
            _featherWidthInput.Value = maximum;
        }

        _featherWidthInput.Maximum = maximum;
    }

    private void SaveAndClose()
    {
        var settings = new UserSettings(
            decimal.ToInt32(_radiusInput.Value),
            Language,
            PortalMode,
            decimal.ToInt32(_rectangleWidthInput.Value),
            decimal.ToInt32(_rectangleHeightInput.Value),
            decimal.ToInt32(_featherWidthInput.Value));
        if (!_saveSettings(settings))
        {
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
