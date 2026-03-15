using System.Collections.Generic;
using System.ComponentModel;

namespace AVAQueryBuilder;

public class SortingFieldViewModel : INotifyPropertyChanged
{
    private string _selectedField = string.Empty;
    private string _selectedDirection = "ASC";

    public static readonly List<string> Directions = new() { "ASC", "DESC" };

    public List<string> AvailableFields { get; set; } = new();

    public string SelectedField
    {
        get => _selectedField;
        set { _selectedField = value; OnPropertyChanged(nameof(SelectedField)); }
    }

    public string SelectedDirection
    {
        get => _selectedDirection;
        set { _selectedDirection = value; OnPropertyChanged(nameof(SelectedDirection)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
