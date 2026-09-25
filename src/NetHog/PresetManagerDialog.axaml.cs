using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetHog.Models;
using NetHog.Services;

namespace NetHog.Avalonia;

public partial class PresetManagerDialog : Window, INotifyPropertyChanged
{
    private readonly ControlPresetStore _store = new();
    private ControlPreset? _selectedPreset;
    private string _presetName = string.Empty;
    private string _presetDescription = string.Empty;
    private string _downloadText = string.Empty;
    private string _uploadText = string.Empty;
    private bool _blockInternet;
    private string _validationText = string.Empty;
    private bool _loading;

    public PresetManagerDialog() : this(ControlPresetCatalog.All)
    {
    }

    public PresetManagerDialog(IEnumerable<ControlPreset> presets)
    {
        InitializeComponent();
        foreach (var preset in presets.Where(preset => !ControlPresetCatalog.IsSystemPreset(preset.Id)))
            Presets.Add(preset);
        DataContext = this;
        if (Presets.Count > 0) SelectedPreset = Presets[0];
        else CreatePreset();
    }

    public ObservableCollection<ControlPreset> Presets { get; } = new();
    public ControlPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (ReferenceEquals(_selectedPreset, value)) return;
            _selectedPreset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDeleteSelected));
            if (!_loading) LoadPreset(value);
        }
    }

    public bool CanDeleteSelected => SelectedPreset is not null && !ControlPresetCatalog.IsBuiltIn(SelectedPreset.Id)
                                     && !ControlPresetCatalog.IsSystemPreset(SelectedPreset.Id);
    public string PresetName { get => _presetName; set => SetField(ref _presetName, value); }
    public string PresetDescription { get => _presetDescription; set => SetField(ref _presetDescription, value); }
    public string DownloadText { get => _downloadText; set => SetField(ref _downloadText, KeepAsciiDigits(value)); }
    public string UploadText { get => _uploadText; set => SetField(ref _uploadText, KeepAsciiDigits(value)); }
    public bool BlockInternet { get => _blockInternet; set => SetField(ref _blockInternet, value); }
    public string ValidationText { get => _validationText; private set => SetField(ref _validationText, value); }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private static void IntegerTextBox_TextInput(object? sender, TextInputEventArgs e)
    {
        e.Handled = string.IsNullOrEmpty(e.Text) || e.Text.Any(character => character is < '0' or > '9');
    }

    private void Preset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading) LoadPreset(SelectedPreset);
    }

    private void NewPreset_Click(object? sender, RoutedEventArgs e) => CreatePreset();

    private void CreatePreset()
    {
        var preset = new ControlPreset(
            ControlPresetCatalog.CreateCustomId(), "New preset", "Custom control profile.", 5, 5, false);
        Presets.Add(preset);
        SelectedPreset = preset;
        ValidationText = "Edit the new preset, then choose Save changes.";
    }

    private void DeletePreset_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedPreset is null || !CanDeleteSelected) return;
        var index = Presets.IndexOf(SelectedPreset);
        Presets.Remove(SelectedPreset);
        SelectedPreset = Presets.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, Presets.Count - 1)));
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryUpdateSelectedPreset()) return;
        try
        {
            _store.Save(Presets);
            Close(true);
        }
        catch (Exception exception)
        {
            ValidationText = $"Could not save presets: {exception.Message}";
        }
    }

    private void LoadPreset(ControlPreset? preset)
    {
        _loading = true;
        try
        {
            PresetName = preset?.Name ?? string.Empty;
            PresetDescription = preset?.Description ?? string.Empty;
            DownloadText = preset?.DownloadLimitMbps?.ToString() ?? string.Empty;
            UploadText = preset?.UploadLimitMbps?.ToString() ?? string.Empty;
            BlockInternet = preset?.BlockInternet == true;
            ValidationText = string.Empty;
        }
        finally { _loading = false; }
    }

    private bool TryUpdateSelectedPreset()
    {
        if (SelectedPreset is null) { ValidationText = "Select a preset or create a new one first."; return false; }
        var name = PresetName.Trim();
        if (name.Length == 0) { ValidationText = "Enter a name for this preset."; return false; }
        if (!TryReadLimit(DownloadText, "Download", out var download)
            || !TryReadLimit(UploadText, "Upload", out var upload)) return false;
        if (!BlockInternet && download is null && upload is null)
        {
            ValidationText = "Set at least one limit or turn on blocking.";
            return false;
        }

        var updated = new ControlPreset(SelectedPreset.Id, name, PresetDescription.Trim(), download, upload, BlockInternet);
        var index = Presets.IndexOf(SelectedPreset);
        if (index >= 0) Presets[index] = updated;
        SelectedPreset = updated;
        return true;
    }

    private bool TryReadLimit(string text, string direction, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), out var parsed) || parsed is < 1 or > 10_000)
        {
            ValidationText = $"{direction} must be a whole number from 1 to 10,000 Mbps, or left blank.";
            return false;
        }
        value = parsed;
        return true;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string KeepAsciiDigits(string? value) =>
        new((value ?? string.Empty).Where(character => character is >= '0' and <= '9').ToArray());
}
