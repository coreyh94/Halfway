using Halfway.Core;
using Halfway.Persistence;
using Halfway.Runtime;
using Halfway.Terminal;
using Halfway.Terminal.Readiness;
using Halfway.Terminal.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Halfway.App;

public sealed partial class MainWindow : Window, IWorkspaceSwitchAdapter
{
    private readonly SessionRegistry _registry = new();
    private readonly SessionCoordinator _coordinator;
    private readonly AlertDeliveryCoordinator _alertDelivery;
    private CompletionBatchScheduler _completionBatches;
    private readonly IWorkspaceStore _store;
    private WorkspaceCatalog _catalog;
    private readonly DurableAlertLedger _ledger;
    private readonly Dictionary<Guid, TerminalSessionView> _views = [];
    private readonly Dictionary<Guid, Button> _sidebarButtons = [];
    private readonly Dictionary<Guid, TabViewItem> _tabs = [];
    private readonly SessionAttentionTracker _attention = new();
    private readonly FailureNotificationPolicy _failureNotifications = new();
    private readonly WindowsFailureNotifier _windowsNotifications;
    private readonly DiagnosticBuffer _diagnostics = new();
    private readonly DiagnosticExporter _diagnosticExporter = new();
    private IProcessReadinessAdapter _plannerReadiness = RuntimeReadinessAdapterSelection.Create(RuntimeLaunchProfile.PowerShell);
    private AlertInputCoordinator _alerts;
    private readonly OrderlyShutdownPersistence _shutdownPersistence = new();
    private DurableAlertBatch? _currentBatch;
    private readonly Queue<DurableAlertBatch> _preparedBatches = new();
    private bool _initialized;
    private bool _syncingSelection;
    private Guid? _focusedSessionId;
    private bool _isWindowActive;
    private ApplicationRunStart? _applicationRun;
    private double _preferredSidebarWidth = PanelSizing.DefaultSidebarWidth;
    private double _preferredSubAgentWidth = PanelSizing.DefaultDetailWidth;
    private bool _applyingPanelLayout;
    private bool _syncingWorkspaceSelector;
    private readonly WorkspaceActivationGeneration _activation = new();
    private WorkspaceSwitchCoordinator? _workspaceSwitch;

