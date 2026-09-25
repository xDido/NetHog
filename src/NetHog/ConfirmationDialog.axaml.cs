using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetHog.Avalonia;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
        : this("Confirm action", "Are you sure you want to continue?", "Continue")
    {
    }

    public ConfirmationDialog(string title, string message, string confirmText)
    {
        InitializeComponent();
        DialogTitle = title;
        Message = message;
        ConfirmText = confirmText;
        DataContext = this;
    }

    public string DialogTitle { get; }
    public string Message { get; }
    public string ConfirmText { get; }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
