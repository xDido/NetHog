using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NetHog.Models;

namespace NetHog.Avalonia;

public partial class ControlDialog : Window, INotifyPropertyChanged
{
    private ControlPreset _selectedPreset;
    private string _downloadText = string.Empty;
    private string _uploadText = string.Empty;
    private bool _blockInternet;
    private string _validationText = string.Empty;

    public ControlDialog() : this("Selected device")
    {
    }

    public ControlDialog(string targetDescription)
    {
        InitializeComponent();
        TargetDescription = targetDescription;
        Presets = new ObservableCollection<ControlPreset>(ControlPresetCatalog.All);
        _selectedPreset = Presets[0];
        DataContext = this;
    }

    public string TargetDescription { get; }
    public ObservableCollection<ControlPreset> Presets { get; }

    public ControlPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (ReferenceEquals(_selectedPreset, value)) return;
            _selectedPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetDescription));
        }
    }

    public string PresetDescription => SelectedPreset.Description;

    public string DownloadText
    {
        get => _downloadText;
        set => SetField(ref _downloadText, value);
    }

    public string UploadText
    {
        get => _uploadText;
        set => SetField(ref _uploadText, value);
    }

    public bool BlockInternet
    {
        get => _blockInternet;
        set => SetField(ref _blockInternet, value);
    }

    public string ValidationText
    {
        get => _validationText;
        private set => SetField(ref _validationText, value);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void Preset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedPreset.Id.Equals("custom", StringComparison.OrdinalIgnoreCase)) return;
        DownloadText = SelectedPreset.DownloadLimitMbps?.ToString() ?? string.Empty;
        UploadText = SelectedPreset.UploadLimitMbps?.ToString() ?? string.Empty;
        BlockInternet = SelectedPreset.BlockInternet;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        ValidationText = string.Empty;
        if (!TryParseLimit(DownloadText, out var download) || !TryParseLimit(UploadText, out var upload))
        {
            ValidationText = "Limits must be whole numbers from 1 to 10,000 Mbps.";
            return;
        }
        if (!BlockInternet && download is null && upload is null)
        {
            ValidationText = "Set at least one limit or enable the IPv4 block.";
            return;
        }
        Close(new DeviceControlRule(string.Empty, download, upload, BlockInternet, 30, SelectedPreset.Id));
    }

    private static bool TryParseLimit(string text, out int? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }
        if (int.TryParse(text, out var parsed) && parsed is >= 1 and <= 10_000)
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
