using System.Collections.ObjectModel;
using System.Windows;
using NetHog.Models;
using NetHog.Services;

namespace NetHog;

public partial class PresetManagerWindow : Window
{
    private readonly ControlPresetStore _store = new();
    private readonly ObservableCollection<ControlPreset> _presets = new();
    private bool _loading;

    public PresetManagerWindow(IEnumerable<ControlPreset> presets)
    {
        InitializeComponent();
        foreach (var preset in presets.Where(preset => !ControlPresetCatalog.IsSystemPreset(preset.Id)))
        {
            _presets.Add(preset);
        }

        PresetsListBox.ItemsSource = _presets;
        if (_presets.Count > 0)
        {
            PresetsListBox.SelectedIndex = 0;
        }
        else
        {
            CreatePreset();
        }
    }

    private void PresetsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadPreset(PresetsListBox.SelectedItem as ControlPreset);
    }

    private void NewPresetButton_Click(object sender, RoutedEventArgs e) => CreatePreset();

    private void CreatePreset()
    {
        var preset = new ControlPreset(
            ControlPresetCatalog.CreateCustomId(),
            "New preset",
            "Custom control profile.",
            5,
            5,
            false);
        _presets.Add(preset);
        PresetsListBox.SelectedItem = preset;
        PresetValidationText.Text = "Edit the new preset, then choose Save changes.";
        PresetValidationText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedBrush");
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetsListBox.SelectedItem is not ControlPreset preset
            || ControlPresetCatalog.IsBuiltIn(preset.Id)) return;

        _presets.Remove(preset);
        PresetsListBox.SelectedIndex = Math.Clamp(PresetsListBox.SelectedIndex, 0, _presets.Count - 1);
    }

    private void SaveChangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryUpdateSelectedPreset()) return;

        try
        {
            _store.Save(_presets);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            PresetValidationText.Text = $"Could not save presets: {exception.Message}";
            PresetValidationText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
        }
    }

    private void LoadPreset(ControlPreset? preset)
    {
        _loading = true;
        try
        {
            PresetNameTextBox.Text = preset?.Name ?? string.Empty;
            PresetDescriptionTextBox.Text = preset?.Description ?? string.Empty;
            PresetDownloadTextBox.Text = preset?.DownloadLimitMbps?.ToString() ?? string.Empty;
            PresetUploadTextBox.Text = preset?.UploadLimitMbps?.ToString() ?? string.Empty;
            PresetBlockCheckBox.IsChecked = preset?.BlockInternet == true;
            DeletePresetButton.IsEnabled = preset is not null && !ControlPresetCatalog.IsBuiltIn(preset.Id);
            PresetValidationText.Text = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    private bool TryUpdateSelectedPreset()
    {
        if (PresetsListBox.SelectedItem is not ControlPreset selected)
        {
            ShowValidation("Select a preset or create a new one first.");
            return false;
        }

        var name = PresetNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation("Enter a name for this preset.");
            return false;
        }

        if (!TryReadLimit(PresetDownloadTextBox.Text, "Download", out var download)
            || !TryReadLimit(PresetUploadTextBox.Text, "Upload", out var upload)) return false;

        var block = PresetBlockCheckBox.IsChecked == true;
        if (!block && download is null && upload is null)
        {
            ShowValidation("Set at least one limit or turn on blocking.");
            return false;
        }

        var updated = new ControlPreset(
            selected.Id,
            name,
            PresetDescriptionTextBox.Text.Trim(),
            download,
            upload,
            block);
        var index = _presets.IndexOf(selected);
        _presets[index] = updated;
        PresetsListBox.SelectedItem = updated;
        return true;
    }

    private bool TryReadLimit(string text, string direction, out int? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Trim(), out var parsed) || parsed is < 1 or > 10_000)
        {
            ShowValidation($"{direction} must be a whole number from 1 to 10,000 Mbps, or left blank.");
            return false;
        }

        value = parsed;
        return true;
    }

    private void ShowValidation(string message)
    {
        PresetValidationText.Text = message;
        PresetValidationText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
    }
}
