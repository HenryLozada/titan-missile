using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Titan.Engine.Native;

namespace Titan.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // Anything that escapes goes to %AppData%\TITAN\crash.log, so a blank window is never a mystery.
        UnhandledException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    public static void Log(Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TITAN");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTimeOffset.Now:u}] {ex}\n\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The engine needs an elevated token. Relaunch once through UAC; if the user declines,
        // keep going in read-only mode (memory readouts work, BOOST/purge are disabled).
        string[] argv = Environment.GetCommandLineArgs();
        bool alreadyTried = argv.Contains("--elevated") || argv.Contains("--no-elevate");
        if (!Privileges.IsElevated && !alreadyTried)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--elevated",
                });
                Exit();
                return;
            }
            catch (Win32Exception)
            {
                // UAC declined
            }
        }

        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }
}
