using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetHog.Models;

namespace NetHog.Avalonia;

public partial class DomainDialog : Window
{
    public DomainDialog() : this(Array.Empty<DomainObservation>())
    {
    }

    public DomainDialog(IEnumerable<DomainObservation> observations)
    {
        InitializeComponent();
        foreach (var observation in observations.OrderByDescending(observation => observation.LastSeen))
        {
            Observations.Add(new DomainRow(observation));
        }
        DataContext = this;
    }

    public ObservableCollection<DomainRow> Observations { get; } = new();
    public string StatusText => Observations.Count == 0 ? "No domain metadata has been observed in this session." : $"{Observations.Count} domain observation(s).";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    public sealed class DomainRow
    {
        public DomainRow(DomainObservation observation)
        {
            Domain = observation.Domain;
            DeviceText = observation.DeviceDisplayName;
            LastSeenText = observation.LastSeen.ToLocalTime().ToString("HH:mm:ss");
            SeenText = $"Seen {observation.SeenCount} time(s)";
            StateText = observation.IsBlocked ? "Blocked" : "Observed";
        }

        public string Domain { get; }
        public string DeviceText { get; }
        public string LastSeenText { get; }
        public string SeenText { get; }
        public string StateText { get; }
    }
}
