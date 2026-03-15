using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AVAQueryBuilder;

public partial class UnderConstructionWindow : Window
{
    public UnderConstructionWindow() : this("This function") { }

    public UnderConstructionWindow(string functionName)
    {
        InitializeComponent();
        MessageText.Text = $"{functionName} is under construction";
    }

    private void CmdClose_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
