using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FreeBuffLauncher;

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
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(options));
        File.Move(temporary, _path, true);
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
