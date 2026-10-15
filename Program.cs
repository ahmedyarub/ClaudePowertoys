namespace ClaudePowertoys;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Lifeline.Begin(args);
        // A crash otherwise only reaches the Windows event log; put it where everything else is logged.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"Crashed: {e.ExceptionObject}");
            Lifeline.RestartAfterCrash();
        };
        Application.ThreadException += (_, e) => Log.Write($"Error on the UI thread: {e.Exception}");
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            Log.Write($"Crashed: {ex}");
            Lifeline.RestartAfterCrash();
            throw;
        }
    }
}
