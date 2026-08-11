namespace WindowPortal;

internal sealed class PierceViewApplicationContext : ApplicationContext
{
    private const int DefaultPollMilliseconds = 16;

    private readonly UserSettingsStore _settingsStore;
    private readonly PortalRuntime _runtime = new();
    private readonly Control _uiDispatcher = new();
    private readonly Icon _applicationIcon;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _toggleItem = new();
    private readonly ToolStripMenuItem _settingsItem = new();
    private readonly ToolStripMenuItem _helpItem = new();
    private readonly ToolStripMenuItem _exitItem = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly int _pollMilliseconds;
    private UserSettings _settings;
    private SettingsForm? _settingsForm;
    private System.Windows.Forms.Timer? _readyTipTimer;
    private System.Windows.Forms.Timer? _autoExitTimer;
    private bool _runtimeEnabled;
    private bool _exiting;

    internal PierceViewApplicationContext(
        UserSettingsStore settingsStore,
        UserSettings settings,
        bool firstRun,
        int pollMilliseconds = DefaultPollMilliseconds,
        int? autoExitMilliseconds = null)
    {
        _settingsStore = settingsStore;
        _settings = settings.Normalize();
        _pollMilliseconds = pollMilliseconds;
        _applicationIcon = BrandResources.LoadApplicationIcon();

        _ = _uiDispatcher.Handle;
        _runtime.ErrorOccurred += OnRuntimeError;

        _toggleItem.Click += (_, _) => ToggleRuntime();
        _settingsItem.Click += (_, _) => ShowSettings();
        _helpItem.Click += (_, _) => ShowHelp();
        _exitItem.Click += (_, _) => ExitApplication();
        _trayMenu.Items.AddRange(
        [
            _toggleItem,
            new ToolStripSeparator(),
            _settingsItem,
            _helpItem,
            new ToolStripSeparator(),
            _exitItem
        ]);

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        ApplyLanguage();
        StartRuntime();

        // 仅首次写入默认设置文件；就绪提醒改为每次启用都提示
        if (firstRun)
        {
            PersistFirstRunDefaults();
        }

        if (_runtimeEnabled)
        {
            ShowReadyTip();
        }

        if (autoExitMilliseconds is { } delay)
        {
            ExitAfter(delay);
        }
    }

    protected override void ExitThreadCore()
    {
        Cleanup();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cleanup();
        }

        base.Dispose(disposing);
    }

    private void ToggleRuntime()
    {
        if (_runtimeEnabled)
        {
            PauseRuntime();
        }
        else
        {
            StartRuntime();
            if (_runtimeEnabled)
            {
                // 托盘「启动透视」重新启用时也提醒一次
                ShowReadyTip();
            }
        }
    }

    private void StartRuntime()
    {
        try
        {
            _runtime.Start(_settings.CreateGeometry(), _pollMilliseconds);
            _runtimeEnabled = true;
        }
        catch (Exception exception)
        {
            _runtimeEnabled = false;
            ShowRuntimeError(exception.Message);
        }

        ApplyLanguage();
    }

    private void PauseRuntime()
    {
        _runtimeEnabled = !_runtime.Stop();
        ApplyLanguage();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.ShowAndActivate();
            return;
        }

        _settingsForm = new SettingsForm(
            _settings,
            _applicationIcon,
            SaveSettings);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.ShowAndActivate();
    }

    private bool SaveSettings(UserSettings settings)
    {
        var normalized = settings.Normalize();
        var previous = _settings;
        try
        {
            _settingsStore.Save(normalized);
            _settings = normalized;
        }
        catch
        {
            _settings = previous;
            var text = Localizer.Get(normalized.Language);
            MessageBox.Show(
                text.SettingsSaveFailed,
                text.SettingsTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        if (_runtimeEnabled && !_runtime.Restart(_settings.CreateGeometry(), _pollMilliseconds))
        {
            _runtimeEnabled = false;
            ShowRuntimeError("The portal runtime did not stop in time.");
        }

        ApplyLanguage();
        return true;
    }

    private void ShowHelp()
    {
        var text = Localizer.Get(_settings.Language);
        MessageBox.Show(
            text.HelpBody,
            text.HelpTitle,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        ExitThread();
    }

    private void ApplyLanguage()
    {
        var text = Localizer.Get(_settings.Language);
        _toggleItem.Text = _runtimeEnabled ? text.TrayPause : text.TrayStart;
        _settingsItem.Text = text.TraySettings;
        _helpItem.Text = text.TrayHelp;
        _exitItem.Text = text.TrayExit;
        _notifyIcon.Text = _runtimeEnabled ? text.TrayRunning : text.TrayPaused;
    }

    private void OnRuntimeError(string detail)
    {
        PostToUi(() => ShowRuntimeError(detail));
    }

    private void ShowRuntimeError(string detail)
    {
        if (_exiting)
        {
            return;
        }

        var text = Localizer.Get(_settings.Language);
        var body = string.IsNullOrWhiteSpace(detail) ||
                   _settings.Language == Localizer.English
            ? text.PreviewUnavailableBody
            : $"{text.PreviewUnavailableBody}\n\n{detail}";
        _notifyIcon.ShowBalloonTip(
            4000,
            text.PreviewUnavailableTitle,
            body,
            ToolTipIcon.Warning);
    }

    private void PostToUi(Action action)
    {
        if (_exiting || _uiDispatcher.IsDisposed)
        {
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // The tray message loop is already shutting down.
        }
        catch (InvalidOperationException)
        {
            // The tray message loop is already shutting down.
        }
    }

    private void PersistFirstRunDefaults()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch
        {
            // Failure is reported only when the user explicitly saves settings.
        }
    }

    /// <summary>
    /// 托盘气泡提醒：每次程序启动启用、或从暂停重新启动透视时提示按住 F8。
    /// </summary>
    private void ShowReadyTip()
    {
        _readyTipTimer?.Stop();
        _readyTipTimer?.Dispose();
        _readyTipTimer = new System.Windows.Forms.Timer { Interval = 700 };
        _readyTipTimer.Tick += (_, _) =>
        {
            _readyTipTimer?.Stop();
            _readyTipTimer?.Dispose();
            _readyTipTimer = null;
            if (_exiting || !_runtimeEnabled)
            {
                return;
            }

            var text = Localizer.Get(_settings.Language);
            _notifyIcon.ShowBalloonTip(
                4000,
                text.FirstRunTitle,
                text.FirstRunBody,
                ToolTipIcon.Info);
        };
        _readyTipTimer.Start();
    }

    private void ExitAfter(int milliseconds)
    {
        _autoExitTimer = new System.Windows.Forms.Timer { Interval = milliseconds };
        _autoExitTimer.Tick += (_, _) =>
        {
            _autoExitTimer?.Stop();
            _autoExitTimer?.Dispose();
            _autoExitTimer = null;
            ExitApplication();
        };
        _autoExitTimer.Start();
    }

    private void Cleanup()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _runtime.ErrorOccurred -= OnRuntimeError;
        _runtime.Dispose();
        _readyTipTimer?.Stop();
        _readyTipTimer?.Dispose();
        _readyTipTimer = null;
        _autoExitTimer?.Stop();
        _autoExitTimer?.Dispose();
        _autoExitTimer = null;
        _settingsForm?.Close();
        _settingsForm = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayMenu.Dispose();
        _applicationIcon.Dispose();
        _uiDispatcher.Dispose();
    }
}
