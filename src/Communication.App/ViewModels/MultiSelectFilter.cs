using System.Collections.ObjectModel;

namespace Communication.App.ViewModels;

public sealed class MultiSelectFilterOption(string value, bool selected, Action changed) : ObservableObject
{
    private bool _selected = selected;
    public string Value { get; } = value;
    private string? _label;
    public string Label => _label ?? (string.IsNullOrEmpty(Value) ? "—" : Value);
    public string ToolTip => Value;
    public void SetLabel(string label)
    {
        if (Set(ref _label, label, nameof(Label)))
            Notify(nameof(ToolTip));
    }
    public bool IsSelected
    {
        get => _selected; set
        {
            if (Set(ref _selected, value))
                changed();
        }
    }
}

public sealed class MultiSelectFilter(string title, Action changed) : ObservableObject
{
    private bool _includeNewValues = true;
    private bool _updating;
    private readonly Dictionary<string, MultiSelectFilterOption> _byValue = new(StringComparer.Ordinal);
    public string Title { get; } = title;
    public ObservableCollection<MultiSelectFilterOption> Options { get; } = [];
    public bool IsFiltered => !_includeNewValues || Options.Any(option => !option.IsSelected);
    public bool Matches(string value) => _byValue.TryGetValue(value, out var option) ? option.IsSelected : _includeNewValues;
    public void UpdateOptions(IEnumerable<string> values)
    {
        var available = values.ToHashSet(StringComparer.Ordinal);
        foreach (var option in Options.Where(option => !available.Contains(option.Value)).ToArray())
        {
            Options.Remove(option);
            _byValue.Remove(option.Value);
        }
        var index = 0;
        foreach (var value in available.Order(StringComparer.Ordinal))
        {
            while (index < Options.Count && StringComparer.Ordinal.Compare(Options[index].Value, value) < 0)
                index++;
            if (_byValue.ContainsKey(value))
            {
                index++;
                continue;
            }
            var option = new MultiSelectFilterOption(value, _includeNewValues, SelectionChanged);
            _byValue.Add(value, option);
            Options.Insert(index++, option);
        }
        Notify(nameof(IsFiltered));
    }
    public void SelectAll(bool selected)
    {
        _updating = true;
        try
        {
            _includeNewValues = selected;
            foreach (var option in Options)
                option.IsSelected = selected;
        }
        finally
        {
            _updating = false;
        }

        Notify(nameof(IsFiltered));
        changed();
    }
    private void SelectionChanged()
    {
        if (_updating)
            return;
        _includeNewValues = Options.All(option => option.IsSelected);
        Notify(nameof(IsFiltered));
        changed();
    }
}
