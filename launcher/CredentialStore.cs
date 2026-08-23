using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeBuffLauncher;

internal sealed record AccountProxyInfo(string Key, string DisplayName, string Email, string ProxyUrl);

internal sealed class CredentialStore
{
    private readonly string _path;

    public CredentialStore(string path) => _path = path;

    public int AccountCount
    {
        get
        {
            try
            {
                var root = ReadRoot();
                if (root["accounts"] is JsonObject accounts) return accounts.Count;
                if (root["default"] is JsonObject || !string.IsNullOrWhiteSpace(root["authToken"]?.ToString())) return 1;
            }
            catch
            {
            }
            return 0;
        }
    }

    public string AccountDescription
    {
        get
        {
            try
            {
                var root = ReadRoot();
                var emails = new List<string>();
                if (root["accounts"] is JsonObject accounts)
                {
                    foreach (var item in accounts)
                    {
                        var email = item.Value?["email"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(email)) emails.Add(MaskEmail(email));
                    }
                }
                else if (root["default"] is JsonObject single)
                {
                    var email = single["email"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(email)) emails.Add(MaskEmail(email));
                }
                return emails.Count == 0 ? string.Empty : string.Join("、", emails.Take(2));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public void SaveUser(JsonObject user)
    {
        var token = user["authToken"]?.ToString();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("登录结果中没有 authToken。请重新登录。");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var root = ReadRoot();
        var accounts = root["accounts"] as JsonObject;
        if (accounts is null)
        {
            accounts = new JsonObject();
            if (root["default"] is JsonObject oldDefault)
            {
                accounts[GetAccountKey(oldDefault)] = oldDefault.DeepClone();
            }
            root = new JsonObject { ["accounts"] = accounts };
        }

        accounts[GetAccountKey(user)] = user.DeepClone();
        WriteAtomically(root);
    }

    public IReadOnlyList<AccountProxyInfo> GetAccounts()
    {
        var result = new List<AccountProxyInfo>();
        try
        {
            var root = ReadRoot();
            if (root["accounts"] is JsonObject accounts)
            {
                foreach (var item in accounts)
                {
                    if (item.Value is not JsonObject account) continue;
                    result.Add(ToAccountProxyInfo(item.Key, account));
                }
            }
            else if (root["default"] is JsonObject single)
            {
                result.Add(ToAccountProxyInfo("default", single));
            }
            else if (!string.IsNullOrWhiteSpace(root["authToken"]?.ToString()))
            {
                result.Add(ToAccountProxyInfo("default", root));
            }
        }
        catch
        {
        }
        return result;
    }

    public void SaveAccountProxies(IReadOnlyDictionary<string, string> proxies)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var root = ReadRoot();
        if (root["accounts"] is JsonObject accounts)
        {
            foreach (var item in proxies)
            {
                if (accounts[item.Key] is not JsonObject account) continue;
                SetProxy(account, item.Value);
            }
        }
        else if (root["default"] is JsonObject single && proxies.TryGetValue("default", out var proxy))
        {
            SetProxy(single, proxy);
        }
        else if (!string.IsNullOrWhiteSpace(root["authToken"]?.ToString()) && proxies.TryGetValue("default", out proxy))
        {
            SetProxy(root, proxy);
        }
        WriteAtomically(root);
    }

    private JsonObject ReadRoot()
    {
        if (!File.Exists(_path)) return new JsonObject();
        var text = File.ReadAllText(_path);
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private void WriteAtomically(JsonObject root)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = _path + ".bak";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(root.ToJsonString(options));
                writer.WriteLine();
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(_path)) File.Copy(_path, backup, true);
            File.Move(temporary, _path, true);
            TryRestrictFileAccess(_path);
            if (File.Exists(backup)) TryRestrictFileAccess(backup);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
            }
        }
    }

    private static AccountProxyInfo ToAccountProxyInfo(string key, JsonObject account)
    {
        var email = account["email"]?.ToString() ?? string.Empty;
        var name = account["name"]?.ToString();
        var displayName = !string.IsNullOrWhiteSpace(name)
            ? name
            : !string.IsNullOrWhiteSpace(email) ? MaskEmail(email) : "已登录账号";
        return new AccountProxyInfo(key, displayName, email, account["proxy"]?.ToString() ?? string.Empty);
    }

    private static void SetProxy(JsonObject account, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) account.Remove("proxy");
        else account["proxy"] = value;
    }

    private static void TryRestrictFileAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return;
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                currentUser,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch
        {
            // Some removable/network filesystems do not support ACLs. The
            // atomic write still succeeds; the user receives no credential
            // content in logs or UI errors.
        }
    }

    private static string GetAccountKey(JsonObject user)
    {
        var id = user["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(id)) return id;
        var email = user["email"]?.ToString();
        if (!string.IsNullOrWhiteSpace(email)) return email;
        var token = user["authToken"]?.ToString() ?? string.Empty;
        return token.Length > 12 ? "token-" + token[..12] : "default";
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return email;
        var visible = Math.Min(2, at);
        return email[..visible] + "***" + email[at..];
    }
}
