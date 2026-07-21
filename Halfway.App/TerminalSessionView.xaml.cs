using System.Text;
using Halfway.Core;
using Halfway.Core.Vt;
using Halfway.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Halfway.App;

public sealed partial class TerminalSessionView : UserControl
{
    private const double TerminalFontSize = 14;
    private const int MaxRenderedLines = 500;
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    private readonly TerminalEmulator _emulator = new(80, 24);
    private double _cellWidth = 8.4;
    private double _cellHeight = 18;
    private bool _metricsMeasured;
    private bool _renderScheduled;
    private string _snapshotText = string.Empty;
    private int _snapshotColumns = 80;

    private IReadOnlyList<int> _searchMatches = [];
    private int _currentSearchMatch = -1;
    private bool _inputEnabled;

    public TerminalSessionView(SessionMetadata metadata)
    {
        InitializeComponent();
        Metadata = metadata;
        TitleText.Text = metadata.DisplayName;
        if (metadata.Kind == AgentKind.Primary) { PowerShellButton.Visibility = Visibility.Visible; CodexButton.Visibility = Visibility.Visible; DemoAlertButton.Visibility = Visibility.Visible; }
        SetStatus(metadata.LastStatus);
        RenderTerminal();
    }

    public SessionMetadata Metadata { get; private set; }
    public string PartialInput => string.Empty;
    public bool IsSearchOpen => SearchPanel.Visibility == Visibility.Visible;
    public Func<string, Task<TerminalInputAcceptance>>? SubmitInputAsync { get; set; }
    public Func<string, Task>? SendKeysAsync { get; set; }
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? PowerShellRequested;
    public event EventHandler? CodexRequested;
    public event EventHandler? DemoAlertRequested;
    public event EventHandler<TerminalSize>? ResizeRequested;

    public void SetStatus(AgentStatus status) { var brush=ThemeBrush(StatusPresentation.ColorKey(status));StatusText.Text=status.ToString().ToUpperInvariant();StatusText.Foreground=brush;StatusDot.Fill=brush;var active=status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting; StartButton.IsEnabled=!active; StartButton.Content=status == AgentStatus.Disconnected ? "Restart" : "Start"; StopButton.IsEnabled=active; _inputEnabled=status is AgentStatus.Running or AgentStatus.Waiting; }
    public void UpdateMetadata(SessionMetadata metadata) { if(metadata.Id!=Metadata.Id)throw new ArgumentException("Session identity cannot change.",nameof(metadata));Metadata=metadata;TitleText.Text=metadata.DisplayName;SetStatus(metadata.LastStatus); }

    // Feed raw terminal output into the VT emulator, then coalesce redraws so a burst of output
    // paints once. The emulator owns the 2-D screen; this view only renders its current snapshot.
    public void Append(string output)
    {
        _emulator.Process(output);
        ScheduleRender();
    }

    public void ClearOutput() { _emulator.Reset(); RenderTerminal(); }
    public void FocusInput() => OutputScroll.Focus(FocusState.Programmatic);
    public void RestoreFocus() { if (IsSearchOpen) SearchText.Focus(FocusState.Programmatic); else FocusInput(); }
    public void OpenSearch() { SearchPanel.Visibility = Visibility.Visible; _currentSearchMatch = 0; RenderTerminal(); SearchText.Focus(FocusState.Programmatic); SearchText.SelectAll(); }
    public void MoveToNextMatch() => MoveSearch(1);
    public void MoveToPreviousMatch() => MoveSearch(-1);

    private void CloseSearch() { SearchPanel.Visibility = Visibility.Collapsed; RenderTerminal(); FocusInput(); }
    private void RefreshSearch() { _currentSearchMatch = 0; RenderTerminal(); }
    private void MoveSearch(int offset) { if (_searchMatches.Count == 0) return; _currentSearchMatch = TerminalSearch.Move(_searchMatches.Count, _currentSearchMatch, offset); RenderTerminal(); }

