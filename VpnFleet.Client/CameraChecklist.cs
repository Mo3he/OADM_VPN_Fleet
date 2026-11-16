using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;

namespace VpnFleet.Client;

/// <summary>A camera of a checklist: what the action would do to it, and whether it is in.</summary>
public sealed partial class CameraCheck(Guid id, string label, string note, bool isChecked, bool isWarning = false, bool isEnabled = true) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Label { get; } = label;

    /// <summary>"App installed", "1.104.6 to 1.104.7", "App not installed".</summary>
    public string Note { get; } = note;

    public bool IsWarning { get; } = isWarning;

    public bool IsEnabled { get; } = isEnabled;

    /// <summary>The camera has the app of the action installed.</summary>
    public bool HasApp { get; init; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked && isEnabled;

    /// <summary>Raised when <see cref="IsChecked"/> changes.</summary>
    public event EventHandler? CheckedChanged;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// The cameras an action on many cameras changes, one checkbox each, so the user sees and chooses exactly which ones.
/// </summary>
public sealed partial class CameraChecklist : ObservableObject
{
    public RangeObservableCollection<CameraCheck> Items { get; } = [];

    /// <summary>"3 of 12 cameras will be changed".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial int CheckedCount { get; private set; }

    public IReadOnlyList<Guid> CheckedIds => [.. Items.Where(i => i.IsChecked).Select(i => i.Id)];

    public event EventHandler? Changed;

    public void Set(IEnumerable<CameraCheck> items)
    {
        foreach (var old in Items)
        {
            old.CheckedChanged -= OnItemChanged;
        }

        var list = items.ToList();
        foreach (var item in list)
        {
            item.CheckedChanged += OnItemChanged;
        }

        Items.ReplaceAll(list);
        Update();
    }

    [RelayCommand]
    private void CheckAll() => SetAll(true);

    [RelayCommand]
    private void CheckNone() => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var item in Items.Where(i => i.IsEnabled))
        {
            item.CheckedChanged -= OnItemChanged;
            item.IsChecked = value;
            item.CheckedChanged += OnItemChanged;
        }

        Update();
    }

    private void OnItemChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        CheckedCount = Items.Count(i => i.IsChecked);
        Summary = Items.Count == 0
            ? "Choose devices on the page first."
            : CheckedCount == 1 ? $"1 of {Items.Count:N0} cameras will be changed." : $"{CheckedCount:N0} of {Items.Count:N0} cameras will be changed.";
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
