using System.Windows;
using EV.Pages;

namespace EV;

public partial class MainWindow : Window
{
    private HomePage? _home;
    private AudioPage? _audio;
    private MemoryPage? _memory;
    private SettingsPage? _settings;

    public MainWindow()
    {
        InitializeComponent();
        ShowPage("Inicio", GetHome());
    }

    private HomePage GetHome() => _home ??= new HomePage();
    private AudioPage GetAudio() => _audio ??= new AudioPage();
    private MemoryPage GetMemory() => _memory ??= new MemoryPage();
    private SettingsPage GetSettings() => _settings ??= new SettingsPage();

    private void ShowPage(string title, object page)
    {
        try
        {
            PageTitle.Text = title;
            PageContent.Content = page;
        }
        catch (Exception ex)
        {
            App.LogException(ex, $"No se pudo abrir la sección {title}");
            MessageBox.Show(
                $"No se pudo abrir esta sección. EV seguirá funcionando.\n\n{ex.Message}",
                "EV",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowPage("Inicio", GetHome());
    private void Audio_Click(object sender, RoutedEventArgs e) => ShowPage("Audio", GetAudio());
    private void Memory_Click(object sender, RoutedEventArgs e) => ShowPage("Memoria", GetMemory());
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage("Configuración", GetSettings());

    protected override void OnClosed(EventArgs e)
    {
        _audio?.Dispose();
        base.OnClosed(e);
    }
}
