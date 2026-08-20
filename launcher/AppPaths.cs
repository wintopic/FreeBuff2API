namespace FreeBuffLauncher;

internal sealed class AppPaths
{
    public string AppDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public string RuntimeDirectory { get; }
    public string NodePath { get; }
    public string ServerPath { get; }
    public string EnvPath { get; }
    public string CredentialDirectory { get; }
    public string CredentialPath { get; }
    public string LogDirectory { get; }
    public string PidPath { get; }

    public AppPaths()
    {
        RuntimeDirectory = Path.Combine(AppDirectory, "runtime");
        NodePath = Path.Combine(RuntimeDirectory, "node.exe");
        ServerPath = Path.Combine(RuntimeDirectory, "server.js");
        EnvPath = Path.Combine(RuntimeDirectory, ".env");
        CredentialDirectory = Path.Combine(RuntimeDirectory, "credentials");
        CredentialPath = Path.Combine(CredentialDirectory, "freebuff_credentials.json");
        LogDirectory = Path.Combine(RuntimeDirectory, "logs");
        PidPath = Path.Combine(LogDirectory, "freebuff2api.pid");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(CredentialDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    public void ImportExistingDeploymentIfAvailable()
    {
        EnsureDirectories();
        foreach (var root in EnumerateAncestorDirectories())
        {
            if (!File.Exists(EnvPath))
            {
                var oldEnv = Path.Combine(root, ".env");
                if (File.Exists(oldEnv) && File.Exists(Path.Combine(root, "worker.js")))
                {
                    File.Copy(oldEnv, EnvPath, false);
                }
            }

            if (!File.Exists(CredentialPath))
            {
                var candidates = new[]
                {
                    Path.Combine(root, "freebuff_tools", "freebuff_credentials.json"),
                    Path.Combine(root, "credentials", "freebuff_credentials.json")
                };
                var existing = candidates.FirstOrDefault(File.Exists);
                if (existing is not null)
                {
                    File.Copy(existing, CredentialPath, false);
                }
            }

            if (File.Exists(EnvPath) && File.Exists(CredentialPath))
            {
                break;
            }
        }
    }

    private IEnumerable<string> EnumerateAncestorDirectories()
    {
        var current = new DirectoryInfo(AppDirectory);
        for (var i = 0; current is not null && i < 6; i++, current = current.Parent)
        {
            yield return current.FullName;
        }
    }
}