    public MainWindow()
    {
        InitializeComponent();
        _store = new SqliteWorkspaceStore(SqliteWorkspaceStore.ProductionDatabasePath);
        _catalog = new WorkspaceCatalog(_store);
        _ledger = new DurableAlertLedger(_store);
        _coordinator = new SessionCoordinator(new ConPtyTerminalSessionFactory(), _registry);
        _alertDelivery = new AlertDeliveryCoordinator(_coordinator);
        _completionBatches = CreateCompletionBatchScheduler(_activation.Current);
        _coordinator.OutputReceived += Coordinator_OutputReceived;
        _coordinator.StateChanged += Coordinator_StateChanged;
        _coordinator.LifecycleTransitioned += Coordinator_LifecycleTransitioned;
        _alerts = new AlertInputCoordinator(_plannerReadiness);
        _windowsNotifications = new WindowsFailureNotifier(() => DispatcherQueue.TryEnqueue(Activate));
        _diagnostics.Record("notification", "availability", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["available"] = _windowsNotifications.IsAvailable.ToString() });
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _isWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_initialized) return; _initialized = true;
        try
        {
            var runtimeProfile = string.Equals(Environment.GetEnvironmentVariable("HALFWAY_RUNTIME_LAUNCH"), "codex", StringComparison.OrdinalIgnoreCase) ? LaunchProfile.Codex : LaunchProfile.PowerShell;
            await _store.InitializeAsync();
            _diagnostics.Record("application", "startup", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["schemaVersion"] = SqliteWorkspaceStore.SchemaVersion.ToString(), ["outcome"] = "initialized" });
            var run = new ApplicationRun(Guid.NewGuid(), DateTimeOffset.UtcNow, null, typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");
            _applicationRun = await _store.StartApplicationRunAsync(run);
            var resolver = new WorkspaceRestoreResolver(_store);
            var workingDirectory = await resolver.ResolveAsync(Environment.GetEnvironmentVariable("HALFWAY_WORKING_DIRECTORY"), Environment.CurrentDirectory);
            await _catalog.InitializeAsync(workingDirectory, runtimeProfile);
            _workspaceSwitch = new WorkspaceSwitchCoordinator(_store, this);
            _diagnostics.Record("workspace", "restored", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["sessionCount"] = _catalog.Sessions.Count.ToString() });
            await _ledger.RecoverAsync();
            foreach (var session in _catalog.Sessions.OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder))
                _registry.Register(new AgentSession(session.Id, session.DisplayName, session.Kind, _catalog.GetParentSessionId(session.Id), session.LastStatus));
            BuildWorkspaceUi(); RefreshInformationBar();
            await RefreshWorkspaceSelectorAsync();
            if (_catalog.SelectedPrimary is { } primary) await StartSessionAsync(primary);
            if (_catalog.SelectedSubAgent is { } subAgent) await StartSessionAsync(subAgent);
        }
        catch (Exception exception)
        {
            RecordFailure("application", "startup-failed", exception);
            ConnectionText.Text = "! INITIALIZATION FAILED";
            var failure = new TextBlock { Text = $"[Unable to initialize Halfway: {exception.Message}]", Foreground = ThemeBrush("ErrorBrush"), TextWrapping = TextWrapping.Wrap };
            PrimaryTerminalHost.Children.Clear(); PrimaryTerminalHost.Children.Add(failure);
        }
    }

    private void BuildWorkspaceUi()
    {
        RepositoryText.Text = new DirectoryInfo(_catalog.Workspace.WorkingDirectory).Name;
        BranchText.Text = ReadBranch(_catalog.Workspace.WorkingDirectory);
        foreach (var session in _catalog.Sessions.OrderBy(x => x.DisplayOrder)) AddSessionUi(session);
        ApplySelection();
    }

    private void AddSessionUi(SessionMetadata session)
    {
        var button = new Button { Tag = session.Id, Style = AppStyle("SidebarRowButtonStyle"), Background = ThemeBrush("TransparentBrush") };
        button.Click += SidebarButton_Click; _sidebarButtons[session.Id] = button;
        (session.Kind == AgentKind.Primary ? PrimaryList : SubAgentList).Children.Add(button);
        var view = new TerminalSessionView(session); WireView(view, _activation.Current); _views[session.Id] = view;
        if (session.Kind == AgentKind.SubAgent)
        {
            var tab = new TabViewItem { Tag = session.Id, Content = view, IsClosable = false };
            _tabs[session.Id] = tab; SubAgentTabs.TabItems.Add(tab);
        }
        UpdateSessionUi(session);
    }

    private void WireView(TerminalSessionView view, long activation)
    {
        view.GotFocus += (_, _) => { if (_activation.IsCurrent(activation)) SetFocusedSession(view.Metadata.Id); };
        view.StartRequested += async (_, _) => { if (_activation.IsCurrent(activation)) await StartSessionAsync(view.Metadata); };
        view.StopRequested += async (_, _) => { if (_activation.IsCurrent(activation)) await StopSessionAsync(view.Metadata); };
        view.PowerShellRequested += async (_, _) => { if (_activation.IsCurrent(activation)) await ReplacePrimaryAsync(view.Metadata, LaunchProfile.PowerShell); };
        view.CodexRequested += async (_, _) => { if (_activation.IsCurrent(activation)) await ReplacePrimaryAsync(view.Metadata, LaunchProfile.Codex); };
        view.DemoAlertRequested += async (_, _) => { if (!_activation.IsCurrent(activation)) return; _alerts.RequestAlert(Guid.Empty, view.Metadata.Id, AlertInputCoordinator.DemonstrationAlert); await TryDeliverAlertAsync(view.Metadata); };
        view.SubmitInputAsync = input => _activation.IsCurrent(activation)
            ? AcceptInputAsync(view, view.Metadata, input)
            : Task.FromResult(TerminalInputAcceptance.RejectedSubmission);
        view.SendKeysAsync = data => _activation.IsCurrent(activation)
            ? SendKeysToSessionAsync(view.Metadata, data)
            : Task.CompletedTask;
        view.ResizeRequested += (_, size) => { if (!_activation.IsCurrent(activation)) return; try { _coordinator.Resize(view.Metadata.SessionKey, size); } catch (KeyNotFoundException) { } };
        if (view.Metadata.Kind == AgentKind.Primary) view.PartialInputChanged += (_, _) => { if (_activation.IsCurrent(activation)) _alerts.SetUserInput(view.PartialInput); };
    }

    private async Task StartSessionAsync(SessionMetadata metadata)
    {
        var activation = _activation.Current;
        metadata = _catalog.Sessions.Single(x => x.Id == metadata.Id);
        _views[metadata.Id].UpdateMetadata(metadata);
        var view = _views[metadata.Id]; view.ClearOutput();
        var runtimeProfile = metadata.LaunchProfile == LaunchProfile.Codex ? RuntimeLaunchProfile.Codex : RuntimeLaunchProfile.PowerShell;
        var readiness = RuntimeReadinessAdapterSelection.Create(runtimeProfile);
        if (metadata.Kind == AgentKind.Primary)
        {
            _plannerReadiness = readiness;
            _alerts = new AlertInputCoordinator(_plannerReadiness);
            _alerts.SetUserInput(view.PartialInput);
            _currentBatch = null;
            _preparedBatches.Clear();
        }
        try
        {
            await _coordinator.StartAsync(new ManagedSession(metadata.SessionKey, metadata.Id, metadata.DisplayName, metadata.Kind, _catalog.GetParentSessionId(metadata.Id)),
                RuntimeLaunchAdapterSelection.Create(runtimeProfile),
                new RuntimeLaunchContext(_catalog.Workspace.WorkingDirectory, new TerminalSize(100,30)), readiness);
            _diagnostics.Record("session", "started", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["sessionId"] = metadata.Id.ToString(), ["agentKind"] = metadata.Kind.ToString(), ["adapterId"] = readiness.Identity.Identifier, ["adapterVersion"] = readiness.Identity.Version.ToString(), ["outcome"] = "running" });
            if (metadata.Kind == AgentKind.Primary) await QueuePendingAlertsAsync(metadata);
            if (_activation.IsCurrent(activation))
            {
                SetFocusedSession(metadata.Id);
                view.FocusInput();
            }
        }
        catch (Exception exception)
        {
            if (!_activation.IsCurrent(activation)) return;
            RecordFailure("session", "start-failed", exception, metadata.Id);
            await _catalog.UpdateStatusAsync(metadata.Id, AgentStatus.Failed);
            UpdateSessionUi(_catalog.Sessions.Single(x => x.Id == metadata.Id)); RefreshInformationBar();
            view.Append($"[Unable to start {metadata.DisplayName}: {exception.Message}]\n");
        }
    }

    private async Task StopSessionAsync(SessionMetadata metadata)
    {
        try { await _coordinator.StopAsync(metadata.SessionKey); _diagnostics.Record("session", "stopped", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["sessionId"] = metadata.Id.ToString(), ["outcome"] = "disconnected" }); }
        catch (Exception exception) { RecordFailure("session", "stop-failed", exception, metadata.Id); _views[metadata.Id].Append($"\n[Stop failed: {exception.Message}]\n"); }
    }

    private async Task SendKeysToSessionAsync(SessionMetadata metadata, string data)
    {
        try { await _coordinator.WriteAsync(metadata.SessionKey, data); }
        catch (Exception) { /* Keystrokes are best-effort; a stopped or replaced session simply ignores them. */ }
    }

    private Task<TerminalInputAcceptance> AcceptInputAsync(TerminalSessionView view, SessionMetadata metadata, string input)
    {
        var activation = _activation.Current;
        try
        {
            var acceptance = _coordinator.SubmitUserInput(metadata.SessionKey, input + "\r");
            _ = ObserveAcceptedInputAsync(view, metadata, activation, acceptance.Completion);
            return Task.FromResult(TerminalInputAcceptance.AcceptedSubmission);
        }
        catch (Exception exception)
        {
            view.Append($"\n[Input rejected: {exception.Message}]\n");
            return Task.FromResult(TerminalInputAcceptance.RejectedSubmission);
        }
    }

    private async Task ObserveAcceptedInputAsync(TerminalSessionView view, SessionMetadata metadata, long activation, Task completion)
    {
        try
        {
            await completion;
            if (_activation.IsCurrent(activation) && metadata.Kind == AgentKind.Primary) await TryDeliverAlertAsync(metadata);
        }
        catch (Exception exception)
        {
            if (_activation.IsCurrent(activation)) view.Append($"\n[Input failed after acceptance: {exception.Message}]\n");
        }
    }

    private async Task ReplacePrimaryAsync(SessionMetadata metadata, LaunchProfile profile)
    {
        var activation = _activation.Current;
        var catalog = _catalog;
        if (_registry.Get(metadata.Id).Status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting)
            await StopSessionAsync(metadata);
        if (!_activation.IsCurrent(activation)) return;
        var updated = await catalog.UpdateLaunchProfileAsync(metadata.Id, profile);
        if (!_activation.IsCurrent(activation)) return;
        _views[metadata.Id].UpdateMetadata(updated);
        await StartSessionAsync(updated);
    }

    private void Coordinator_OutputReceived(object? sender, SessionOutput output)
    {
        var activation = _activation.Current;
        var metadata = _catalog.Sessions.FirstOrDefault(x => x.SessionKey == output.Key); if (metadata is null) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_activation.IsCurrent(activation)) return;
            if (!_coordinator.IsCurrentOwnership(output.Key, output.Generation)) return;
            _views[metadata.Id].Append(output.Text);
            if (_attention.RecordActivity(metadata.Id)) UpdateSessionUi(_catalog.Sessions.Single(x => x.Id == metadata.Id));
            if (metadata.Kind == AgentKind.Primary) await TryDeliverAlertAsync(metadata);
        });
    }

    private void Coordinator_StateChanged(object? sender, SessionStateChanged state)
    {
        var activation = _activation.Current;
        var metadata = _catalog.Sessions.FirstOrDefault(x => x.SessionKey == state.Key); if (metadata is null) return;
        _shutdownPersistence.Enqueue(async () =>
        {
            await _store.UpdateStatusAsync(metadata.Id, state.Status);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_activation.IsCurrent(activation)) return;
                if (!_catalog.Sessions.Any(x => x.Id == metadata.Id)) return;
                _catalog.ApplyPersistedStatus(metadata.Id, state.Status);
                UpdateSessionUi(_catalog.Sessions.Single(x => x.Id == metadata.Id));
                RefreshInformationBar();
            });
        }, exception => DispatcherQueue.TryEnqueue(() => { if (_activation.IsCurrent(activation) && _views.TryGetValue(metadata.Id, out var view)) view.Append($"\n[Unable to persist status: {exception.Message}]\n"); }));
    }

    private void Coordinator_LifecycleTransitioned(object? sender, LifecycleTransition transition)
    {
        var activation = _activation.Current;
        if (transition.Event is { } lifecycleEvent)
            _diagnostics.Record("lifecycle", "transitioned", lifecycleEvent.OccurredAt, new Dictionary<string, string> { ["sessionId"] = transition.Session.Id.ToString(), ["previousState"] = lifecycleEvent.PreviousStatus.ToString(), ["newState"] = lifecycleEvent.NewStatus.ToString() });
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_activation.IsCurrent(activation)) return;
            var notification = _failureNotifications.Evaluate(transition, _isWindowActive, _focusedSessionId);
            if (notification is not null) _windowsNotifications.Show(notification);
        });
        _shutdownPersistence.Enqueue(
            () => PersistLifecycleTransitionAsync(transition, activation),
            exception => DispatcherQueue.TryEnqueue(() =>
            {
                if (_activation.IsCurrent(activation) && _catalog.Sessions.FirstOrDefault(x => x.Id == transition.Session.Id) is { } session)
                    _views[session.Id].Append($"\n[Unable to persist lifecycle event: {exception.Message}]\n");
            }));
    }

    private async Task PersistLifecycleTransitionAsync(LifecycleTransition transition, long activation)
    {
        await _ledger.RecordAsync(transition);
        if (transition.Alert is { } alert)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_activation.IsCurrent(activation)) return;
                if (_catalog.SelectedPrimary?.Id == alert.ParentSessionId && _catalog.SelectedPrimary is { } parent)
                {
                    SchedulePendingAlerts(parent, transition.Event!.OccurredAt);
                }
            });
        }
    }

    private async Task TryDeliverAlertAsync(SessionMetadata parent)
    {
        var activation = _activation.Current;
        var alerts = _alerts;
        var eventIds = _currentBatch is { ParentSessionId: var parentId } batch && parentId == parent.Id
            ? batch.EventIds
            : Array.Empty<Guid>();
        var outcome = await _alertDelivery.TryDeliverAsync(
            parent.Id,
            parent.SessionKey,
            eventIds,
            alerts,
            (ids, token) => _ledger.ReserveAsync(ids, token),
            (ids, token) => _ledger.MarkWriteSucceededAsync(ids, token),
            (ids, token) => _ledger.CommitAsync(ids, token),
            (ids, token) => _ledger.ReleaseAsync(ids, token));
        if (!_activation.IsCurrent(activation) || !ReferenceEquals(alerts, _alerts)) return;
        _diagnostics.Record("alert", outcome.ToString(), DateTimeOffset.UtcNow, new Dictionary<string, string>
        {
            ["sessionId"] = parent.Id.ToString(),
            ["eventCount"] = eventIds.Count.ToString(),
        });
        if (outcome is AlertDeliveryOutcome.Delivered or AlertDeliveryOutcome.ReservationUnavailable or
            AlertDeliveryOutcome.ReservationIndeterminate or AlertDeliveryOutcome.WriteIndeterminate or
            AlertDeliveryOutcome.CommitIndeterminate or AlertDeliveryOutcome.CommitPending)
        {
            _currentBatch = null;
            await ActivateNextPreparedBatchAsync(parent);
        }
    }

    private async Task QueuePendingAlertsAsync(SessionMetadata parent)
    {
        var activation = _activation.Current;
        var batch = await _ledger.CreatePendingBatchAsync(parent.Id); if (batch is null) return;
        if (!_activation.IsCurrent(activation)) return;
        _preparedBatches.Enqueue(batch);
        await ActivateNextPreparedBatchAsync(parent);
    }

    private async Task QueuePendingAlertsAsync(SessionMetadata parent, DateTimeOffset occurredBeforeUtc)
    {
        var activation = _activation.Current;
        var assigned = _preparedBatches.SelectMany(item => item.EventIds)
            .Concat(_currentBatch?.EventIds ?? Array.Empty<Guid>())
            .ToArray();
        var batch = await _ledger.CreatePendingBatchAsync(parent.Id, occurredBeforeUtc, assigned); if (batch is null) return;
        if (!_activation.IsCurrent(activation)) return;
        _preparedBatches.Enqueue(batch);
        await ActivateNextPreparedBatchAsync(parent);
    }

    private async Task ActivateNextPreparedBatchAsync(SessionMetadata parent)
    {
        var activation = _activation.Current;
        if (_currentBatch is not null || _preparedBatches.Count == 0) return;
        _currentBatch = _preparedBatches.Dequeue();
        _alerts.RequestAlert(_currentBatch.ReservationId, _currentBatch.ParentSessionId, _currentBatch.Message);
        await TryDeliverAlertAsync(parent);
        if (!_activation.IsCurrent(activation)) return;
    }

    private void SchedulePendingAlerts(SessionMetadata parent, DateTimeOffset firstCompletionUtc)
    {
        _completionBatches.Schedule(parent.Id, firstCompletionUtc);
    }

    private Task CompletionBatchDueAsync(long activation, Guid parentSessionId, DateTimeOffset _, DateTimeOffset deadlineUtc)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_activation.IsCurrent(activation)) return;
            if (_catalog.Sessions.FirstOrDefault(item => item.Id == parentSessionId) is not { } parent) return;
            try { await QueuePendingAlertsAsync(parent, deadlineUtc); }
            catch (Exception exception) { if (_activation.IsCurrent(activation) && _views.TryGetValue(parent.Id, out var view)) view.Append($"\n[Unable to prepare completion batch: {exception.Message}]\n"); }
        });
        return Task.CompletedTask;
    }

    private CompletionBatchScheduler CreateCompletionBatchScheduler(long activation) =>
        new(new SystemCompletionBatchTimer(), (parentId, start, deadline) => CompletionBatchDueAsync(activation, parentId, start, deadline));

    private async void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;
        await SelectSessionAsync(id);
    }

    private async void SubAgentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || SubAgentTabs.SelectedItem is not TabViewItem { Tag: Guid id } || !_initialized) return;
        await SelectSessionAsync(id);
    }

    private async Task SelectSessionAsync(Guid id)
    {
        var activation = _activation.Current;
        var catalog = _catalog;
        var session = catalog.Sessions.Single(x => x.Id == id);
        if (session.Kind == AgentKind.Primary) await catalog.SelectPrimaryAsync(id);
        else await catalog.SelectSubAgentAsync(id);
        if (!_activation.IsCurrent(activation)) return;
        ApplySelection();
        SetFocusedSession(id);
        _views[id].FocusInput();
    }

    private void SetFocusedSession(Guid id)
    {
        _focusedSessionId = id;
        if (_attention.Focus(id) && _catalog.Sessions.FirstOrDefault(x => x.Id == id) is { } session) UpdateSessionUi(session);
    }

    private async Task FocusTargetAsync(WorkspaceFocusTarget target)
    {
        var id = WorkspaceNavigation.SelectTarget(target, _catalog.Workspace.SelectedPrimarySessionId, _catalog.Workspace.SelectedSubAgentSessionId);
        if (id is Guid selectedId) await SelectSessionAsync(selectedId);
    }

    private async Task MoveSubAgentAsync(int offset)
    {
        var ordered = _catalog.SubAgentSessions.OrderBy(x => x.DisplayOrder).Select(x => x.Id).ToArray();
        var id = WorkspaceNavigation.Move(ordered, _catalog.Workspace.SelectedSubAgentSessionId, offset);
        if (id is Guid selectedId) await SelectSessionAsync(selectedId);
    }

    private async Task MoveSidebarAsync(int offset)
    {
        var ordered = WorkspaceNavigation.SidebarOrder(_catalog.Sessions);
        var currentId = _focusedSessionId ?? _catalog.Workspace.SelectedPrimarySessionId ?? _catalog.Workspace.SelectedSubAgentSessionId;
        var id = WorkspaceNavigation.Move(ordered, currentId, offset);
        if (id is Guid selectedId) await SelectSessionAsync(selectedId);
    }

    private async void FocusPrimary_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await FocusTargetAsync(WorkspaceFocusTarget.Primary); }
    private async void FocusSubAgent_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await FocusTargetAsync(WorkspaceFocusTarget.SubAgent); }
    private async void NextSubAgent_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await MoveSubAgentAsync(1); }
    private async void PreviousSubAgent_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await MoveSubAgentAsync(-1); }
    private async void PreviousSidebarSession_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await MoveSidebarAsync(-1); }
    private async void NextSidebarSession_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await MoveSidebarAsync(1); }
    private async void AddSubAgent_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await ShowAddSubAgentDialogAsync(); }
    private void OpenTerminalSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; FocusedTerminalView()?.OpenSearch(); }
    private void NextSearchMatch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { var view = FocusedTerminalView(); if (view?.IsSearchOpen != true) return; args.Handled = true; view.MoveToNextMatch(); }
    private void PreviousSearchMatch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { var view = FocusedTerminalView(); if (view?.IsSearchOpen != true) return; args.Handled = true; view.MoveToPreviousMatch(); }

    private TerminalSessionView? FocusedTerminalView()
    {
        var id = _focusedSessionId ?? _catalog.Workspace.SelectedSubAgentSessionId ?? _catalog.Workspace.SelectedPrimarySessionId;
        return id is Guid sessionId && _views.TryGetValue(sessionId, out var view) ? view : null;
    }

    private void ApplySelection()
    {
        _syncingSelection = true;
        try
        {
            if (_catalog.SelectedPrimary is { } primary) { MainSessionTitle.Text = primary.DisplayName; PrimaryTerminalHost.Children.Clear(); PrimaryTerminalHost.Children.Add(_views[primary.Id]); }
            if (_catalog.SelectedSubAgent is { } sub && _tabs.TryGetValue(sub.Id, out var tab)) SubAgentTabs.SelectedItem = tab;
            foreach (var pair in _sidebarButtons) pair.Value.Background = ThemeBrush(pair.Key == _catalog.Workspace.SelectedPrimarySessionId || pair.Key == _catalog.Workspace.SelectedSubAgentSessionId ? "SelectedBackgroundBrush" : "TransparentBrush");
        }
        finally { _syncingSelection = false; }
    }

    private async void AddSubAgent_Click(object sender, RoutedEventArgs e)
    {
        await ShowAddSubAgentDialogAsync();
    }

    private async Task ShowAddSubAgentDialogAsync()
    {
        var name = new TextBox { Header = "Display name" }; var profile = new ComboBox { Header = "Launch profile", ItemsSource = new[] { "PowerShell", "Codex" }, SelectedIndex = 0 };
        var error = new TextBlock { Foreground = ThemeBrush("ErrorBrush") };
        var panel = new StackPanel { Spacing = 8 }; panel.Children.Add(name); panel.Children.Add(profile); panel.Children.Add(error);
        var dialog = new ContentDialog { Title = "Add sub-agent", Content = panel, PrimaryButtonText = "Add", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        while (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                var created = await _catalog.CreateSubAgentAsync(name.Text, profile.SelectedIndex == 1 ? LaunchProfile.Codex : LaunchProfile.PowerShell);
                _registry.Register(new AgentSession(created.Id, created.DisplayName, created.Kind, _catalog.GetParentSessionId(created.Id))); AddSessionUi(created); ApplySelection(); await StartSessionAsync(created); return;
            }
            catch (Exception exception) { error.Text = exception.Message; }
        }
    }

    private void UpdateSessionUi(SessionMetadata session)
    {
        _views[session.Id].SetStatus(session.LastStatus);
        _sidebarButtons[session.Id].Content = BuildSidebarRow(session);
        if (_tabs.TryGetValue(session.Id, out var tab)) tab.Header = BuildTabHeader(session);
    }

    private UIElement BuildSidebarRow(SessionMetadata session)
    {
        var statusBrush = ThemeBrush(StatusPresentation.ColorKey(session.LastStatus));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new TextBlock { Text = StatusPresentation.Glyph(session.LastStatus), Foreground = statusBrush, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(glyph, 0);
        var name = new TextBlock { Text = session.DisplayName, Foreground = ThemeBrush("SidebarTextBrush"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(name);
        if (_attention.IsUnread(session.Id))
        {
            var unread = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = ThemeBrush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(unread, 2);
            grid.Children.Add(unread);
        }
        return grid;
    }

    private UIElement BuildTabHeader(SessionMetadata session)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = session.DisplayName, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = StatusPresentation.Glyph(session.LastStatus), Foreground = ThemeBrush(StatusPresentation.ColorKey(session.LastStatus)), VerticalAlignment = VerticalAlignment.Center });
        if (_attention.IsUnread(session.Id))
            panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = ThemeBrush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void RefreshInformationBar()
    {
        var counts = _catalog.Sessions.GroupBy(x => x.LastStatus).ToDictionary(x => x.Key, x => x.Count());
        RunningCountText.Text = $"RUNNING {counts.GetValueOrDefault(AgentStatus.Running)}"; WaitingCountText.Text = $"WAITING {counts.GetValueOrDefault(AgentStatus.Waiting)}"; CompletedCountText.Text = $"COMPLETE {counts.GetValueOrDefault(AgentStatus.Completed)}";
        ConnectionText.Text = ConnectionPresentation.IsConnected(_catalog.Sessions.Select(session => session.LastStatus)) ? "● CONNECTED" : "! DISCONNECTED";
    }

    private async Task RefreshWorkspaceSelectorAsync()
    {
        var items = WorkspaceSelectionPolicy.Create(await _store.LoadWorkspacesAsync(), _catalog.Workspace.Id);
        _syncingWorkspaceSelector = true;
        try
        {
            WorkspaceSelector.Items.Clear();
            foreach (var item in items)
            {
                var option = new ComboBoxItem
                {
                    Tag = item.Id,
                    Content = item.IsAvailable ? item.DisplayText : $"{item.DisplayText} (unavailable)",
                    IsEnabled = item.IsAvailable,
                };
                ToolTipService.SetToolTip(option, item.WorkingDirectory);
                WorkspaceSelector.Items.Add(option);
                if (item.IsActive) WorkspaceSelector.SelectedItem = option;
            }
        }
        finally { _syncingWorkspaceSelector = false; }
    }

    private async void WorkspaceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingWorkspaceSelector || !_initialized || _workspaceSwitch is null ||
            WorkspaceSelector.SelectedItem is not ComboBoxItem { Tag: Guid targetId, IsEnabled: true }) return;
        if (targetId == _catalog.Workspace.Id) { RestoreWorkspaceFocus(_focusedSessionId); return; }

        WorkspaceSelector.IsEnabled = false;
        var previousFocus = _focusedSessionId;
        try
        {
            var outcome = await _workspaceSwitch.SwitchAsync(targetId);
            if (outcome is WorkspaceSwitchOutcome.Cancelled or WorkspaceSwitchOutcome.ActiveWorkspace)
                RestoreWorkspaceFocus(previousFocus);
        }
        catch (Exception exception)
        {
            RecordFailure("workspace", "switch-failed", exception);
            ConnectionText.Text = $"! WORKSPACE SWITCH FAILED: {exception.Message}";
            RestoreWorkspaceFocus(previousFocus);
        }
        finally
        {
            await RefreshWorkspaceSelectorAsync();
            WorkspaceSelector.IsEnabled = true;
        }
    }

    private void RestoreWorkspaceFocus(Guid? sessionId)
    {
        if (sessionId is Guid id && _views.TryGetValue(id, out var view)) view.RestoreFocus();
    }

    Guid IWorkspaceSwitchAdapter.ActiveWorkspaceId => _catalog.Workspace.Id;
    bool IWorkspaceSwitchAdapter.OwnsAnySession => _coordinator.OwnsAnySession;
    IReadOnlyCollection<string> IWorkspaceSwitchAdapter.PartialInputs => _views.Values.Select(view => view.PartialInput).ToArray();

    async Task<bool> IWorkspaceSwitchAdapter.ConfirmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ContentDialog
        {
            Title = "Switch workspace?",
            Content = "Switching stops active sessions and discards all unsent partial input.",
            PrimaryButtonText = "Switch",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    Task IWorkspaceSwitchAdapter.StopOwnedSessionsAsync(CancellationToken cancellationToken) =>
        _coordinator.StopAllAsync(cancellationToken);

    Task IWorkspaceSwitchAdapter.FlushPersistenceAsync() => _shutdownPersistence.FlushAsync();

    void IWorkspaceSwitchAdapter.InvalidateActivation()
    {
        var generation = _activation.Advance();
        _completionBatches.Dispose();
        _completionBatches = CreateCompletionBatchScheduler(generation);
        _currentBatch = null;
        _preparedBatches.Clear();
        _alerts = new AlertInputCoordinator(RuntimeReadinessAdapterSelection.Create(RuntimeLaunchProfile.PowerShell));
    }

    Task IWorkspaceSwitchAdapter.ActivatePresentationAsync(WorkspaceCatalog target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _catalog = target;
        _registry.Clear();
        _attention.Clear();
        _focusedSessionId = null;
        PrimaryTerminalHost.Children.Clear();
        PrimaryList.Children.Clear();
        SubAgentList.Children.Clear();
        SubAgentTabs.TabItems.Clear();
        _views.Clear();
        _sidebarButtons.Clear();
        _tabs.Clear();
        foreach (var session in _catalog.Sessions.OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder))
            _registry.Register(new AgentSession(session.Id, session.DisplayName, session.Kind, _catalog.GetParentSessionId(session.Id), session.LastStatus));
        BuildWorkspaceUi();
        RefreshInformationBar();
        ApplyPanelLayout();
        return Task.CompletedTask;
    }

    async Task IWorkspaceSwitchAdapter.StartSelectedSessionsAsync(WorkspaceCatalog target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.SelectedPrimary is { } primary) await StartSessionAsync(primary);
        if (target.SelectedSubAgent is { } subAgent) await StartSessionAsync(subAgent);
    }

    private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var resized = PanelSizing.Resize(SidebarColumn.ActualWidth, PrimaryColumn.ActualWidth, e.HorizontalChange, PanelSizing.MinimumSidebarWidth, PanelSizing.MaximumSidebarWidth, PanelSizing.MinimumPrimaryWidth);
        _preferredSidebarWidth = resized.Leading;
        ApplyPanelLayout();
    }

    private void SubAgentSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var resized = PanelSizing.Resize(PrimaryColumn.ActualWidth, SubAgentColumn.ActualWidth, e.HorizontalChange, PanelSizing.MinimumPrimaryWidth, double.MaxValue, PanelSizing.MinimumDetailWidth);
        _preferredSubAgentWidth = resized.Trailing;
        ApplyPanelLayout();
    }

    private void SidebarSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { _preferredSidebarWidth = PanelSizing.DefaultSidebarWidth; ApplyPanelLayout(); }
    private void SubAgentSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { _preferredSubAgentWidth = PanelSizing.DefaultDetailWidth; ApplyPanelLayout(); }

    private void WorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyPanelLayout();

    private void ApplyPanelLayout()
    {
        if (_applyingPanelLayout) return;
        _applyingPanelLayout = true;
        try
        {
            var widths = PanelSizing.CalculateWorkspace(Math.Max(0, WorkspaceGrid.ActualWidth - 10), _preferredSidebarWidth, _preferredSubAgentWidth);
            SidebarColumn.Width = new GridLength(widths.Sidebar);
            PrimaryColumn.Width = new GridLength(widths.Primary);
            SubAgentColumn.Width = new GridLength(widths.Detail);
        }
        finally { _applyingPanelLayout = false; }
    }

    private static SolidColorBrush ThemeBrush(string key) => (SolidColorBrush)Application.Current.Resources[key];

    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halfway", "diagnostics", "halfway-diagnostics.json");
        try
        {
            _diagnostics.Record("diagnostics", "export-requested", DateTimeOffset.UtcNow);
            await _diagnosticExporter.ExportAsync(path, _diagnostics.Snapshot());
            ConnectionText.Text = $"EXPORTED {path}";
        }
        catch (Exception exception)
        {
            RecordFailure("diagnostics", "export-failed", exception);
            ConnectionText.Text = "! DIAGNOSTIC EXPORT FAILED";
        }
    }

    private void RecordFailure(string category, string name, Exception exception, Guid? sessionId = null)
    {
        var facts = new Dictionary<string, string> { ["errorType"] = exception.GetType().Name, ["errorMessage"] = exception.Message };
        if (sessionId is Guid id) facts["sessionId"] = id.ToString();
        _diagnostics.Record(category, name, DateTimeOffset.UtcNow, facts);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _completionBatches.Dispose();
        _windowsNotifications.Dispose();
        try
        {
            _shutdownPersistence.CompleteAsync(
                () => _coordinator.DisposeAsync(),
                async () =>
                {
                    if (_applicationRun is { } run && !await _store.CompleteApplicationRunAsync(run.CurrentRun.Id, DateTimeOffset.UtcNow))
                        throw new InvalidOperationException("The application run could not be marked clean.");
                    _diagnostics.Record("application", "shutdown", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["outcome"] = "orderly" });
                },
                () => _store.DisposeAsync()).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            RecordFailure("application", "shutdown-failed", exception);
        }
    }

    private static string ReadBranch(string path)
    {
        try { var head=Path.Combine(path,".git","HEAD");if(!File.Exists(head))return "—";var value=File.ReadAllText(head).Trim();const string prefix="ref: refs/heads/";return value.StartsWith(prefix,StringComparison.Ordinal)?value[prefix.Length..]:value.Length>=7?value[..7]:"—"; } catch { return "—"; }
    }
}
