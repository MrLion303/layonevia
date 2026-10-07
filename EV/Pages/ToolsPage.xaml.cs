using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EV.Services;

namespace EV.Pages;

public partial class ToolsPage : UserControl
{
    private readonly CommandEngine _engine = new();

    public ToolsPage()
    {
        InitializeComponent();
    }

    private async void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string app)
            await RunAsync("open_application", new { application = app }, $"Abrir {app}");
    }

    private async void OpenCustomApp_Click(object sender, RoutedEventArgs e)
    {
        var app = ApplicationBox.Text.Trim();
        if (app.Length == 0)
        {
            ResultText.Text = "Escribe el nombre de una aplicación.";
            return;
        }

        await RunAsync("open_application", new { application = app }, $"Abrir {app}");
    }

    private async void CloseCustomApp_Click(object sender, RoutedEventArgs e)
    {
        var app = ApplicationBox.Text.Trim();
        if (app.Length == 0)
        {
            ResultText.Text = "Escribe el nombre de una aplicación.";
            return;
        }

        await RunAsync("close_application", new { application = app }, $"Cerrar {app}");
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string folder)
            await RunAsync("open_folder", new { folder }, $"Abrir {folder}");
    }

    private async void OpenCustomFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            ResultText.Text = "Escribe una ruta o una carpeta conocida.";
            return;
        }

        await RunAsync("open_folder", new { folder }, $"Abrir {folder}");
    }

    private async Task RunAsync(string tool, object arguments, string action)
    {
        try
        {
            ResultText.Text = $"{action}...";
            var json = JsonSerializer.Serialize(arguments);
            var result = await _engine.ExecuteAiToolAsync(tool, json);
            ResultText.Text = result.Message;
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"Error desde herramientas: {action}");
            ResultText.Text = "No se pudo completar la acción.";
        }
    }
}