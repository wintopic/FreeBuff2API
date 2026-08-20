namespace FreeBuffLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, @"Local\FreeBuffDesktopAssistant", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("FreeBuff 桌面助手已经打开了。", "FreeBuff 桌面助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, args) =>
            MessageBox.Show("程序遇到问题：" + args.Exception.Message, "FreeBuff 桌面助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Application.Run(new MainForm());
    }
}
