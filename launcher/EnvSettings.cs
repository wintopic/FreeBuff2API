using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FreeBuffLauncher;

internal sealed class EnvSettings
{
    public int Port { get; set; } = 8877;
    public string Host { get; set; } = "127.0.0.1";
    public string ApiKey { get; set; } = GenerateApiKey();
    public string ProxyUrl { get; set; } = "http://127.0.0.1:3067";
    public bool PerAccountProxyEnabled { get; set; }
    public bool ManagementApiEnabled { get; set; }
    public string ManagementToken { get; set; } = string.Empty;

    public string BaseUrl => $"http://{Host}:{Port}/v1";
    public string HealthUrl => $"http://{Host}:{Port}/healthz";

    public static EnvSettings LoadOrCreate(string path)
    {
        var settings = new EnvSettings();
        if (File.Exists(path))
        {
            var values = Parse(path);
            if (int.TryParse(Get(values, "PORT"), out var port) && port is > 0 and <= 65535)
                settings.Port = port;
            settings.Host = Get(values, "HOST") ?? settings.Host;
            settings.ApiKey = Get(values, "FREEBUFF_API_KEY") ?? settings.ApiKey;
            settings.ProxyUrl = NormalizeProxy(
                Get(values, "HTTPS_PROXY") ?? Get(values, "HTTP_PROXY") ?? settings.ProxyUrl);
            settings.PerAccountProxyEnabled = string.Equals(
                Get(values, "FREE_PROXY_ACCOUNTS"), "1", StringComparison.OrdinalIgnoreCase);
            settings.ManagementApiEnabled = string.Equals(
                Get(values, "FREEBUFF_MANAGEMENT_API"), "1", StringComparison.OrdinalIgnoreCase);
            settings.ManagementToken = Get(values, "FREEBUFF_MANAGEMENT_TOKEN") ?? string.Empty;
        }
        else
        {
            settings.ProxyUrl = DetectSystemProxy() ?? settings.ProxyUrl;
        }

        settings.Save(path);
        return settings;
    }

    public void Save(string path)
    {
        ProxyUrl = NormalizeProxy(ProxyUrl);
        var lines = new[]
        {
            $"PORT={Port}",
            $"HOST={Host}",
            $"FREEBUFF_API_KEY={ApiKey}",
            "FREEBUFF_DEBUG=false",
            "CODEBUFF_API=",
            "RELAY_KEY=",
            $"HTTP_PROXY={ProxyUrl}",
            $"HTTPS_PROXY={ProxyUrl}",
            $"ALL_PROXY={ProxyUrl}",
            $"NO_PROXY={Host},127.0.0.1,localhost",
            "NODE_USE_ENV_PROXY=1",
            $"FREE_PROXY_ACCOUNTS={(PerAccountProxyEnabled ? "1" : "0")}",
            $"FREEBUFF_MANAGEMENT_API={(ManagementApiEnabled ? "1" : "0")}",
            $"FREEBUFF_MANAGEMENT_TOKEN={ManagementToken}",
            string.Empty
        };
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    public static string NormalizeProxy(string value)
    {
        var proxy = (value ?? string.Empty).Trim();
        if (proxy.Length == 0) return string.Empty;
        if (!proxy.Contains("://", StringComparison.Ordinal) && proxy.Contains(';'))
        {
            var parts = proxy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            proxy = parts.FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                    ?? parts.FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                    ?? parts[0];
        }
        if (!proxy.Contains("://", StringComparison.Ordinal))
        {
            var equalsIndex = proxy.IndexOf('=');
            if (equalsIndex >= 0) proxy = proxy[(equalsIndex + 1)..];
        }
        if (!proxy.Contains("://", StringComparison.Ordinal)) proxy = "http://" + proxy;
        return proxy.TrimEnd('/');
    }

    public static bool TryNormalizeAccountProxy(string value, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        try
        {
            normalized = NormalizeProxy(value);
            if (normalized.Length == 0) return true;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "代理地址格式不正确。";
                return false;
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme is not ("http" or "https" or "socks5" or "socks5h"))
            {
                error = "仅支持 http、https、socks5 和 socks5h 代理。";
                return false;
            }
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                error = "代理地址不能包含查询参数或片段。";
                return false;
            }
            if (uri.Port is 0 or < 0 or > 65535)
            {
                // Uri.Port == -1 means the scheme default; that is valid.
                if (uri.Port != -1)
                {
                    error = "代理端口不正确。";
                    return false;
                }
            }
            return true;
        }
        catch
        {
            normalized = string.Empty;
            error = "代理地址格式不正确。";
            return false;
        }
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            values[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return values;
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? DetectSystemProxy()
    {
        var env = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
        if (!string.IsNullOrWhiteSpace(env)) return NormalizeProxy(env);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = Convert.ToInt32(key?.GetValue("ProxyEnable", 0)) == 1;
            var server = key?.GetValue("ProxyServer")?.ToString();
            if (enabled && !string.IsNullOrWhiteSpace(server)) return NormalizeProxy(server);
        }
        catch
        {
        }
        return null;
    }
}
