using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace EV;

public partial class App : Application
{
    private static int _handlingException;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        base.OnStartup(e);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogException(ex, "Error al iniciar EV");
            MessageBox.Show(
                $"EV no pudo iniciar correctamente.\n\n{ex.Message}",
                "EV",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception, "Excepción no controlada en la interfaz");

        if (Interlocked.Exchange(ref _handlingException, 1) == 0)
        {
            MessageBox.Show(
                $"EV encontró un error y evitó que la aplicación se cerrara.\n\n{e.Exception.Message}",
                "EV",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            Interlocked.Exchange(ref _handlingException, 0);
        }

        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException(e.Exception, "Excepción no observada en una tarea");
        e.SetObserved();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException(ex, "Excepción no controlada del proceso");
    }

    public static void LogException(Exception exception, string context)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EV");

            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "ev.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
