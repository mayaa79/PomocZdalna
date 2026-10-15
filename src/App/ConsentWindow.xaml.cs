using System.Windows;
using PomocZdalna.App.Host;

namespace PomocZdalna.App;

public partial class ConsentWindow : Window
{
    public ConsentResult? Result { get; private set; }

    public ConsentWindow(string techName)
    {
        InitializeComponent();
        Question.Text = $"„{techName}” prosi o połączenie z tym komputerem.";
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Result = new ConsentResult(ControlCheck.IsChecked == true, CommandsCheck.IsChecked == true);
        Close();
    }

    private void Deny_Click(object sender, RoutedEventArgs e) => Close();
}
