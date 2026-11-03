using System.Collections.Generic;
using System.ComponentModel;

namespace AVAQueryBuilder;

public class DerivedFieldViewModel : INotifyPropertyChanged
{
    private string _selectedField = string.Empty;
    private string _selectedDerivation = string.Empty;
    private string _parameter = string.Empty;
    private string _alias = string.Empty;
    private bool _showParameter;

    public static readonly List<string> Derivations = new()
    {
        // String
        "UPPER CASE",
        "lower case",
        "Trim",
        "Length",
        "Left N Chars",
        "Right N Chars",
        "Reverse",
        // Date
        "Year Part",
        "Month Part",
        "Day Part",
        "Hour Part",
        "Month Name",
        "Day Name",
        "Date Only",
        "DATEDIFF (Days)",
        "DATEDIFF (Months)",
        "DATEDIFF (Years)",
        "DATEADD (Days)",
        "DATEADD (Months)",
        "DATEADD (Years)",
        // Null handling
        "ISNULL (Default)",
        "COALESCE (Fallback)",
        // Numeric
        "Absolute Value",
        "Round to N Decimals",
        "Ceiling",
        "Floor"
    };

    public static readonly HashSet<string> ParameterRequired = new()
    {
        "Left N Chars",
        "Right N Chars",
        "Round to N Decimals",
        "DATEDIFF (Days)",
        "DATEDIFF (Months)",
        "DATEDIFF (Years)",
        "DATEADD (Days)",
        "DATEADD (Months)",
        "DATEADD (Years)",
        "ISNULL (Default)",
        "COALESCE (Fallback)"
    };

    public List<string> AvailableFields { get; set; } = new();

    public string SelectedField
    {
        get => _selectedField;
        set { _selectedField = value; OnPropertyChanged(nameof(SelectedField)); }
    }

    public string SelectedDerivation
    {
        get => _selectedDerivation;
        set
        {
            _selectedDerivation = value;
            ShowParameter = ParameterRequired.Contains(value);
            OnPropertyChanged(nameof(SelectedDerivation));
        }
    }

    public string Parameter
    {
        get => _parameter;
        set { _parameter = value; OnPropertyChanged(nameof(Parameter)); }
    }

    public string Alias
    {
        get => _alias;
        set { _alias = value; OnPropertyChanged(nameof(Alias)); }
    }

    public bool ShowParameter
    {
        get => _showParameter;
        set { _showParameter = value; OnPropertyChanged(nameof(ShowParameter)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The derivations offered in the dialog, for the connected engine.</summary>
    public List<string> AvailableDerivations { get; } = Dialect.Derivations(AppState.Engine);

    public static string ToSqlExpression(string field, string derivation, string parameter, DbEngine? engine = null) =>
        Dialect.Derive(engine ?? AppState.Engine, field, derivation, parameter);
}
