using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Reclaim;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            ReportCrash((a.ExceptionObject as Exception)!, "fatal", true);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            ReportCrash(a.Exception, "background task", false);
            a.SetObserved();
        };
    }

    private void OnDispatcherCrash(object s, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception, "UI thread", true);
        // e.Handled stays false — the process still terminates, but logged first
    }

    private static void ReportCrash(Exception ex, string where, bool fatal)
    {
        string log;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Reclaim");
            Directory.CreateDirectory(dir);
            log = Path.Combine(dir, "crash.log");
            File.AppendAllText(log,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where}) ===\n{ex}\n\n");
        }
        catch { return; }

        if (!fatal) return;
        try
        {
            System.Windows.MessageBox.Show(
                $"Reclaim hit an unexpected error ({where}) and has to close.\n\n" +
                $"{ex.Message}\n\nDetails written to:\n{log}",
                "Reclaim — error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }
}
