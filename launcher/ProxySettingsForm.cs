using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace FreeBuffLauncher;

internal sealed class ProxySettingsForm : Form
{
    private static readonly Color Background = Color.FromArgb(245, 247, 251);
    private static readonly Color Card = Color.White;
    private static readonly Color Soft = Color.FromArgb(248, 250, 252);
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color Success = Color.FromArgb(16, 185, 129);
    private static readonly Color Danger = Color.FromArgb(239, 68, 68);
    private static readonly Color TextMain = Color.FromArgb(15, 23, 42);
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(226, 232, 240);

    private readonly EnvSettings _settings;
    private readonly CredentialStore _credentialStore;
    private readonly List<AccountEditor> _editors = [];
    private readonly Panel _accountsPanel;
    private readonly CheckBox _enableCheck;
    private readonly Label _hintLabel;
    private readonly ModernButton _saveButton;
    private readonly ModernButton _testButton;

    public bool Saved { get; private set; }

    public ProxySettingsForm(EnvSettings settings, CredentialStore credentialStore)
    {
        _settings = settings;
        _credentialStore = credentialStore;

        Text = "账号独立代理";
        ClientSize = new Size(720, 590);
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Background;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var header = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Card };
        header.Controls.Add(NewLabel("账号独立代理", 24, 15, 420, 32, 17F, FontStyle.Bold, TextMain));
        header.Controls.Add(NewLabel("高级功能 · 每个账号可使用不同出口，默认关闭", 25, 48, 600, 24, 9F, FontStyle.Regular, TextMuted));
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Border });
        Controls.Add(header);

        var switchCard = NewCard(24, 106, 672, 76);
        _enableCheck = new CheckBox
        {
            Text = "启用账号独立代理",
            Location = new Point(20, 14),
            Size = new Size(260, 28),
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = TextMain,
            BackColor = Color.Transparent,
            Checked = settings.PerAccountProxyEnabled,
            AutoSize = false
        };
        _enableCheck.CheckedChanged += (_, _) => UpdateEnabledState();
        switchCard.Controls.Add(_enableCheck);
        switchCard.Controls.Add(NewLabel("留空的账号仍使用上方“本机代理”；只有填写的账号走自己的代理。", 42, 42, 600, 22, 8.5F, FontStyle.Regular, TextMuted));
        Controls.Add(switchCard);

        var listCard = NewCard(24, 198, 672, 290);
        listCard.Controls.Add(NewLabel("账号与出口", 20, 11, 160, 28, 10.5F, FontStyle.Bold, TextMain));
        listCard.Controls.Add(NewLabel("代理密码不会在状态栏、日志或错误消息中回显", 250, 13, 394, 24, 8.5F, FontStyle.Regular, TextMuted));
        _accountsPanel = new Panel
        {
            Location = new Point(16, 48),
            Size = new Size(640, 226),
            BackColor = Soft,
            AutoScroll = true
        };
        listCard.Controls.Add(_accountsPanel);
        Controls.Add(listCard);

        _hintLabel = NewLabel("准备就绪。", 26, 500, 450, 28, 9F, FontStyle.Regular, TextMuted);
        Controls.Add(_hintLabel);

        var cancel = NewButton("取消", 458, 530, 104, 42, Card, TextMain, Border);
        cancel.DialogResult = DialogResult.Cancel;
        _testButton = NewButton("测试选中代理", 282, 530, 160, 42, Card, Primary, Border);
        _testButton.Click += async (_, _) => await TestSelectedAsync();
        _saveButton = NewButton("保存设置", 576, 530, 120, 42, Primary, Color.White, Primary);
        _saveButton.Click += (_, _) => SaveSettings();
        Controls.AddRange([_testButton, cancel, _saveButton]);
        CancelButton = cancel;
        AcceptButton = _saveButton;

        BuildAccountEditors();
        UpdateEnabledState();
    }

    private void BuildAccountEditors()
    {
        var accounts = _credentialStore.GetAccounts();
        if (accounts.Count == 0)
        {
            _accountsPanel.Controls.Add(NewLabel("还没有登录账号。请先关闭此窗口并点击“登录 / 添加账号”。", 18, 78, 600, 40, 9.5F, FontStyle.Regular, TextMuted));
            return;
        }

        var y = 10;
        var accountIndex = 0;
        foreach (var account in accounts)
        {
            accountIndex++;
            var row = NewCard(10, y, 600, 92, Card, Border, 10);
            var display = string.IsNullOrWhiteSpace(account.Email)
                ? account.DisplayName
                : $"{account.DisplayName}  ·  {MaskEmail(account.Email)}";
            row.Controls.Add(NewLabel(display, 14, 9, 430, 25, 9F, FontStyle.Bold, TextMain));
            var direct = NewLabel(string.IsNullOrWhiteSpace(account.ProxyUrl) ? "使用本机代理" : RedactProxy(account.ProxyUrl), 448, 9, 136, 25, 8F, FontStyle.Regular, TextMuted);
            direct.TextAlign = ContentAlignment.MiddleRight;
            row.Controls.Add(direct);
            var box = NewTextBox(14, 42, 480, 38);
            box.SafeAccessibleName = $"账号 {accountIndex} 代理地址（内容已隐藏）";
            box.Text = account.ProxyUrl;
            box.UseSystemPasswordChar = true;
            var show = NewButton("显示", 504, 42, 80, 38, Card, TextMain, Border);
            show.Click += (_, _) =>
            {
                box.UseSystemPasswordChar = !box.UseSystemPasswordChar;
                show.Text = box.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            row.Controls.AddRange([box, show]);
            _accountsPanel.Controls.Add(row);
            _editors.Add(new AccountEditor(account, box, row));
            y += 102;
        }
        _accountsPanel.AutoScrollMinSize = new Size(0, y + 4);
    }

    private void UpdateEnabledState()
    {
        foreach (var editor in _editors)
        {
            editor.Box.Enabled = _enableCheck.Checked;
            foreach (Control control in editor.Row.Controls)
            {
                if (control is ModernButton) control.Enabled = _enableCheck.Checked;
            }
        }
        _testButton.Enabled = _enableCheck.Checked && _editors.Count > 0;
        _hintLabel.Text = _enableCheck.Checked
            ? "填写后保存，运行中的服务会由主窗口自动重启。"
            : "当前关闭，所有账号统一使用主窗口里的本机代理。";
        _hintLabel.ForeColor = TextMuted;
    }

    private void SaveSettings()
    {
        var proxies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_enableCheck.Checked)
        {
            try
            {
                _settings.PerAccountProxyEnabled = false;
                _credentialStore.SaveAccountProxies(proxies);
                Saved = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch
            {
                _hintLabel.Text = "保存失败，请确认程序目录可写，然后重试。";
                _hintLabel.ForeColor = Danger;
            }
            return;
        }
        foreach (var editor in _editors)
        {
            if (!EnvSettings.TryNormalizeAccountProxy(editor.Box.Text, out var normalized, out var error))
            {
                _hintLabel.Text = $"{editor.Account.DisplayName}：{error}";
                _hintLabel.ForeColor = Danger;
                editor.Box.Focus();
                return;
            }
            proxies[editor.Account.Key] = normalized;
        }

        try
        {
            _credentialStore.SaveAccountProxies(proxies);
            _settings.PerAccountProxyEnabled = _enableCheck.Checked;
            Saved = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            _hintLabel.Text = "保存失败，请确认程序目录可写，然后重试。";
            _hintLabel.ForeColor = Danger;
        }
    }

    private async Task TestSelectedAsync()
    {
        var editor = _editors.FirstOrDefault(item => item.Box.Focused)
                     ?? _editors.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Box.Text));
        if (editor is null)
        {
            _hintLabel.Text = "请先填写一个账号代理。";
            _hintLabel.ForeColor = Danger;
            return;
        }
        if (!EnvSettings.TryNormalizeAccountProxy(editor.Box.Text, out var normalized, out var error) || string.IsNullOrWhiteSpace(normalized))
        {
            _hintLabel.Text = string.IsNullOrWhiteSpace(normalized) ? "该账号当前使用本机代理，无需单独测试。" : error;
            _hintLabel.ForeColor = string.IsNullOrWhiteSpace(normalized) ? TextMuted : Danger;
            return;
        }

        _testButton.Enabled = false;
        _hintLabel.Text = $"正在测试 {editor.Account.DisplayName} 的代理…";
        _hintLabel.ForeColor = Primary;
        try
        {
            var uri = new Uri(normalized);
            if (uri.Scheme is "socks5" or "socks5h")
            {
                await TestSocks5Async(uri);
                _hintLabel.Text = "SOCKS5 隧道已建立，出口地区仍以上游实际结果为准。";
            }
            else
            {
                using var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy(normalized),
                    UseProxy = true,
                    AutomaticDecompression = DecompressionMethods.All
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.codebuff.com/");
                using var response = await client.SendAsync(request);
                _hintLabel.Text = response.IsSuccessStatusCode || (int)response.StatusCode < 500
                    ? $"代理可连接（HTTP {(int)response.StatusCode}），出口地区仍以上游实际结果为准。"
                    : $"代理已连接，但目标返回 HTTP {(int)response.StatusCode}。";
            }
            _hintLabel.ForeColor = Success;
        }
        catch
        {
            _hintLabel.Text = "代理连接失败。请检查地址、账号密码和代理服务状态。";
            _hintLabel.ForeColor = Danger;
        }
        finally
        {
            _testButton.Enabled = _enableCheck.Checked;
        }
    }

    private static async Task TestSocks5Async(Uri proxy)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(proxy.Host, proxy.Port > 0 ? proxy.Port : 1080, timeout.Token);
        await using var stream = client.GetStream();

        var user = Uri.UnescapeDataString(proxy.UserInfo.Split(':', 2).FirstOrDefault() ?? string.Empty);
        var pass = Uri.UnescapeDataString(proxy.UserInfo.Contains(':') ? proxy.UserInfo[(proxy.UserInfo.IndexOf(':') + 1)..] : string.Empty);
        var methods = string.IsNullOrEmpty(proxy.UserInfo) ? new byte[] { 0x00 } : new byte[] { 0x00, 0x02 };
        var greetingRequest = new List<byte> { 0x05, (byte)methods.Length };
        greetingRequest.AddRange(methods);
        await stream.WriteAsync(greetingRequest.ToArray(), timeout.Token);
        var greeting = await ReadExactAsync(stream, 2, timeout.Token);
        if (greeting[0] != 0x05 || greeting[1] == 0xFF) throw new InvalidOperationException("SOCKS5 代理拒绝认证方式。");
        if (greeting[1] == 0x02)
        {
            var userBytes = Encoding.UTF8.GetBytes(user);
            var passBytes = Encoding.UTF8.GetBytes(pass);
            if (userBytes.Length > 255 || passBytes.Length > 255) throw new InvalidOperationException("SOCKS5 用户名或密码过长。");
            var authRequest = new List<byte> { 0x01, (byte)userBytes.Length };
            authRequest.AddRange(userBytes);
            authRequest.Add((byte)passBytes.Length);
            authRequest.AddRange(passBytes);
            await stream.WriteAsync(authRequest.ToArray(), timeout.Token);
            var auth = await ReadExactAsync(stream, 2, timeout.Token);
            if (auth[1] != 0x00) throw new InvalidOperationException("SOCKS5 代理认证失败。");
        }

        var host = Encoding.UTF8.GetBytes("www.codebuff.com");
        var connectRequest = new List<byte> { 0x05, 0x01, 0x00, 0x03, (byte)host.Length };
        connectRequest.AddRange(host);
        connectRequest.AddRange([0x01, 0xBB]);
        await stream.WriteAsync(connectRequest.ToArray(), timeout.Token);
        var response = await ReadExactAsync(stream, 4, timeout.Token);
        if (response[1] != 0x00) throw new InvalidOperationException($"SOCKS5 连接目标失败（代码 {response[1]}）。");
        var addressLength = response[3] switch
        {
            0x01 => 4,
            0x04 => 16,
            0x03 => (await ReadExactAsync(stream, 1, timeout.Token))[0],
            _ => throw new InvalidOperationException("SOCKS5 返回了无效地址类型。")
        };
        await ReadExactAsync(stream, addressLength + 2, timeout.Token);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = await stream.ReadAsync(result.AsMemory(offset, length - offset), cancellationToken);
            if (count == 0) throw new IOException("代理连接提前关闭。");
            offset += count;
        }
        return result;
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return email;
        return email[..Math.Min(2, at)] + "***" + email[at..];
    }

    private static string RedactProxy(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "代理已设置";
        var host = uri.Host.Contains(':', StringComparison.Ordinal) ? $"[{uri.Host}]" : uri.Host;
        var port = uri.Port > 0 && !uri.IsDefaultPort ? $":{uri.Port}" : string.Empty;
        return $"{uri.Scheme}://{(string.IsNullOrEmpty(uri.UserInfo) ? "" : "***@")}{host}{port}";
    }

    private static RoundedPanel NewCard(int x, int y, int width, int height, Color? back = null, Color? border = null, int radius = 14) => new()
    {
        Location = new Point(x, y), Size = new Size(width, height), BackColor = back ?? Card,
        BorderColor = border ?? Border, BorderWidth = 1F, CornerRadius = radius
    };

    private static Label NewLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color) => new()
    {
        Text = text, Location = new Point(x, y), Size = new Size(width, height),
        Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color,
        BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
    };

    private static InputBox NewTextBox(int x, int y, int width, int height) => new()
    {
        Location = new Point(x, y), Size = new Size(width, height), BackColor = Color.White,
        BorderColor = Border, FocusBorderColor = Primary, CornerRadius = 9
    };

    private static ModernButton NewButton(string text, int x, int y, int width, int height, Color back, Color fore, Color border)
    {
        var button = new ModernButton
        {
            Text = text, Location = new Point(x, y), Size = new Size(width, height),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold), CornerRadius = 10, BorderWidth = 1F
        };
        button.SetPalette(back, back == Card ? Soft : Color.FromArgb(29, 78, 216), border, fore);
        return button;
    }

    private sealed record AccountEditor(AccountProxyInfo Account, InputBox Box, RoundedPanel Row);
}
