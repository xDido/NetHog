using Avalonia.Controls;
using Avalonia.Interactivity;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class UpdateDialog : Window
{
    public UpdateDialog() : this(new AvailableUpdate("0.0.0", "NetHog update", "https://github.com/xDido/NetHog/releases"))
    {
    }

    public UpdateDialog(AvailableUpdate update)
    {
        InitializeComponent();
        Update = update;
        DataContext = this;
    }

    public AvailableUpdate Update { get; }
    public string UpdateName => Update.Name;
    public bool OpenRelease { get; private set; }

    private void Later_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Open_Click(object? sender, RoutedEventArgs e)
    {
        OpenRelease = true;
        Close(true);
    }
}
