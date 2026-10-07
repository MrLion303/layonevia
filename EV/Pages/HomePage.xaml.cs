using System.Windows.Controls;

namespace EV.Pages;

public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    public void SetSpeechLevel(double level)
    {
        SpeechWave.SetLevel(level);
        SpeechStatus.Text = level > 0.025 ? "EV está hablando..." : "En espera";
    }
}