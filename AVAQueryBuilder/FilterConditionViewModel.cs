using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace AVAQueryBuilder;

public class FilterConditionViewModel : INotifyPropertyChanged
{
    private string _selectedField = string.Empty;
    private string _selectedCastAs = "(none)";
    private string _selectedOperator = "=";
    private string _value = string.Empty;
    private string? _value2;
    private bool _showValue = true;
    private bool _showValue2;

    public static readonly List<string> CastTypes = new()
    {
        "(none)", "VARCHAR", "INT", "DATE", "DATETIME", "DECIMAL"
    };

    public static readonly List<string> Operators = new()
    {
        "=", "!=", "<", ">", "<=", ">=",
        "LIKE", "NOT LIKE", "IN", "NOT IN",
        "IS NULL", "IS NOT NULL", "BETWEEN",
        "IS EMPTY", "IS NOT EMPTY"
    };

    public List<string> AvailableFields { get; set; } = new();

    public string SelectedField
    {
        get => _selectedField;
        set { _selectedField = value; OnPropertyChanged(nameof(SelectedField)); }
    }

    public string SelectedCastAs
    {
        get => _selectedCastAs;
        set { _selectedCastAs = value; OnPropertyChanged(nameof(SelectedCastAs)); }
    }

    public string SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            _selectedOperator = value;
            ShowValue = value is not ("IS NULL" or "IS NOT NULL" or "IS EMPTY" or "IS NOT EMPTY");
            ShowValue2 = value == "BETWEEN";
            OnPropertyChanged(nameof(SelectedOperator));
        }
    }

    public string Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(nameof(Value)); }
    }

    public string? Value2
    {
        get => _value2;
        set { _value2 = value; OnPropertyChanged(nameof(Value2)); }
    }

    public bool ShowValue
    {
        get => _showValue;
        set { _showValue = value; OnPropertyChanged(nameof(ShowValue)); }
    }

    public bool ShowValue2
    {
        get => _showValue2;
        set { _showValue2 = value; OnPropertyChanged(nameof(ShowValue2)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
