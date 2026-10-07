using System.Windows;
using System.Windows.Controls;
using EV.Services;

namespace EV.Pages;

public partial class MemoryPage : UserControl
{
    private readonly MemorySyncService _sync = new();
    private readonly GitHubAuthService _auth = new();
    private readonly SecureAccountStore _accounts = new();

    public MemoryPage()
    {
        InitializeComponent();
        RefreshAccountStatus();
    }

    private void RefreshAccountStatus()
    {
        var account = _accounts.Load();

        if (account is null)
        {
            AccountStatus.Text = "Sin vincular. Las memorias locales se conservarán hasta que conectes una cuenta.";
            LinkButton.Content = "Vincular GitHub";
            return;
        }

        AccountStatus.Text = $"Conectado como @{account.Login} · {account.Repository}";
        LinkButton.Content = "Cambiar cuenta";
    }

    private async void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        LinkButton.IsEnabled = false;

        try
        {
            var login = await _auth.StartDeviceLoginAsync();

            if (login is null)
            {
                MessageBox.Show(
                    "Primero hay que configurar el Client ID de la aplicación OAuth de EV en EV/Services/GitHubOAuthSettings.cs.",
                    "EV",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _auth.OpenVerificationPage(login.VerificationUri);

            MessageBox.Show(
                $"Se abrió GitHub en el navegador. Introduce este código:\n\n{login.UserCode}\n\nDespués vuelve a EV; la aplicación seguirá esperando la autorización.",
                "Vincular GitHub",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            var account = await _auth.CompleteDeviceLoginAsync(login, "MrLion303/layonevia-memory");

            if (account is null)
            {
                MessageBox.Show("No se pudo completar la vinculación con GitHub.", "EV", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _accounts.Save(account);
            RefreshAccountStatus();

            var result = await _sync.SyncAsync();
            MessageBox.Show(result.Message, "Memoria de EV", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo vincular la cuenta.\n\n{ex.Message}", "EV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LinkButton.IsEnabled = true;
        }
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _sync.SyncAsync();
            var icon = result.State == MemorySyncState.Success
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning;

            MessageBox.Show(result.Message, "Memoria de EV", MessageBoxButton.OK, icon);
            RefreshAccountStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"La sincronización falló.\n\n{ex.Message}", "Memoria de EV", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}