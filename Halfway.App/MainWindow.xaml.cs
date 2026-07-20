using System.Text.RegularExpressions;
using Halfway.Core;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;
using Halfway.Terminal.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Halfway.App;

public sealed partial class MainWindow : Window
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private readonly ITerminalSessionFactory _terminalFactory = new ConPtyTerminalSessionFactory();
    private readonly SessionRegistry _registry = new();
    private readonly Guid _sessionId = Guid.NewGuid();
    private ITerminalSession? _terminalSession;
    private IProcessReadinessAdapter _readiness = new ShellReadinessAdapter();
    private AlertInputCoordinator _alertCoordinator;
    private bool _closing;
    private bool _submittingUserInput;

    public MainWindow()
    {
        InitializeComponent();
        _alertCoordinator = new AlertInputCoordinator(_readiness);
        _registry.Register(new AgentSession(_sessionId, "Planner", AgentKind.Primary, null));
        SubAgentTabs.SelectedIndex = 0;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
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

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await StartTerminalAsync(TerminalLaunchOptions.PowerShell(GetWorkingDirectory()), false);
    }

    private async void PowerShellButton_Click(object sender, RoutedEventArgs e) =>
        await StartTerminalAsync(TerminalLaunchOptions.PowerShell(GetWorkingDirectory()), false);

    private async void CodexButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartTerminalAsync(
                ProcessCommandResolver.ResolveCodex(GetWorkingDirectory(), GetTerminalSize()),
                true);
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
        }
    }

    private async Task StartTerminalAsync(TerminalLaunchOptions options, bool isCodex)
    {
        await StopCurrentSessionAsync();
        TerminalOutputText.Text = string.Empty;
        _readiness = isCodex ? new CodexReadinessAdapter() : new ShellReadinessAdapter();
        _alertCoordinator = new AlertInputCoordinator(_readiness);
        Transition(AgentStatus.Queued);

        try
        {
            var sizedOptions = options with { InitialSize = GetTerminalSize() };
            var session = await _terminalFactory.StartAsync(sizedOptions);
            _terminalSession = session;
            session.OutputReceived += TerminalSession_OutputReceived;
            session.Exited += TerminalSession_Exited;
            Transition(AgentStatus.Running);
            TerminalInputText.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            ShowStartupFailure(exception);
        }
    }

    private void TerminalSession_OutputReceived(object? sender, string output)
    {
        _readiness.ObserveOutput(output);
        DispatcherQueue.TryEnqueue(async () =>
        {
            AppendOutput(output);
            await TryDeliverAlertAsync();
        });
    }

    private void TerminalSession_Exited(object? sender, TerminalExit exit)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Transition(exit.WasCancelled
                ? AgentStatus.Disconnected
                : exit.ExitCode == 0 ? AgentStatus.Completed : AgentStatus.Failed);
            AppendOutput($"\n[Process exited with code {exit.ExitCode}]\n");
        });
    }

    private async void TerminalInputText_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || _terminalSession is null)
        {
            return;
        }

        e.Handled = true;
        var command = TerminalInputText.Text;
        _submittingUserInput = true;
        TerminalInputText.Text = string.Empty;
        try
        {
            await _terminalSession.WriteAsync(command + "\r");
            _submittingUserInput = false;
            await TryDeliverAlertAsync();
        }
        catch (Exception exception)
        {
            _submittingUserInput = false;
            AppendOutput($"\n[Input failed: {exception.Message}]\n");
            Transition(AgentStatus.Disconnected);
        }
    }

    private async void TerminalInputText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _alertCoordinator.SetUserInput(TerminalInputText.Text);
        if (TerminalInputText.Text.Length == 0 && !_submittingUserInput)
        {
            await TryDeliverAlertAsync();
        }
    }

    private async void AlertButton_Click(object sender, RoutedEventArgs e)
    {
        _alertCoordinator.RequestDemonstrationAlert();
        AlertButton.IsEnabled = false;
        await TryDeliverAlertAsync();
        if (_alertCoordinator.HasQueuedAlert)
        {
            TerminalStatusText.Text = "ALERT QUEUED";
        }
    }

    private async Task TryDeliverAlertAsync()
    {
        var alert = _alertCoordinator.TakeReadyAlert();
        if (alert is null || _terminalSession is null)
        {
            return;
        }

        await _terminalSession.WriteAsync(alert + "\r");
        TerminalStatusText.Text = "ALERT DELIVERED";
    }

    private void TerminalPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            _terminalSession?.Resize(GetTerminalSize());
        }
        catch (Exception exception)
        {
            AppendOutput($"\n[Resize failed: {exception.Message}]\n");
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closing = true;
        StopCurrentSessionAsync().GetAwaiter().GetResult();
    }

    private async Task StopCurrentSessionAsync()
    {
        var session = Interlocked.Exchange(ref _terminalSession, null);
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= TerminalSession_OutputReceived;
        session.Exited -= TerminalSession_Exited;
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            if (!_closing)
            {
                AppendOutput($"\n[Shutdown failed: {exception.Message}]\n");
            }
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        Transition(AgentStatus.Failed);
        AppendOutput($"[Unable to start terminal: {exception.Message}]\n");
    }

    private void Transition(AgentStatus status)
    {
        _registry.Transition(_sessionId, status);
        TerminalStatusText.Text = status.ToString().ToUpperInvariant();
    }

    private void AppendOutput(string output)
    {
        var plain = Regex.Replace(output, "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty);
        var combined = TerminalOutputText.Text + plain;
        TerminalOutputText.Text = combined.Length <= MaximumOutputCharacters
            ? combined
            : combined[^MaximumOutputCharacters..];
        TerminalScrollViewer.UpdateLayout();
        TerminalScrollViewer.ChangeView(null, TerminalScrollViewer.ScrollableHeight, null, true);
    }

    private TerminalSize GetTerminalSize()
    {
        var columns = (short)Math.Clamp((int)(TerminalPanel.ActualWidth / 8), 20, 240);
        var rows = (short)Math.Clamp((int)(TerminalPanel.ActualHeight / 18), 5, 100);
        return new TerminalSize(columns, rows);
    }

    private static string GetWorkingDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("HALFWAY_WORKING_DIRECTORY");
        return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)
            ? configured
            : Environment.CurrentDirectory;
    }
}
