namespace QwenStudio;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            File.WriteAllText(@"C:\Projects\QwenStudio\crash.log", e.ExceptionObject?.ToString() ?? "Unknown exception");
        };
        Application.ThreadException += (s, e) =>
        {
            File.WriteAllText(@"C:\Projects\QwenStudio\crash.log", e.Exception?.ToString() ?? "Unknown thread exception");
        };

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            File.WriteAllText(@"C:\Projects\QwenStudio\crash.log", ex.ToString());
        }
    }
}
