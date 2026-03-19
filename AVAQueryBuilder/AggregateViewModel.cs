using System.Collections.Generic;
using System.ComponentModel;

namespace AVAQueryBuilder;

public class AggregateViewModel : INotifyPropertyChanged
{
    private string _selectedFunction = "COUNT";
    private string _selectedField = string.Empty;
    private string _alias = string.Empty;
    private bool _showField = true;

    public static readonly List<string> Functions = new()
    {
        "COUNT(*)", "COUNT", "COUNT DISTINCT", "SUM", "AVG", "MIN", "MAX"
    };

    public List<string> AvailableFields { get; set; } = new();

    public string SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            _selectedFunction = value;
            ShowField = value != "COUNT(*)";
            OnPropertyChanged(nameof(SelectedFunction));
        }
    }

    public string SelectedField
    {
        get => _selectedField;
        set { _selectedField = value; OnPropertyChanged(nameof(SelectedField)); }
    }

    public string Alias
    {
        get => _alias;
        set { _alias = value; OnPropertyChanged(nameof(Alias)); }
    }

    public bool ShowField
    {
        get => _showField;
        set { _showField = value; OnPropertyChanged(nameof(ShowField)); }
    }

    public string ToSqlExpression()
    {
        return SelectedFunction switch
        {
            "COUNT(*)" => "COUNT(*)",
            "COUNT DISTINCT" => $"COUNT(DISTINCT {SelectedField})",
            _ => $"{SelectedFunction}({SelectedField})"
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class HavingViewModel : INotifyPropertyChanged
{
    private string _expression = string.Empty;
    private string _selectedOperator = ">";
    private string _value = string.Empty;

    public static readonly List<string> Operators = new()
    {
        "=", "!=", "<", ">", "<=", ">="
    };

    public List<string> AvailableExpressions { get; set; } = new();

    public string Expression
    {
        get => _expression;
        set { _expression = value; OnPropertyChanged(nameof(Expression)); }
    }

    public string SelectedOperator
    {
        get => _selectedOperator;
        set { _selectedOperator = value; OnPropertyChanged(nameof(SelectedOperator)); }
    }

    public string Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(nameof(Value)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
