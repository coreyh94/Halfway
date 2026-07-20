using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halfway.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SubAgentTabs.SelectedIndex = 0;
    }

    private void PlannerButton_Click(object sender, RoutedEventArgs e) => SelectPrimary("Planner");

    private void ProjectManagerButton_Click(object sender, RoutedEventArgs e) => SelectPrimary("Project Manager");

    private void RuntimeButton_Click(object sender, RoutedEventArgs e) => SelectSubAgent(0);

    private void UiButton_Click(object sender, RoutedEventArgs e) => SelectSubAgent(1);

    private void TestsButton_Click(object sender, RoutedEventArgs e) => SelectSubAgent(2);

    private void SelectPrimary(string name)
    {
        MainSessionTitle.Text = name;
        PlannerButton.Background = name == "Planner" ? Application.Current.Resources["SystemControlHighlightListAccentLowBrush"] as Microsoft.UI.Xaml.Media.Brush : null;
        ProjectManagerButton.Background = name == "Project Manager" ? Application.Current.Resources["SystemControlHighlightListAccentLowBrush"] as Microsoft.UI.Xaml.Media.Brush : null;
    }

    private void SelectSubAgent(int index) => SubAgentTabs.SelectedIndex = index;
}
