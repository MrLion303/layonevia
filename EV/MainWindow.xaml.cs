using System.Windows;
using EV.Pages;

namespace EV;

public partial class MainWindow : Window
{
    private readonly HomePage _home = new();
    private readonly AudioPage _audio = new();
    private readonly MemoryPage _memory = new();
    private readonly SettingsPage _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        ShowPage("Inicio", _home);
    }

    private void ShowPage(string title, object page)
    {
        PageTitle.Text = title;
        PageContent.Content = page;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowPage("Inicio", _home);
    private void Audio_Click(object sender, RoutedEventArgs e) => ShowPage("Audio", _audio);
    private void Memory_Click(object sender, RoutedEventArgs e) => ShowPage("Memoria", _memory);
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowPage("Configuración", _settings);
}