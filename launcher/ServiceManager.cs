using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace FreeBuffLauncher;

internal sealed record HealthInfo(bool Running, string Status, int Accounts, string Version)
{
    public static HealthInfo Stopped { get; } = new(false, "stopped", 0, string.Empty);
}

internal sealed class ServiceManager
{
    private readonly AppPaths _paths;
    private EnvSettings _settings;

    public ServiceManager(AppPaths paths, EnvSettings settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public void UpdateSettings(EnvSettings settings) => _settings = settings;

    public async Task<HealthInfo> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var dto = await client.GetFromJsonAsync<HealthDto>(_settings.HealthUrl, cancellationToken);
            return dto is null
                ? HealthInfo.Stopped
                : new HealthInfo(true, dto.Status ?? "running", dto.Accounts, dto.Version ?? string.Empty);
        }
        catch
        {
            return HealthInfo.Stopped;
        }
    }

    public async Task<bool> IsProxyReachableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(_settings.ProxyUrl);
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if ((await GetHealthAsync(cancellationToken)).Running)
        {
            AdoptCurrentProcess();
            return;
        }
        if (!await IsProxyReachableAsync(cancellationToken))
            throw new InvalidOperationException("没有检测到本机代理。请先打开代理软件，再点击启动。");
        if (!File.Exists(_paths.NodePath))
            throw new FileNotFoundException("程序运行组件不完整，缺少 runtime\\node.exe。", _paths.NodePath);
        if (!File.Exists(_paths.ServerPath))
            throw new FileNotFoundException("程序运行组件不完整，缺少 runtime\\server.js。", _paths.ServerPath);
        if (await IsPortOccupiedAsync(_settings.Host, _settings.Port, cancellationToken))
            throw new InvalidOperationException($"端口 {_settings.Port} 已被其他程序占用，请关闭占用程序后重试。");

        _settings.Save(_paths.EnvPath);
        Directory.CreateDirectory(_paths.LogDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = _paths.NodePath,
            WorkingDirectory = _paths.RuntimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--use-env-proxy");
        startInfo.ArgumentList.Add("--env-file=" + _paths.EnvPath);
        startInfo.ArgumentList.Add("server.js");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动本地服务。");
        File.WriteAllText(_paths.PidPath, process.Id.ToString());

        var deadline = DateTimeOffset.Now.AddSeconds(15);
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(300, cancellationToken);
            process.Refresh();
            if (process.HasExited)
                throw new InvalidOperationException("本地服务启动失败。请确认程序文件完整后重试。");
            if ((await GetHealthAsync(cancellationToken)).Running) return;
        }

        TryKill(process.Id);
        SafeDeletePid();
        throw new TimeoutException("本地服务启动超时，请检查代理或端口设置。");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetHealthAsync(cancellationToken);
        if (!health.Running)
        {
            SafeDeletePid();
            return;
        }

        var processId = ReadPid() ?? PortOwner.FindListeningProcessId(_settings.Port);
        if (processId is null)
            throw new InvalidOperationException("检测到服务正在运行，但无法识别进程。请重启电脑后再试。");

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("端口由其他程序占用，为保护数据没有强制关闭它。");
            process.Kill(true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (ArgumentException)
        {
        }
        SafeDeletePid();

        var deadline = DateTimeOffset.Now.AddSeconds(5);
        while (DateTimeOffset.Now < deadline)
        {
            if (!(await GetHealthAsync(cancellationToken)).Running) return;
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException("服务没有及时停止，请稍后再试。");
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if ((await GetHealthAsync(cancellationToken)).Running) await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public void AdoptCurrentProcess()
    {
        var processId = PortOwner.FindListeningProcessId(_settings.Port);
        if (processId is not null)
        {
            Directory.CreateDirectory(_paths.LogDirectory);
            File.WriteAllText(_paths.PidPath, processId.Value.ToString());
        }
    }

    private static async Task<bool> IsPortOccupiedAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private int? ReadPid()
    {
        try
        {
            return File.Exists(_paths.PidPath) && int.TryParse(File.ReadAllText(_paths.PidPath).Trim(), out var value)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void SafeDeletePid()
    {
        try
        {
            if (File.Exists(_paths.PidPath)) File.Delete(_paths.PidPath);
        }
        catch
        {
        }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(true);
        }
        catch
        {
        }
    }

    private sealed class HealthDto
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("accounts")] public int Accounts { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
    }
}
