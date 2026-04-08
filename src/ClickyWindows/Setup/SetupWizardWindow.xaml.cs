using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ClickyWindows.AI;

namespace ClickyWindows.Setup;

public partial class SetupWizardWindow : Window
{
    private readonly bool _prePopulate;

    public SetupWizardWindow(bool prePopulate = false)
    {
        _prePopulate = prePopulate;
        InitializeComponent();

        if (_prePopulate)
        {
            Loaded += OnLoaded;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        GeminiKeyBox.Password = CredentialStore.GetKey(CredentialStore.GeminiTarget) ?? "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var geminiKey = GeminiKeyBox.Password.Trim();

        if (string.IsNullOrEmpty(geminiKey))
        {
            ErrorText.Text = "Gemini API key is required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            CredentialStore.SaveKey(CredentialStore.GeminiTarget, geminiKey);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Failed to save key: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!_prePopulate)
        {
            // First-run: app cannot work without keys — exit
            System.Windows.Application.Current.Shutdown(0);
        }
        else
        {
            // Key rotation from tray: just close
            Close();
        }
    }

    private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
