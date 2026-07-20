using Halfway.Core;
using Halfway.Persistence;
using Halfway.Runtime;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;
using Halfway.Terminal.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Halfway.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan CompletionBatchWindow = TimeSpan.FromMilliseconds(250);
    private readonly SessionRegistry _registry = new();
    private readonly SessionCoordinator _coordinator;
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceCatalog _catalog;
    private readonly DurableAlertLedger _ledger;
    private readonly Dictionary<Guid, TerminalSessionView> _views = [];
    private readonly Dictionary<Guid, Button> _sidebarButtons = [];
    private readonly Dictionary<Guid, TabViewItem> _tabs = [];
    private IProcessReadinessAdapter _plannerReadiness = new ShellReadinessAdapter();
    private AlertInputCoordinator _alerts;
    private readonly object _lifecyclePersistenceGate = new();
    private Task _lifecyclePersistence = Task.CompletedTask;
    private DurableAlertBatch? _currentBatch;
    private CancellationTokenSource? _batchDelay;
    private bool _initialized;
    private bool _syncingSelection;

    public MainWindow()
    {
        InitializeComponent();
        _store = new SqliteWorkspaceStore(SqliteWorkspaceStore.ProductionDatabasePath);
        _catalog = new WorkspaceCatalog(_store);
        _ledger = new DurableAlertLedger(_store);
        _coordinator = new SessionCoordinator(new ConPtyTerminalSessionFactory(), _registry);
        _coordinator.OutputReceived += Coordinator_OutputReceived;
        _coordinator.StateChanged += Coordinator_StateChanged;
        _coordinator.LifecycleTransitioned += Coordinator_LifecycleTransitioned;
        _alerts = new AlertInputCoordinator(_plannerReadiness);
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized) return; _initialized = true;
        try
        {
            var runtimeProfile = string.Equals(Environment.GetEnvironmentVariable("HALFWAY_RUNTIME_LAUNCH"), "codex", StringComparison.OrdinalIgnoreCase) ? LaunchProfile.Codex : LaunchProfile.PowerShell;
            await _catalog.InitializeAsync(GetWorkingDirectory(), runtimeProfile);
            await _ledger.RecoverAsync();
            foreach (var session in _catalog.Sessions.OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder))
                _registry.Register(new AgentSession(session.Id, session.DisplayName, session.Kind, session.ParentSessionId, session.LastStatus));
            BuildWorkspaceUi(); RefreshInformationBar();
            if (_catalog.SelectedPrimary is { } primary) await StartSessionAsync(primary);
            if (_catalog.SelectedSubAgent is { } subAgent) await StartSessionAsync(subAgent);
        }
        catch (Exception exception)
        {
            ConnectionText.Text = "! INITIALIZATION FAILED";
            var failure = new TextBlock { Text = $"[Unable to initialize Halfway: {exception.Message}]", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 138, 138)), TextWrapping = TextWrapping.Wrap };
            PrimaryTerminalHost.Children.Clear(); PrimaryTerminalHost.Children.Add(failure);
        }
    }

    private void BuildWorkspaceUi()
    {
        WorkspaceText.Text = _catalog.Workspace.DisplayName;
        RepositoryText.Text = new DirectoryInfo(_catalog.Workspace.WorkingDirectory).Name;
        BranchText.Text = ReadBranch(_catalog.Workspace.WorkingDirectory);
        foreach (var session in _catalog.Sessions.OrderBy(x => x.DisplayOrder)) AddSessionUi(session);
        ApplySelection();
    }

    private void AddSessionUi(SessionMetadata session)
    {
        var button = new Button { Tag = session.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)), Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255,185,192,203)), Padding = new Thickness(10,9,10,9) };
        button.Click += SidebarButton_Click; _sidebarButtons[session.Id] = button;
        (session.Kind == AgentKind.Primary ? PrimaryList : SubAgentList).Children.Add(button);
        var view = new TerminalSessionView(session); WireView(view); _views[session.Id] = view;
        if (session.Kind == AgentKind.SubAgent)
        {
            var tab = new TabViewItem { Tag = session.Id, Content = view, IsClosable = false };
            _tabs[session.Id] = tab; SubAgentTabs.TabItems.Add(tab);
        }
        UpdateSessionUi(session);
    }

    private void WireView(TerminalSessionView view)
    {
        view.StartRequested += async (_, _) => await StartSessionAsync(view.Metadata);
        view.StopRequested += async (_, _) => await StopSessionAsync(view.Metadata);
        view.PowerShellRequested += async (_, _) => await ReplacePrimaryAsync(view.Metadata, LaunchProfile.PowerShell);
        view.CodexRequested += async (_, _) => await ReplacePrimaryAsync(view.Metadata, LaunchProfile.Codex);
        view.DemoAlertRequested += async (_, _) => { _alerts.RequestDemonstrationAlert(); await TryDeliverAlertAsync(view.Metadata); };
        view.InputSubmitted += async (_, input) => await SendInputAsync(view.Metadata, input);
        view.ResizeRequested += (_, size) => { try { _coordinator.Resize(view.Metadata.SessionKey, size); } catch (KeyNotFoundException) { } };
        if (view.Metadata.Kind == AgentKind.Primary) view.PartialInputChanged += (_, _) => _alerts.SetUserInput(view.PartialInput);
    }

    private async Task StartSessionAsync(SessionMetadata metadata)
    {
        var view = _views[metadata.Id]; view.ClearOutput();
        if (metadata.Kind == AgentKind.Primary)
        {
            _plannerReadiness = metadata.LaunchProfile == LaunchProfile.Codex ? new CodexReadinessAdapter() : new ShellReadinessAdapter();
            _alerts = new AlertInputCoordinator(_plannerReadiness);
        }
        try
        {
            await _coordinator.StartAsync(new ManagedSession(metadata.SessionKey, metadata.Id, metadata.DisplayName, metadata.Kind, metadata.ParentSessionId),
                RuntimeLaunchAdapterSelection.Create(metadata.LaunchProfile == LaunchProfile.Codex ? RuntimeLaunchProfile.Codex : RuntimeLaunchProfile.PowerShell),
                new RuntimeLaunchContext(_catalog.Workspace.WorkingDirectory, new TerminalSize(100,30)));
            if (metadata.Kind == AgentKind.Primary) await QueuePendingAlertsAsync(metadata);
            view.FocusInput();
        }
        catch (Exception exception)
        {
            await _catalog.UpdateStatusAsync(metadata.Id, AgentStatus.Failed);
            UpdateSessionUi(_catalog.Sessions.Single(x => x.Id == metadata.Id)); RefreshInformationBar();
            view.Append($"[Unable to start {metadata.DisplayName}: {exception.Message}]\n");
        }
    }

    private async Task StopSessionAsync(SessionMetadata metadata)
    {
        try { await _coordinator.StopAsync(metadata.SessionKey); }
        catch (Exception exception) { _views[metadata.Id].Append($"\n[Stop failed: {exception.Message}]\n"); }
    }

    private async Task SendInputAsync(SessionMetadata metadata, string input)
    {
        try
        {
            await _coordinator.WriteAsync(metadata.SessionKey, input + "\r");
            if (metadata.Kind == AgentKind.Primary) await TryDeliverAlertAsync(metadata);
        }
        catch (Exception exception) { _views[metadata.Id].Append($"\n[Input failed: {exception.Message}]\n"); }
    }

    private async Task ReplacePrimaryAsync(SessionMetadata metadata, LaunchProfile profile)
    {
        if (_registry.Get(metadata.Id).Status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting)
            await StopSessionAsync(metadata);
        await StartSessionAsync(metadata with { LaunchProfile = profile });
    }

    private void Coordinator_OutputReceived(object? sender, SessionOutput output)
    {
        var metadata = _catalog.Sessions.FirstOrDefault(x => x.SessionKey == output.Key); if (metadata is null) return;
        if (metadata.Kind == AgentKind.Primary) _plannerReadiness.ObserveOutput(output.Text);
        DispatcherQueue.TryEnqueue(async () => { _views[metadata.Id].Append(output.Text); if (metadata.Kind == AgentKind.Primary) await TryDeliverAlertAsync(metadata); });
    }

    private void Coordinator_StateChanged(object? sender, SessionStateChanged state)
    {
        var metadata = _catalog.Sessions.FirstOrDefault(x => x.SessionKey == state.Key); if (metadata is null) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await _catalog.UpdateStatusAsync(metadata.Id, state.Status); UpdateSessionUi(_catalog.Sessions.Single(x => x.Id == metadata.Id)); RefreshInformationBar(); }
            catch (Exception exception) { _views[metadata.Id].Append($"\n[Unable to persist status: {exception.Message}]\n"); }
        });
    }

    private void Coordinator_LifecycleTransitioned(object? sender, LifecycleTransition transition)
    {
        lock (_lifecyclePersistenceGate)
        {
            _lifecyclePersistence = _lifecyclePersistence.ContinueWith(
                _ => PersistLifecycleTransitionAsync(transition),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task PersistLifecycleTransitionAsync(LifecycleTransition transition)
    {
        try
        {
            await _ledger.RecordAsync(transition);
            if (transition.Alert is { } alert)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (_catalog.SelectedPrimary?.Id == alert.ParentSessionId && _catalog.SelectedPrimary is { } parent)
                    {
                        SchedulePendingAlerts(parent);
                    }
                });
            }
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_catalog.Sessions.FirstOrDefault(x => x.Id == transition.Session.Id) is { } session)
                    _views[session.Id].Append($"\n[Unable to persist lifecycle event: {exception.Message}]\n");
            });
        }
    }

    private async Task TryDeliverAlertAsync(SessionMetadata parent)
    {
        var alert = _alerts.TakeReadyAlertReservation(); if (alert is null) return;
        var eventIds = alert.EventId == Guid.Empty
            ? Array.Empty<Guid>()
            : _currentBatch is { } batch && batch.ReservationId == alert.EventId ? batch.EventIds : [alert.EventId];
        if (eventIds.Count > 0 && !await _ledger.ReserveAsync(eventIds))
        {
            _alerts.CommitAlertDelivery();
            _currentBatch = null;
            await QueuePendingAlertsAsync(parent);
            return;
        }
        try
        {
            await _coordinator.WriteAsync(parent.SessionKey, alert.Message + "\r");
            if (eventIds.Count > 0 && !await _ledger.CommitAsync(eventIds)) throw new InvalidOperationException("Alert delivery could not be committed.");
            _alerts.CommitAlertDelivery();
            _currentBatch = null;
            await QueuePendingAlertsAsync(parent);
        }
        catch
        {
            if (eventIds.Count > 0) await _ledger.ReleaseAsync(eventIds);
            _alerts.ReleaseAlertDelivery();
        }
    }

    private async Task QueuePendingAlertsAsync(SessionMetadata parent)
    {
        var batch = await _ledger.CreatePendingBatchAsync(parent.Id); if (batch is null) return;
        _currentBatch = batch;
        _alerts.RequestAlert(batch.ReservationId, batch.Message);
        await TryDeliverAlertAsync(parent);
    }

    private void SchedulePendingAlerts(SessionMetadata parent)
    {
        _batchDelay?.Cancel(); _batchDelay?.Dispose(); _batchDelay = new CancellationTokenSource(); var token = _batchDelay.Token;
        _ = WaitForCompletionBatchAsync(parent, token);
    }

    private async Task WaitForCompletionBatchAsync(SessionMetadata parent, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CompletionBatchWindow, cancellationToken);
            await QueuePendingAlertsAsync(parent);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() => _views[parent.Id].Append($"\n[Unable to prepare completion batch: {exception.Message}]\n"));
        }
    }

    private async void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        var session = _catalog.Sessions.Single(x => x.Id == id);
        if (session.Kind == AgentKind.Primary) await _catalog.SelectPrimaryAsync(id); else await _catalog.SelectSubAgentAsync(id);
        ApplySelection(); _views[id].FocusInput();
    }

    private async void SubAgentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || SubAgentTabs.SelectedItem is not TabViewItem { Tag: Guid id } || !_initialized) return;
        await _catalog.SelectSubAgentAsync(id); ApplySelection(); _views[id].FocusInput();
    }

    private void ApplySelection()
    {
        _syncingSelection = true;
        try
        {
            if (_catalog.SelectedPrimary is { } primary) { MainSessionTitle.Text = primary.DisplayName; PrimaryTerminalHost.Children.Clear(); PrimaryTerminalHost.Children.Add(_views[primary.Id]); }
            if (_catalog.SelectedSubAgent is { } sub && _tabs.TryGetValue(sub.Id, out var tab)) SubAgentTabs.SelectedItem = tab;
            foreach (var pair in _sidebarButtons) pair.Value.Background = new SolidColorBrush(pair.Key == _catalog.Workspace.SelectedPrimarySessionId || pair.Key == _catalog.Workspace.SelectedSubAgentSessionId ? Windows.UI.Color.FromArgb(255,37,42,53) : Windows.UI.Color.FromArgb(0,0,0,0));
        }
        finally { _syncingSelection = false; }
    }

    private async void AddSubAgent_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Display name" }; var profile = new ComboBox { Header = "Launch profile", ItemsSource = new[] { "PowerShell", "Codex" }, SelectedIndex = 0 };
        var error = new TextBlock { Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255,241,138,138)) };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(name); panel.Children.Add(profile); panel.Children.Add(error);
        var dialog = new ContentDialog { Title = "Add sub-agent", Content = panel, PrimaryButtonText = "Add", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        while (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                var created = await _catalog.CreateSubAgentAsync(name.Text, profile.SelectedIndex == 1 ? LaunchProfile.Codex : LaunchProfile.PowerShell);
                _registry.Register(new AgentSession(created.Id, created.DisplayName, created.Kind, created.ParentSessionId)); AddSessionUi(created); ApplySelection(); await StartSessionAsync(created); return;
            }
            catch (Exception exception) { error.Text = exception.Message; }
        }
    }

    private void UpdateSessionUi(SessionMetadata session)
    {
        _views[session.Id].SetStatus(session.LastStatus);
        _sidebarButtons[session.Id].Content = new TextBlock { Text = $"{StatusPresentation.Glyph(session.LastStatus)}  {session.DisplayName}" };
        if (_tabs.TryGetValue(session.Id, out var tab)) tab.Header = $"{session.DisplayName} {StatusPresentation.Glyph(session.LastStatus)}";
    }

    private void RefreshInformationBar()
    {
        var counts = _catalog.Sessions.GroupBy(x => x.LastStatus).ToDictionary(x => x.Key, x => x.Count());
        RunningCountText.Text = $"RUNNING {counts.GetValueOrDefault(AgentStatus.Running)}"; WaitingCountText.Text = $"WAITING {counts.GetValueOrDefault(AgentStatus.Waiting)}"; CompletedCountText.Text = $"COMPLETE {counts.GetValueOrDefault(AgentStatus.Completed)}";
        ConnectionText.Text = counts.GetValueOrDefault(AgentStatus.Running) > 0 ? "● CONNECTED" : "! DISCONNECTED";
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _batchDelay?.Cancel(); _batchDelay?.Dispose();
        _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Task persistence;
        lock (_lifecyclePersistenceGate) persistence = _lifecyclePersistence;
        persistence.GetAwaiter().GetResult();
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static string GetWorkingDirectory() { var configured=Environment.GetEnvironmentVariable("HALFWAY_WORKING_DIRECTORY");return !string.IsNullOrWhiteSpace(configured)&&Directory.Exists(configured)?configured:Environment.CurrentDirectory; }
    private static string ReadBranch(string path)
    {
        try { var head=Path.Combine(path,".git","HEAD");if(!File.Exists(head))return "—";var value=File.ReadAllText(head).Trim();const string prefix="ref: refs/heads/";return value.StartsWith(prefix,StringComparison.Ordinal)?value[prefix.Length..]:value.Length>=7?value[..7]:"—"; } catch { return "—"; }
    }
}
