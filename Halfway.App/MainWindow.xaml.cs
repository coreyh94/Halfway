using System.Text.RegularExpressions;
using Halfway.Core;
using Halfway.Runtime;
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
    private const string PlannerKey = "planner";
    private const string RuntimeKey = "runtime";
    private readonly SessionRegistry _registry = new();
    private readonly SessionCoordinator _sessions;
    private readonly Guid _plannerId = Guid.NewGuid();
    private readonly Guid _runtimeId = Guid.NewGuid();
    private IProcessReadinessAdapter _readiness = new ShellReadinessAdapter();
    private AlertInputCoordinator _alertCoordinator;
    private bool _submittingUserInput;

    public MainWindow()
    {
        InitializeComponent();
        _sessions = new SessionCoordinator(new ConPtyTerminalSessionFactory(), _registry);
        _sessions.OutputReceived += Sessions_OutputReceived;
        _sessions.StateChanged += Sessions_StateChanged;
        _sessions.CompletionAlertReady += Sessions_CompletionAlertReady;
        _alertCoordinator = new AlertInputCoordinator(_readiness);
        _registry.Register(new AgentSession(_plannerId, "Planner", AgentKind.Primary, null));
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
        await StartPlannerAsync(TerminalLaunchOptions.PowerShell(GetWorkingDirectory()), false);
        await StartRuntimeAsync();
    }

    private async void PowerShellButton_Click(object sender, RoutedEventArgs e) =>
        await StartPlannerAsync(TerminalLaunchOptions.PowerShell(GetWorkingDirectory()), false);

    private async void CodexButton_Click(object sender, RoutedEventArgs e)
    {
        try { await StartPlannerAsync(ProcessCommandResolver.ResolveCodex(GetWorkingDirectory(), GetTerminalSize()), true); }
        catch (Exception exception) { ShowPlannerStartupFailure(exception); }
    }

    private async Task StartPlannerAsync(TerminalLaunchOptions options, bool isCodex)
    {
        await _sessions.StopAsync(PlannerKey);
        TerminalOutputText.Text = string.Empty;
        _readiness = isCodex ? new CodexReadinessAdapter() : new ShellReadinessAdapter();
        _alertCoordinator = new AlertInputCoordinator(_readiness);
        TransitionPlanner(AgentStatus.Queued);
        try
        {
            await _sessions.StartAsync(
                new ManagedSession(PlannerKey, _plannerId, "Planner", AgentKind.Primary, null),
                options with { InitialSize = GetTerminalSize() });
            TerminalInputText.Focus(FocusState.Programmatic);
        }
        catch (Exception exception) { ShowPlannerStartupFailure(exception); }
    }

    private async Task StartRuntimeAsync()
    {
        try
        {
            await _sessions.StartAsync(
                new ManagedSession(RuntimeKey, _runtimeId, "Runtime", AgentKind.SubAgent, _plannerId),
                TerminalLaunchOptions.PowerShell(GetWorkingDirectory()) with { InitialSize = GetRuntimeSize() });
            RuntimeInputText.Focus(FocusState.Programmatic);
        }
        catch (Exception exception) { AppendRuntime($"[Unable to start Runtime: {exception.Message}]\n"); }
    }

    private void Sessions_OutputReceived(object? sender, SessionOutput output)
    {
        if (output.Key == PlannerKey) _readiness.ObserveOutput(output.Text);
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (output.Key == PlannerKey)
            {
                AppendOutput(output.Text);
                await TryDeliverAlertAsync();
            }
            else if (output.Key == RuntimeKey) AppendRuntime(output.Text);
        });
    }

    private void Sessions_StateChanged(object? sender, SessionStateChanged state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (state.Key == PlannerKey) TransitionPlanner(state.Status);
            if (state.Key == RuntimeKey) SetRuntimeStatus(state.Status);
        });
    }

    private void Sessions_CompletionAlertReady(object? sender, CompletionAlert alert)
    {
        if (alert.ParentSessionId != _plannerId) return;
        _alertCoordinator.RequestAlert(alert.Message);
        DispatcherQueue.TryEnqueue(async () => await TryDeliverAlertAsync());
    }

    private async void TerminalInputText_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var command = TerminalInputText.Text;
        _submittingUserInput = true;
        TerminalInputText.Text = string.Empty;
        try { await _sessions.WriteAsync(PlannerKey, command + "\r"); await TryDeliverAlertAsync(); }
        catch (Exception exception) { AppendOutput($"\n[Input failed: {exception.Message}]\n"); TransitionPlanner(AgentStatus.Disconnected); }
        finally { _submittingUserInput = false; }
    }

    private async void RuntimeInputText_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var command = RuntimeInputText.Text;
        RuntimeInputText.Text = string.Empty;
        try { await _sessions.WriteAsync(RuntimeKey, command + "\r"); }
        catch (Exception exception) { AppendRuntime($"\n[Input failed: {exception.Message}]\n"); SetRuntimeStatus(AgentStatus.Disconnected); }
    }

    private async void TerminalInputText_TextChanged(object sender, TextChangedEventArgs e)
    {
        _alertCoordinator.SetUserInput(TerminalInputText.Text);
        if (TerminalInputText.Text.Length == 0 && !_submittingUserInput) await TryDeliverAlertAsync();
    }

    private async void AlertButton_Click(object sender, RoutedEventArgs e)
    {
        _alertCoordinator.RequestDemonstrationAlert();
        AlertButton.IsEnabled = false;
        await TryDeliverAlertAsync();
        if (_alertCoordinator.HasQueuedAlert) TerminalStatusText.Text = "ALERT QUEUED";
    }

    private async Task TryDeliverAlertAsync()
    {
        var alert = _alertCoordinator.TakeReadyAlert();
        if (alert is null) return;
        try { await _sessions.WriteAsync(PlannerKey, alert + "\r"); TerminalStatusText.Text = "ALERT DELIVERED"; }
        catch { TransitionPlanner(AgentStatus.Disconnected); }
    }

    private void TerminalPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeSession(PlannerKey, GetTerminalSize());
    private void RuntimePanel_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeSession(RuntimeKey, GetRuntimeSize());

    private void ResizeSession(string key, TerminalSize size)
    {
        try { _sessions.Resize(key, size); }
        catch (Exception exception) { if (key == RuntimeKey) AppendRuntime($"\n[Resize failed: {exception.Message}]\n"); else AppendOutput($"\n[Resize failed: {exception.Message}]\n"); }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void ShowPlannerStartupFailure(Exception exception) { TransitionPlanner(AgentStatus.Failed); AppendOutput($"[Unable to start terminal: {exception.Message}]\n"); }
    private void TransitionPlanner(AgentStatus status) { TerminalStatusText.Text = status.ToString().ToUpperInvariant(); }
    private void SetRuntimeStatus(AgentStatus status) { RuntimeStatusText.Text = status.ToString().ToUpperInvariant(); RuntimeTab.Header = $"Runtime {StatusGlyph(status)}"; RuntimeButtonStatus.Text = StatusGlyph(status); }
    private static string StatusGlyph(AgentStatus status) => status switch { AgentStatus.Running => "●", AgentStatus.Waiting => "◐", AgentStatus.Completed => "✓", AgentStatus.Failed or AgentStatus.Disconnected => "!", _ => "○" };

    private void AppendOutput(string output) => AppendBounded(TerminalOutputText, TerminalScrollViewer, output);
    private void AppendRuntime(string output) => AppendBounded(RuntimeOutputText, RuntimeScrollViewer, output);
    private static void AppendBounded(TextBlock target, ScrollViewer scroll, string output)
    {
        var plain = Regex.Replace(output, "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty);
        var combined = target.Text + plain;
        target.Text = combined.Length <= MaximumOutputCharacters ? combined : combined[^MaximumOutputCharacters..];
        scroll.UpdateLayout(); scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
    }

    private TerminalSize GetTerminalSize() => SizeFor(TerminalPanel);
    private TerminalSize GetRuntimeSize() => SizeFor(RuntimePanel);
    private static TerminalSize SizeFor(FrameworkElement panel) => new((short)Math.Clamp((int)(panel.ActualWidth / 8), 20, 240), (short)Math.Clamp((int)(panel.ActualHeight / 18), 5, 100));
    private static string GetWorkingDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("HALFWAY_WORKING_DIRECTORY");
        return !string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured) ? configured : Environment.CurrentDirectory;
    }
}
