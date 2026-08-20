using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace FreeBuffLauncher;

internal sealed record LoginRequest(string LoginUrl, string FingerprintId, string FingerprintHash, string ExpiresAt);
internal sealed record LoginResult(string Email, JsonObject User);

internal sealed class LoginService
{
    private const string BaseUrl = "https://www.codebuff.com";
    private readonly CredentialStore _credentialStore;

    public LoginService(CredentialStore credentialStore) => _credentialStore = credentialStore;

    public async Task<LoginRequest> CreateRequestAsync(string proxyUrl, CancellationToken cancellationToken)
    {
        using var client = CreateClient(proxyUrl);
        var fingerprint = "codebuff-cli-" + CreateRandomSuffix();
        using var response = await client.PostAsJsonAsync(
            BaseUrl + "/api/auth/cli/code",
            new { fingerprintId = fingerprint },
            cancellationToken);

        var json = await ParseObjectAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"无法获取登录链接（HTTP {(int)response.StatusCode}）。请检查代理后重试。");

        var loginUrl = json["loginUrl"]?.ToString();
        var fingerprintHash = json["fingerprintHash"]?.ToString();
        var expiresAt = json["expiresAt"]?.ToString();
        if (string.IsNullOrWhiteSpace(loginUrl) || string.IsNullOrWhiteSpace(fingerprintHash) || string.IsNullOrWhiteSpace(expiresAt))
            throw new InvalidOperationException("Freebuff 返回的登录信息不完整，请稍后重试。");

        return new LoginRequest(loginUrl, fingerprint, fingerprintHash, expiresAt);
    }

    public async Task<LoginResult> WaitForLoginAsync(
        LoginRequest request,
        string proxyUrl,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(proxyUrl);
        var deadline = DateTimeOffset.Now.AddMinutes(5);
        var attempt = 0;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            var query = "?fingerprintId=" + Uri.EscapeDataString(request.FingerprintId)
                        + "&fingerprintHash=" + Uri.EscapeDataString(request.FingerprintHash)
                        + "&expiresAt=" + Uri.EscapeDataString(request.ExpiresAt);
            using var response = await client.GetAsync(BaseUrl + "/api/auth/cli/status" + query, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await ParseObjectAsync(response, cancellationToken);
                if (json["user"] is JsonObject user)
                {
                    var token = user["authToken"]?.ToString();
                    if (string.IsNullOrWhiteSpace(token))
                        throw new InvalidOperationException("登录成功，但没有拿到账号凭据。请重新登录。");
                    _credentialStore.SaveUser(user);
                    return new LoginResult(user["email"]?.ToString() ?? "已登录账号", user);
                }
            }
            else if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new InvalidOperationException("登录链接已失效，请重新点击登录。");
            }
            else if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                status($"等待登录中（网络状态 {(int)response.StatusCode}）…");
            }

            status($"浏览器登录完成后会自动继续（已等待 {attempt * 5} 秒）…");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
        throw new TimeoutException("登录等待超时，请重新点击登录。");
    }

    public static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static HttpClient CreateClient(string proxyUrl)
    {
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxyUrl),
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/125.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static async Task<JsonObject> ParseObjectAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string CreateRandomSuffix()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..8];
    }
}
