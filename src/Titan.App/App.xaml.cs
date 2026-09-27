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

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
