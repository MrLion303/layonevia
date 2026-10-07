using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using EV.Services;

namespace EV.Pages;

public partial class RoutinesPage : UserControl
{
    private readonly RoutineStore _store = new();
    private readonly CommandEngine _engine = new();
    private List<EvRoutine> _routines = [];

    public RoutinesPage()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        _routines = _store.Load().ToList();
        RoutineList.ItemsSource = _routines;
        RoutineList.DisplayMemberPath = nameof(EvRoutine.Name);

        if (_routines.Count == 0)
        {
            RoutineName.Text = "Rutinas";
            RoutineDescription.Text = "Todavía no tienes rutinas guardadas. Puedes pedirle a EV que aprenda una secuencia de acciones.";
            StepList.ItemsSource = null;
            return;
        }

        RoutineList.SelectedIndex = 0;
    }

    private void RoutineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RoutineList.SelectedItem is not EvRoutine routine)
            return;

        RoutineName.Text = routine.Name;
        RoutineDescription.Text = string.IsNullOrWhiteSpace(routine.Description)
            ? "Sin descripción."
            : routine.Description;

        StepList.ItemsSource = routine.Steps
            .Select((step, index) => $"{index + 1}. {step.ToolName}")
            .ToList();

        StatusText.Text = $"{routine.Steps.Count} pasos.";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (RoutineList.SelectedItem is not EvRoutine routine)
        {
            StatusText.Text = "Selecciona una rutina.";
            return;
        }

        try
        {
            StatusText.Text = $"Ejecutando «{routine.Name}»...";
            var result = await _engine.ExecuteAiToolAsync(
                "run_routine",
                JsonSerializer.Serialize(new { name = routine.Name }));

            StatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo ejecutar una rutina desde la interfaz");
            StatusText.Text = "No se pudo ejecutar la rutina.";
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RoutineList.SelectedItem is not EvRoutine routine)
        {
            StatusText.Text = "Selecciona una rutina.";
            return;
        }

        var confirm = MessageBox.Show(
            $"¿Eliminar la rutina «{routine.Name}»?",
            "EV",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var result = await _engine.ExecuteAiToolAsync(
                "delete_routine",
                JsonSerializer.Serialize(new { name = routine.Name }));

            StatusText.Text = result.Message;
            Refresh();
        }
        catch (Exception ex)
        {
            App.LogException(ex, "No se pudo eliminar una rutina desde la interfaz");
            StatusText.Text = "No se pudo eliminar la rutina.";
        }
    }
}