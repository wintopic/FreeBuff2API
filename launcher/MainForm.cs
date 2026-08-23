using System.Diagnostics;

namespace FreeBuffLauncher;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(245, 247, 251);
    private static readonly Color Card = Color.White;
    private static readonly Color Soft = Color.FromArgb(248, 250, 252);
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
    private static readonly Color Success = Color.FromArgb(16, 185, 129);
    private static readonly Color Danger = Color.FromArgb(239, 68, 68);
    private static readonly Color DangerHover = Color.FromArgb(220, 38, 38);
    private static readonly Color TextMain = Color.FromArgb(15, 23, 42);
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(226, 232, 240);

    private readonly AppPaths _paths;
    private readonly Image _logoImage;
    private EnvSettings _settings;
    private readonly CredentialStore _credentialStore;
    private readonly ServiceManager _serviceManager;
    private readonly LoginService _loginService;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private Label _serviceDot = null!;
    private Label _serviceStatus = null!;
    private Label _serviceDetail = null!;
    private Label _proxyDot = null!;
    private Label _proxyStatus = null!;
    private Label _proxyDetail = null!;
    private Label _accountDot = null!;
    private Label _accountStatus = null!;
    private Label _accountDetail = null!;
    private Label _messageDot = null!;
    private Label _messageLabel = null!;
    private ModernButton _powerButton = null!;
    private ModernButton _loginButton = null!;
    private ModernButton _copyAllButton = null!;
    private ModernButton _openFolderButton = null!;
    private ModernButton _saveProxyButton = null!;
    private ModernButton _accountProxyButton = null!;
    private ModernButton _showKeyButton = null!;
    private InputBox _baseUrlBox = null!;
    private InputBox _apiKeyBox = null!;
    private InputBox _proxyBox = null!;

    private bool _busy;
    private bool _refreshing;
    private bool _keyVisible;
    private CancellationTokenSource? _loginCancellation;
    private HealthInfo _lastHealth = HealthInfo.Stopped;

    public MainForm()
    {
        _paths = new AppPaths();
        _paths.ImportExistingDeploymentIfAvailable();
        _settings = EnvSettings.LoadOrCreate(_paths.EnvPath);
        _credentialStore = new CredentialStore(_paths.CredentialPath);
        _serviceManager = new ServiceManager(_paths, _settings);
        _loginService = new LoginService(_credentialStore);
        _logoImage = LoadLogoImage();

        BuildInterface();
        PopulateSettings();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            await RefreshStatusAsync(true);
            ActiveControl = _powerButton;
            _refreshTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            _refreshTimer.Stop();
            _loginCancellation?.Cancel();
        };
        Disposed += (_, _) => _logoImage.Dispose();
    }

    private void BuildInterface()
    {
        Text = "FreeBuff 桌面助手";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        ClientSize = new Size(820, 734);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;

        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Card };
        var logo = new PictureBox
        {
            Location = new Point(24, 21),
            Size = new Size(48, 48),
            BackColor = Color.Transparent,
            Image = _logoImage,
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false
        };
        header.Controls.Add(logo);
        header.Controls.Add(NewLabel("FreeBuff 桌面助手", 88, 18, 420, 32, 18F, FontStyle.Bold, TextMain));
        header.Controls.Add(NewLabel("本地 OpenAI 兼容接口 · 一键启动 · 安全登录", 89, 50, 520, 24, 9.5F, FontStyle.Regular, TextMuted));
        var versionPill = NewCard(712, 30, 76, 30, Color.FromArgb(239, 246, 255), Color.FromArgb(219, 234, 254), 15);
        var versionText = NewLabel("v1.9", 0, 0, 76, 30, 9F, FontStyle.Bold, Primary);
        versionText.TextAlign = ContentAlignment.MiddleCenter;
        versionPill.Controls.Add(versionText);
        header.Controls.Add(versionPill);
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(238, 242, 247) });
        Controls.Add(header);

        var statusCard = NewCard(24, 108, 772, 130);
        statusCard.Controls.Add(NewLabel("运行概览", 20, 12, 100, 26, 10.5F, FontStyle.Bold, TextMain));
        var refreshHint = NewLabel("每 4 秒自动刷新", 610, 13, 140, 24, 8.5F, FontStyle.Regular, TextMuted);
        refreshHint.TextAlign = ContentAlignment.MiddleRight;
        statusCard.Controls.Add(refreshHint);
        statusCard.Controls.Add(NewStatusTile(20, 47, 232, out _serviceDot, out _serviceStatus, out _serviceDetail));
        statusCard.Controls.Add(NewStatusTile(270, 47, 232, out _proxyDot, out _proxyStatus, out _proxyDetail));
        statusCard.Controls.Add(NewStatusTile(520, 47, 232, out _accountDot, out _accountStatus, out _accountDetail));
        Controls.Add(statusCard);

        _powerButton = NewButton("一键启动", 24, 254, 772, 64, Primary, Color.White, 13.5F, FontStyle.Bold, Primary);
        _powerButton.CornerRadius = 12;
        _powerButton.Click += async (_, _) => await PowerButtonClickedAsync();
        Controls.Add(_powerButton);

        _loginButton = NewButton("登录 / 添加账号", 24, 334, 244, 46, Card, Primary, 9.5F, FontStyle.Bold, Border);
        _copyAllButton = NewButton("复制接入信息", 288, 334, 244, 46, Card, TextMain, 9.5F, FontStyle.Bold, Border);
        _openFolderButton = NewButton("打开数据目录", 552, 334, 244, 46, Card, TextMain, 9.5F, FontStyle.Bold, Border);
        _loginButton.Click += async (_, _) =>
        {
            if (_loginCancellation is not null) _loginCancellation.Cancel();
            else await LoginAsync(false);
        };
        _copyAllButton.Click += (_, _) => CopyConnectionInfo();
        _openFolderButton.Click += (_, _) => Process.Start(new ProcessStartInfo(_paths.RuntimeDirectory) { UseShellExecute = true });
        Controls.AddRange([_loginButton, _copyAllButton, _openFolderButton]);

        var configCard = NewCard(24, 396, 772, 246);
        configCard.Controls.Add(NewLabel("连接配置", 20, 12, 100, 28, 10.5F, FontStyle.Bold, TextMain));
        configCard.Controls.Add(NewLabel("复制后可直接填入 OpenAI 兼容客户端", 112, 13, 360, 26, 8.5F, FontStyle.Regular, TextMuted));

        configCard.Controls.Add(NewLabel("Base URL", 20, 50, 80, 40, 9F, FontStyle.Regular, TextMuted));
        _baseUrlBox = NewTextBox(108, 50, 544, 40, true);
        _baseUrlBox.SafeAccessibleName = "Base URL";
        var copyBaseButton = NewButton("复制", 662, 50, 88, 40, Card, Primary, 9F, FontStyle.Bold, Border);
        copyBaseButton.Click += (_, _) => CopyText(_settings.BaseUrl, "Base URL 已复制");
        configCard.Controls.AddRange([_baseUrlBox, copyBaseButton]);

        configCard.Controls.Add(NewLabel("API Key", 20, 102, 80, 40, 9F, FontStyle.Regular, TextMuted));
        _apiKeyBox = NewTextBox(108, 102, 444, 40, true);
        _apiKeyBox.SafeAccessibleName = "API Key（内容已隐藏）";
        _apiKeyBox.UseSystemPasswordChar = true;
        _showKeyButton = NewButton("显示", 562, 102, 90, 40, Card, TextMain, 9F, FontStyle.Bold, Border);
        _showKeyButton.Click += (_, _) => ToggleApiKeyVisibility();
        var copyKeyButton = NewButton("复制", 662, 102, 88, 40, Card, Primary, 9F, FontStyle.Bold, Border);
        copyKeyButton.Click += (_, _) => CopyText(_settings.ApiKey, "API Key 已复制");
        configCard.Controls.AddRange([_apiKeyBox, _showKeyButton, copyKeyButton]);

        configCard.Controls.Add(NewLabel("本机代理", 20, 154, 80, 40, 9F, FontStyle.Regular, TextMuted));
        _proxyBox = NewTextBox(108, 154, 544, 40, false);
        _proxyBox.SafeAccessibleName = "本机代理地址（可选）";
        _saveProxyButton = NewButton("保存", 662, 154, 88, 40, Card, Primary, 9F, FontStyle.Bold, Border);
        _saveProxyButton.Click += async (_, _) => await SaveProxyAsync();
        configCard.Controls.AddRange([_proxyBox, _saveProxyButton]);

        configCard.Controls.Add(NewLabel("高级设置", 20, 204, 80, 28, 9F, FontStyle.Regular, TextMuted));
        _accountProxyButton = NewButton("账号独立代理…", 108, 202, 176, 34, Card, TextMain, 8.8F, FontStyle.Bold, Border);
        _accountProxyButton.Click += async (_, _) => await OpenAccountProxySettingsAsync();
        var proxyModeHint = NewLabel("默认关闭；普通用户保持统一使用上方本机代理即可", 298, 204, 440, 28, 8.5F, FontStyle.Regular, TextMuted);
        configCard.Controls.AddRange([_accountProxyButton, proxyModeHint]);
        Controls.Add(configCard);

        var messagePanel = NewCard(24, 658, 772, 44, Soft, Border, 10);
        _messageDot = NewLabel("●", 14, 9, 18, 26, 10F, FontStyle.Regular, TextMuted);
        _messageLabel = NewLabel("准备就绪。关闭本窗口不会停止已经运行的服务。", 38, 8, 710, 28, 9F, FontStyle.Regular, TextMuted);
        messagePanel.Controls.AddRange([_messageDot, _messageLabel]);
        Controls.Add(messagePanel);
        Controls.Add(NewLabel("提示：真实聊天可能消耗 Freebuff session；本地凭据请勿分享。", 26, 710, 760, 20, 8.5F, FontStyle.Regular, TextMuted));
    }

    private static Image LoadLogoImage()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("FreeBuffLauncher.Assets.logo.png")
            ?? throw new InvalidOperationException("无法加载应用 Logo。");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private async Task PowerButtonClickedAsync()
    {
        if (_busy) return;
        var health = await _serviceManager.GetHealthAsync();
        if (health.Running)
        {
            await RunBusyAsync("正在停止服务…", async () =>
            {
                await _serviceManager.StopAsync();
                SetMessage("服务已安全停止。", Success);
            });
            return;
        }

        if (_credentialStore.AccountCount == 0)
        {
            await LoginAsync(true);
            return;
        }

        await RunBusyAsync("正在启动服务…", async () =>
        {
            ApplyProxyFromTextBox();
            await _serviceManager.StartAsync();
            SetMessage("服务已启动，可以复制接入信息使用。", Success);
        });
    }

    private async Task LoginAsync(bool startAfterLogin)
    {
        if (_busy || _loginCancellation is not null) return;
        ApplyProxyFromTextBox();
        if (!await _serviceManager.IsProxyReachableAsync())
        {
            ShowFriendlyError("没有检测到本机代理，请先打开代理软件，或修改下方代理地址。");
            return;
        }

        _busy = true;
        _loginCancellation = new CancellationTokenSource();
        SetControlsEnabled(false);
        _loginButton.Enabled = true;
        _loginButton.Text = "取消登录";
        Cursor = Cursors.WaitCursor;

        try
        {
            SetMessage("正在向 Freebuff 获取登录链接…", Primary);
            var request = await _loginService.CreateRequestAsync(_settings.ProxyUrl, _loginCancellation.Token);
            try
            {
                LoginService.OpenBrowser(request.LoginUrl);
            }
            catch
            {
                Clipboard.SetText(request.LoginUrl);
                MessageBox.Show(this, "无法自动打开浏览器，登录链接已经复制，请粘贴到浏览器打开。", "请完成登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            SetMessage("浏览器已打开，请完成 Freebuff 登录；成功后这里会自动继续。", Primary);
            var result = await _loginService.WaitForLoginAsync(
                request,
                _settings.ProxyUrl,
                text => BeginInvoke(() => SetMessage(text, Primary)),
                _loginCancellation.Token);

            var wasRunning = (await _serviceManager.GetHealthAsync()).Running;
            if (wasRunning)
            {
                SetMessage("登录成功，正在重新加载账号…", Primary);
                await _serviceManager.RestartAsync();
            }
            else if (startAfterLogin)
            {
                SetMessage("登录成功，正在启动服务…", Primary);
                await _serviceManager.StartAsync();
            }
            SetMessage($"登录成功：{MaskEmail(result.Email)}", Success);
        }
        catch (OperationCanceledException)
        {
            SetMessage("已取消登录。", TextMuted);
        }
        catch (Exception ex)
        {
            ShowFriendlyError(ex.Message);
        }
        finally
        {
            _loginCancellation.Dispose();
            _loginCancellation = null;
            _loginButton.Text = "登录 / 添加账号";
            _busy = false;
            Cursor = Cursors.Default;
            SetControlsEnabled(true);
            await RefreshStatusAsync(true);
        }
    }

    private async Task SaveProxyAsync()
    {
        if (_busy) return;
        try
        {
            ApplyProxyFromTextBox();
        }
        catch (Exception ex)
        {
            ShowFriendlyError(ex.Message);
            return;
        }

        await RunBusyAsync("正在检测代理…", async () =>
        {
            if (!await _serviceManager.IsProxyReachableAsync())
                throw new InvalidOperationException("代理地址已保存，但当前无法连接。请确认代理软件已经开启。");

            if ((await _serviceManager.GetHealthAsync()).Running)
            {
                SetMessage("代理可用，正在重启服务应用新设置…", Primary);
                await _serviceManager.RestartAsync();
            }
            SetMessage("代理设置已保存并检测通过。", Success);
        });
    }

    private async Task OpenAccountProxySettingsAsync()
    {
        if (_busy) return;
        using var dialog = new ProxySettingsForm(_settings, _credentialStore);
        if (dialog.ShowDialog(this) != DialogResult.OK || !dialog.Saved) return;

        await RunBusyAsync("正在应用账号代理设置…", async () =>
        {
            _settings.Save(_paths.EnvPath);
            _serviceManager.UpdateSettings(_settings);
            if ((await _serviceManager.GetHealthAsync()).Running)
            {
                await _serviceManager.RestartAsync();
            }
            SetMessage(_settings.PerAccountProxyEnabled
                ? "账号独立代理已启用并保存。"
                : "账号独立代理已关闭，所有账号使用本机代理。", Success);
        });
    }

    private void ApplyProxyFromTextBox()
    {
        var normalized = EnvSettings.NormalizeProxy(_proxyBox.Text);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Port <= 0)
            throw new InvalidOperationException("代理地址格式不正确。示例：http://127.0.0.1:3067");
        _settings.ProxyUrl = normalized;
        _settings.Save(_paths.EnvPath);
        _serviceManager.UpdateSettings(_settings);
        _proxyBox.Text = normalized;
    }

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetControlsEnabled(false);
        Cursor = Cursors.WaitCursor;
        SetMessage(message, Primary);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowFriendlyError(ex.Message);
        }
        finally
        {
            _busy = false;
            Cursor = Cursors.Default;
            SetControlsEnabled(true);
            await RefreshStatusAsync(true);
        }
    }

    private async Task RefreshStatusAsync(bool force = false)
    {
        if (_refreshing || (_busy && !force)) return;
        _refreshing = true;
        try
        {
            var healthTask = _serviceManager.GetHealthAsync();
            var proxyTask = _serviceManager.IsProxyReachableAsync();
            await Task.WhenAll(healthTask, proxyTask);
            _lastHealth = healthTask.Result;
            var proxyOk = proxyTask.Result;
            var accountCount = _credentialStore.AccountCount;

            _serviceDot.ForeColor = _lastHealth.Running ? Success : TextMuted;
            _serviceStatus.Text = _lastHealth.Running ? "服务运行中" : "服务未启动";
            _serviceStatus.ForeColor = _lastHealth.Running ? Success : TextMain;
            _serviceDetail.Text = _lastHealth.Running
                ? $"127.0.0.1:{_settings.Port} · v{_lastHealth.Version}"
                : "点击下方按钮启动";

            _proxyDot.ForeColor = proxyOk ? Success : Danger;
            _proxyStatus.Text = proxyOk ? "代理已连接" : "代理未连接";
            _proxyStatus.ForeColor = proxyOk ? Success : Danger;
            _proxyDetail.Text = RedactProxy(_settings.ProxyUrl);

            _accountDot.ForeColor = accountCount > 0 ? Success : Danger;
            var accountExtra = _credentialStore.AccountDescription;
            _accountStatus.Text = accountCount > 0 ? $"已登录 {accountCount} 个账号" : "尚未登录";
            _accountStatus.ForeColor = accountCount > 0 ? Success : Danger;
            _accountDetail.Text = accountCount > 0
                ? (string.IsNullOrWhiteSpace(accountExtra) ? "凭据已保存在本机" : accountExtra)
                : "点击“登录 / 添加账号”";

            if (_lastHealth.Running)
            {
                _powerButton.Text = "停止服务";
                _powerButton.SetPalette(Danger, DangerHover, Danger, Color.White);
            }
            else
            {
                _powerButton.Text = accountCount == 0 ? "登录并启动" : "一键启动";
                _powerButton.SetPalette(Primary, PrimaryHover, Primary, Color.White);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void PopulateSettings()
    {
        _baseUrlBox.Text = _settings.BaseUrl;
        _apiKeyBox.Text = _settings.ApiKey;
        _proxyBox.Text = _settings.ProxyUrl;
    }

    private void CopyConnectionInfo()
    {
        var content = $"Base URL: {_settings.BaseUrl}{Environment.NewLine}API Key: {_settings.ApiKey}";
        CopyText(content, "接入信息已复制");
    }

    private void CopyText(string text, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            SetMessage(successMessage + "。", Success);
        }
        catch
        {
            ShowFriendlyError("复制失败，请稍后重试。");
        }
    }

    private void ToggleApiKeyVisibility()
    {
        _keyVisible = !_keyVisible;
        _apiKeyBox.UseSystemPasswordChar = !_keyVisible;
        _showKeyButton.Text = _keyVisible ? "隐藏" : "显示";
    }

    private void SetControlsEnabled(bool enabled)
    {
        _powerButton.Enabled = enabled;
        _loginButton.Enabled = enabled;
        _copyAllButton.Enabled = enabled;
        _openFolderButton.Enabled = enabled;
        _saveProxyButton.Enabled = enabled;
        _accountProxyButton.Enabled = enabled;
        _proxyBox.Enabled = enabled;
    }

    private void SetMessage(string text, Color color)
    {
        _messageLabel.Text = text;
        _messageLabel.ForeColor = color;
        _messageDot.ForeColor = color;
    }

    private void ShowFriendlyError(string message)
    {
        SetMessage(message, Danger);
        MessageBox.Show(this, message, "FreeBuff 桌面助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return email;
        return email[..Math.Min(2, at)] + "***" + email[at..];
    }

    private static string RedactProxy(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "直连";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "代理地址已设置";
        var host = uri.Host.Contains(':', StringComparison.Ordinal) ? $"[{uri.Host}]" : uri.Host;
        var port = uri.Port > 0 && !uri.IsDefaultPort ? $":{uri.Port}" : string.Empty;
        return $"{uri.Scheme}://{(string.IsNullOrWhiteSpace(uri.UserInfo) ? string.Empty : "***@")}{host}{port}";
    }

    private static RoundedPanel NewCard(
        int x,
        int y,
        int width,
        int height,
        Color? backColor = null,
        Color? borderColor = null,
        int radius = 14) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, height),
        BackColor = backColor ?? Card,
        BorderColor = borderColor ?? Border,
        BorderWidth = 1F,
        CornerRadius = radius
    };

    private static RoundedPanel NewStatusTile(
        int x,
        int y,
        int width,
        out Label dot,
        out Label status,
        out Label detail)
    {
        var tile = NewCard(x, y, width, 64, Soft, Color.FromArgb(237, 241, 247), 10);
        dot = NewLabel("●", 15, 16, 18, 28, 11F, FontStyle.Regular, TextMuted);
        status = NewLabel("正在检查…", 41, 8, width - 54, 26, 10F, FontStyle.Bold, TextMain);
        detail = NewLabel("请稍候", 41, 33, width - 54, 22, 8.5F, FontStyle.Regular, TextMuted);
        tile.Controls.AddRange([dot, status, detail]);
        return tile;
    }

    private static Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, height),
        Font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point),
        ForeColor = color,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private static InputBox NewTextBox(int x, int y, int width, int height, bool readOnly)
    {
        return new InputBox
        {
            Location = new Point(x, y),
            Size = new Size(width, height),
            ReadOnly = readOnly,
            BackColor = readOnly ? Soft : Color.White,
            BorderColor = Border,
            FocusBorderColor = Primary,
            CornerRadius = 9
        };
    }

    private static ModernButton NewButton(
        string text,
        int x,
        int y,
        int width,
        int height,
        Color backColor,
        Color foreColor,
        float fontSize,
        FontStyle fontStyle,
        Color? borderColor = null)
    {
        var button = new ModernButton
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            Font = new Font("Microsoft YaHei UI", fontSize, fontStyle, GraphicsUnit.Point),
            CornerRadius = 10,
            BorderWidth = 1F
        };
        var hover = backColor == Card ? Soft : ControlPaint.Dark(backColor, 0.04F);
        button.SetPalette(backColor, hover, borderColor ?? backColor, foreColor);
        return button;
    }
}