    private void ScheduleRender()
    {
        if (_renderScheduled) return;
        _renderScheduled = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _renderScheduled = false; RenderTerminal(); });
    }

    private void RenderTerminal()
    {
        EnsureCellMetrics();
        var snapshot = _emulator.Snapshot();
        var lines = snapshot.Lines;
        var total = lines.Count;
        var start = Math.Max(0, total - MaxRenderedLines);
        var columns = snapshot.Columns;
        _snapshotColumns = columns;

        var baseFg = ThemeBrush("PrimaryTextBrush").Color;
        var baseBg = ThemeBrush("TerminalBackgroundBrush").Color;

        var text = new StringBuilder();
        var groups = new Dictionary<(uint Fg, uint Bg), TextHighlighter>();
        var index = 0;
        for (var r = start; r < total; r++)
        {
            var cells = lines[r];
            var runStart = -1;
            (uint Fg, uint Bg) runKey = default;
            for (var c = 0; c < columns; c++)
            {
                var cell = c < cells.Length ? cells[c] : VtCell.Blank;
                var glyph = cell.Glyph;
                text.Append(glyph < ' ' ? ' ' : glyph);
                var (fg, bg, isDefault) = Effective(cell, baseFg, baseBg);
                if (isDefault)
                {
                    if (runStart >= 0) { AddRange(groups, runKey, runStart, index - runStart); runStart = -1; }
                }
                else if (runStart >= 0 && runKey.Fg == fg && runKey.Bg == bg)
                {
                    // extend current run
                }
                else
                {
                    if (runStart >= 0) AddRange(groups, runKey, runStart, index - runStart);
                    runStart = index;
                    runKey = (fg, bg);
                }
                index++;
            }
            if (runStart >= 0) AddRange(groups, runKey, runStart, index - runStart);
            if (r < total - 1) { text.Append('\n'); index++; }
        }

        _snapshotText = text.ToString();
        OutputText.Foreground = new SolidColorBrush(baseFg);
        OutputText.Text = _snapshotText;
        OutputText.TextHighlighters.Clear();
        foreach (var highlighter in groups.Values) OutputText.TextHighlighters.Add(highlighter);

        ApplySearchHighlights();

        // The cursor is a highlight on its actual character cell, so the text engine positions it
        // exactly — no pixel math that could drift from the rendered glyph rows.
        if (snapshot.CursorVisible)
        {
            var cursorRow = snapshot.CursorRow - start;
            if (cursorRow >= 0)
            {
                var cursorIndex = cursorRow * (columns + 1) + snapshot.CursorColumn;
                if (cursorIndex >= 0 && cursorIndex < _snapshotText.Length && _snapshotText[cursorIndex] != '\n')
                {
                    var cursor = new TextHighlighter { Background = new SolidColorBrush(baseFg), Foreground = new SolidColorBrush(baseBg) };
                    cursor.Ranges.Add(new TextRange { StartIndex = cursorIndex, Length = 1 });
                    OutputText.TextHighlighters.Add(cursor);
                }
            }
        }

        if (IsSearchOpen && _currentSearchMatch >= 0 && _currentSearchMatch < _searchMatches.Count)
            ScrollToIndex(_searchMatches[_currentSearchMatch]);
        else if (!_emulator.IsAlternateScreen)
        {
            OutputScroll.UpdateLayout();
            OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true);
        }
    }

    private void ApplySearchHighlights()
    {
        if (!IsSearchOpen || SearchText.Text.Length == 0)
        {
            _searchMatches = [];
            SearchResultText.Text = string.Empty;
            return;
        }
        _searchMatches = TerminalSearch.FindMatches(_snapshotText, SearchText.Text);
        if (_searchMatches.Count == 0) { _currentSearchMatch = -1; SearchResultText.Text = "No matches"; return; }
        _currentSearchMatch = Math.Clamp(_currentSearchMatch, 0, _searchMatches.Count - 1);
        var all = new TextHighlighter { Background = ThemeBrush("SearchMatchBrush") };
        foreach (var s in _searchMatches) all.Ranges.Add(new TextRange { StartIndex = s, Length = SearchText.Text.Length });
        OutputText.TextHighlighters.Add(all);
        var current = new TextHighlighter { Background = ThemeBrush("SearchCurrentBrush") };
        current.Ranges.Add(new TextRange { StartIndex = _searchMatches[_currentSearchMatch], Length = SearchText.Text.Length });
        OutputText.TextHighlighters.Add(current);
        SearchResultText.Text = $"{_currentSearchMatch + 1} of {_searchMatches.Count}";
    }

    private void ScrollToIndex(int charIndex)
    {
        var row = charIndex / (_snapshotColumns + 1);
        OutputScroll.UpdateLayout();
        OutputScroll.ChangeView(null, Math.Max(0, row * _cellHeight - _cellHeight * 3), null, true);
    }

    private void OutputScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnsureCellMetrics();
        var w = OutputScroll.ViewportWidth > 0 ? OutputScroll.ViewportWidth : e.NewSize.Width - 16;
        var h = OutputScroll.ViewportHeight > 0 ? OutputScroll.ViewportHeight : e.NewSize.Height - 4;
        if (w <= 0 || h <= 0) return;
        var cols = Math.Clamp((int)(w / _cellWidth), 20, 400);
        var rows = Math.Clamp((int)(h / _cellHeight), 5, 200);
        if (cols == _emulator.Columns && rows == _emulator.Rows) return;
        _emulator.Resize(cols, rows);
        ResizeRequested?.Invoke(this, new TerminalSize((short)cols, (short)rows));
        ScheduleRender();
    }

    private void EnsureCellMetrics()
    {
        if (_metricsMeasured) return;
        var probe = new TextBlock { FontFamily = MonoFont, FontSize = TerminalFontSize, Text = new string('M', 20) };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = probe.DesiredSize.Width / 20.0;
        var h = probe.DesiredSize.Height;
        if (w > 1 && h > 1) { _cellWidth = w; _cellHeight = h; _metricsMeasured = true; }
    }

    private static void AddRange(Dictionary<(uint Fg, uint Bg), TextHighlighter> groups, (uint Fg, uint Bg) key, int start, int length)
    {
        if (!groups.TryGetValue(key, out var highlighter))
        {
            highlighter = new TextHighlighter { Foreground = new SolidColorBrush(FromArgb(key.Fg)), Background = new SolidColorBrush(FromArgb(key.Bg)) };
            groups[key] = highlighter;
        }
        highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = length });
    }

    private static (uint Fg, uint Bg, bool IsDefault) Effective(VtCell cell, Color baseFg, Color baseBg)
    {
        var fg = cell.Foreground;
        var bg = cell.Background;
        var bold = (cell.Attributes & VtCellAttributes.Bold) != 0;
        if (bold && fg.Kind == VtColorKind.Indexed && fg.Index < 8) fg = VtColor.Indexed(fg.Index + 8);

        var fgDefault = fg.Kind == VtColorKind.Default;
        var bgDefault = bg.Kind == VtColorKind.Default;
        var fgColor = fgDefault ? baseFg : MapColor(fg);
        var bgColor = bgDefault ? baseBg : MapColor(bg);

        if ((cell.Attributes & VtCellAttributes.Reverse) != 0)
        {
            (fgColor, bgColor) = (bgColor, fgColor);
            (fgDefault, bgDefault) = (bgDefault, fgDefault);
        }

        return (ToArgb(fgColor), ToArgb(bgColor), fgDefault && bgDefault);
    }

    private static uint ToArgb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    private static Color FromArgb(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private static Color MapColor(VtColor color) => color.Kind switch
    {
        VtColorKind.Rgb => Color.FromArgb(255, color.R, color.G, color.B),
        VtColorKind.Indexed => PaletteColor(color.Index),
        _ => Color.FromArgb(255, 204, 204, 204),
    };

    private static readonly Color[] Ansi16 =
    {
        Color.FromArgb(255, 0x0C, 0x0C, 0x0C), Color.FromArgb(255, 0xC5, 0x0F, 0x1F),
        Color.FromArgb(255, 0x13, 0xA1, 0x0E), Color.FromArgb(255, 0xC1, 0x9C, 0x00),
        Color.FromArgb(255, 0x00, 0x37, 0xDA), Color.FromArgb(255, 0x88, 0x17, 0x98),
        Color.FromArgb(255, 0x3A, 0x96, 0xDD), Color.FromArgb(255, 0xCC, 0xCC, 0xCC),
        Color.FromArgb(255, 0x76, 0x76, 0x76), Color.FromArgb(255, 0xE7, 0x48, 0x56),
        Color.FromArgb(255, 0x16, 0xC6, 0x0C), Color.FromArgb(255, 0xF9, 0xF1, 0xA5),
        Color.FromArgb(255, 0x3B, 0x78, 0xFF), Color.FromArgb(255, 0xB4, 0x00, 0x9E),
        Color.FromArgb(255, 0x61, 0xD6, 0xD6), Color.FromArgb(255, 0xF2, 0xF2, 0xF2),
    };

    private static Color PaletteColor(int index)
    {
        if (index < 16) return Ansi16[Math.Clamp(index, 0, 15)];
        if (index <= 231)
        {
            var n = index - 16;
            var r = n / 36; var g = n / 6 % 6; var b = n % 6;
            return Color.FromArgb(255, Cube(r), Cube(g), Cube(b));
        }
        var level = (byte)Math.Clamp(8 + (index - 232) * 10, 0, 255);
        return Color.FromArgb(255, level, level, level);
    }

    private static byte Cube(int v) => (byte)(v == 0 ? 0 : 55 + 40 * v);

    private void StartButton_Click(object sender,RoutedEventArgs e)=>StartRequested?.Invoke(this,EventArgs.Empty);
    private void StopButton_Click(object sender,RoutedEventArgs e)=>StopRequested?.Invoke(this,EventArgs.Empty);
    private void ClearButton_Click(object sender,RoutedEventArgs e){ClearOutput();FocusInput();}
    // Clicking the terminal focuses it so keystrokes flow to the shell (the emulator's cursor on the
    // active line is the caret).
    private void Terminal_PointerPressed(object sender,PointerRoutedEventArgs e)=>OutputScroll.Focus(FocusState.Programmatic);
    private void PowerShellButton_Click(object sender,RoutedEventArgs e)=>PowerShellRequested?.Invoke(this,EventArgs.Empty);
    private void CodexButton_Click(object sender,RoutedEventArgs e)=>CodexRequested?.Invoke(this,EventArgs.Empty);
    private void DemoAlertButton_Click(object sender,RoutedEventArgs e)=>DemoAlertRequested?.Invoke(this,EventArgs.Empty);

    // Characters (printable, Enter, Backspace, Escape, Ctrl-combos) arrive already resolved for the
    // keyboard layout via CharacterReceived and are forwarded straight to the shell.
    private async void Terminal_CharacterReceived(UIElement sender,CharacterReceivedRoutedEventArgs args)
    {
        if(!_inputEnabled)return;
        var c=args.Character;
        if(c=='\0'||c=='\t'||c==' ')return; // Tab and Space are handled in KeyDown to also suppress scroll/focus
        args.Handled=true;
        var text=c=='\b'?"\x7f":c.ToString(); // Backspace key -> DEL, as terminals expect
        if(SendKeysAsync is {} send)await send(text);
    }

    // Tab, Space and the navigation keys are forwarded here; marking them handled also stops the
    // ScrollViewer from scrolling or moving keyboard focus off the terminal.
    private async void Terminal_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(!_inputEnabled)return;
        var sequence=TranslateNavKey(e.Key);
        if(sequence is null)return;
        e.Handled=true;
        if(SendKeysAsync is {} send)await send(sequence);
    }

    private static string? TranslateNavKey(VirtualKey key)=>key switch
    {
        VirtualKey.Tab=>"\t",
        VirtualKey.Space=>" ",
        VirtualKey.Up=>"\x1b[A",
        VirtualKey.Down=>"\x1b[B",
        VirtualKey.Right=>"\x1b[C",
        VirtualKey.Left=>"\x1b[D",
        VirtualKey.Home=>"\x1b[H",
        VirtualKey.End=>"\x1b[F",
        VirtualKey.Delete=>"\x1b[3~",
        VirtualKey.PageUp=>"\x1b[5~",
        VirtualKey.PageDown=>"\x1b[6~",
        _=>null,
    };

    private void SearchText_TextChanged(object sender,TextChangedEventArgs e)=>RefreshSearch();
    private void SearchText_KeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;MoveToNextMatch();}else if(e.Key==VirtualKey.Escape){e.Handled=true;CloseSearch();}}
    private void PreviousSearchButton_Click(object sender,RoutedEventArgs e)=>MoveToPreviousMatch();
    private void NextSearchButton_Click(object sender,RoutedEventArgs e)=>MoveToNextMatch();
    private void CloseSearchButton_Click(object sender,RoutedEventArgs e)=>CloseSearch();

    private static SolidColorBrush ThemeBrush(string key)=>(SolidColorBrush)Application.Current.Resources[key];
}
