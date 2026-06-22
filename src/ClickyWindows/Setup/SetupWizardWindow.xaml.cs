using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using ClickyWindows.AI;

namespace ClickyWindows.Setup;

public partial class SetupWizardWindow : Window
{
    private readonly bool _prePopulate;
    private readonly string _provider;

    public SetupWizardWindow(bool prePopulate = false, string provider = "Gemini")
    {
        _prePopulate = prePopulate;
        _provider = provider;
        InitializeComponent();

        if (_prePopulate)
        {
            Loaded += OnLoaded;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        GeminiKeyBox.Password = CredentialStore.GetKey(CredentialStore.GeminiTarget) ?? "";
        OpenAiKeyBox.Password = CredentialStore.GetKey(CredentialStore.OpenAiTarget) ?? "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var geminiKey = GeminiKeyBox.Password.Trim();
        var openAiKey = OpenAiKeyBox.Password.Trim();

        // Require the key for the active provider; the other is optional (lets users pre-fill both).
        var isOpenAi = AiProviderFactory.IsOpenAi(_provider);
        if (isOpenAi && string.IsNullOrEmpty(openAiKey))
        {
            ErrorText.Text = "OpenAI API key is required for the selected provider.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (!isOpenAi && string.IsNullOrEmpty(geminiKey))
        {
            ErrorText.Text = "Gemini API key is required for the selected provider.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(geminiKey))
                CredentialStore.SaveKey(CredentialStore.GeminiTarget, geminiKey);
            if (!string.IsNullOrEmpty(openAiKey))
                CredentialStore.SaveKey(CredentialStore.OpenAiTarget, openAiKey);
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
