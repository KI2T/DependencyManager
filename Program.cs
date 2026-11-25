using DependencyManager.UI;

namespace DependencyManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Dependency Manager could not start.\r\n\r\n{ex.Message}\r\n\r\nThe application stores its cache in the Data folder beside the executable. Check that this folder is writable.",
                "Dependency Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
