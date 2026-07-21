using System.Text.RegularExpressions;
using Halfway.Core;
using Halfway.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Halfway.App;

public sealed partial class TerminalSessionView : UserControl
{
    private const int MaximumOutputCharacters = 64 * 1024;
    private static readonly Regex AnsiEscapePattern = new("\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);
    private static readonly Regex ScreenClearPattern = new("\\x1B(?:c|\\[[23]J|\\[\\?1049[hl])", RegexOptions.Compiled);
    private static readonly Regex BlankLineRunPattern = new("\\n{3,}", RegexOptions.Compiled);
    private IReadOnlyList<int> _searchMatches = [];
    private int _currentSearchMatch = -1;
    private bool _relayingInput;
    public TerminalSessionView(SessionMetadata metadata) { InitializeComponent(); Metadata = metadata; TitleText.Text = metadata.DisplayName; if(metadata.Kind==AgentKind.Primary){PowerShellButton.Visibility=Visibility.Visible;CodexButton.Visibility=Visibility.Visible;DemoAlertButton.Visibility=Visibility.Visible;} SetStatus(metadata.LastStatus); }
    public SessionMetadata Metadata { get; private set; }
    public string PartialInput => InputText.Text;
    public bool IsSearchOpen => SearchPanel.Visibility == Visibility.Visible;
    public Func<string, Task<TerminalInputAcceptance>>? SubmitInputAsync { get; set; }
    public Func<string, Task>? SendKeysAsync { get; set; }
    public event EventHandler? PartialInputChanged;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? PowerShellRequested;
    public event EventHandler? CodexRequested;
    public event EventHandler? DemoAlertRequested;
    public event EventHandler<TerminalSize>? ResizeRequested;
    public void SetStatus(AgentStatus status) { var brush=ThemeBrush(StatusPresentation.ColorKey(status));StatusText.Text=status.ToString().ToUpperInvariant();StatusText.Foreground=brush;StatusDot.Fill=brush;var active=status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting; StartButton.IsEnabled=!active; StartButton.Content=status == AgentStatus.Disconnected ? "Restart" : "Start"; StopButton.IsEnabled=active; InputText.IsReadOnly=status is not (AgentStatus.Running or AgentStatus.Waiting); }
    public void UpdateMetadata(SessionMetadata metadata) { if(metadata.Id!=Metadata.Id)throw new ArgumentException("Session identity cannot change.",nameof(metadata));Metadata=metadata;TitleText.Text=metadata.DisplayName;SetStatus(metadata.LastStatus); }
    public void Append(string output)
    {
        // A full-screen clear or alternate-screen switch means the shell is repainting, so reset the
        // view to that point instead of stacking every frame. (This is a bounded view, not an emulator.)
        var clears = ScreenClearPattern.Matches(output);
        if (clears.Count > 0)
        {
            OutputText.Text = string.Empty;
            var last = clears[^1];
            output = output[(last.Index + last.Length)..];
        }
        var plain = AnsiEscapePattern.Replace(output, string.Empty);
        if (plain.Length == 0)
        {
            if (clears.Count > 0 && !IsSearchOpen) { OutputScroll.UpdateLayout(); OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true); }
            else if (clears.Count > 0) RefreshSearch();
            return;
        }
        var marked = string.IsNullOrWhiteSpace(plain) ? plain : "- " + plain;
        var combined = BlankLineRunPattern.Replace(OutputText.Text + marked, "\n\n");
        OutputText.Text = combined.Length <= MaximumOutputCharacters ? combined : combined[^MaximumOutputCharacters..];
        if (IsSearchOpen) RefreshSearch();
        else { OutputScroll.UpdateLayout(); OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true); }
    }
    public void ClearOutput() { OutputText.Text=string.Empty; RefreshSearch(); }
    public void FocusInput()=>InputText.Focus(FocusState.Programmatic);
    public void RestoreFocus() { if(IsSearchOpen) SearchText.Focus(FocusState.Programmatic); else FocusInput(); }
    public void OpenSearch() { SearchPanel.Visibility=Visibility.Visible;RefreshSearch();SearchText.Focus(FocusState.Programmatic);SearchText.SelectAll(); }
    public void MoveToNextMatch()=>MoveSearch(1);
    public void MoveToPreviousMatch()=>MoveSearch(-1);
    private void CloseSearch() { SearchPanel.Visibility=Visibility.Collapsed;OutputText.TextHighlighters.Clear();FocusInput(); }
    private void RefreshSearch() { _searchMatches=TerminalSearch.FindMatches(OutputText.Text,SearchText.Text);_currentSearchMatch=TerminalSearch.Move(_searchMatches.Count,-1,1);RenderSearch(); }
    private void MoveSearch(int offset) { _currentSearchMatch=TerminalSearch.Move(_searchMatches.Count,_currentSearchMatch,offset);RenderSearch(); }
    private void RenderSearch()
    {
        OutputText.TextHighlighters.Clear();
        if(_searchMatches.Count==0){SearchResultText.Text=string.IsNullOrEmpty(SearchText.Text)?string.Empty:"No matches";return;}
        var matches=new TextHighlighter { Background=ThemeBrush("SearchMatchBrush") };
        foreach(var start in _searchMatches)matches.Ranges.Add(new TextRange { StartIndex=start,Length=SearchText.Text.Length });
        OutputText.TextHighlighters.Add(matches);
        var current=new TextHighlighter { Background=ThemeBrush("SearchCurrentBrush") };
        current.Ranges.Add(new TextRange { StartIndex=_searchMatches[_currentSearchMatch],Length=SearchText.Text.Length });OutputText.TextHighlighters.Add(current);
        SearchResultText.Text=$"{_currentSearchMatch+1} of {_searchMatches.Count}";
        var line=OutputText.Text.AsSpan(0,_searchMatches[_currentSearchMatch]).Count('\n');OutputScroll.UpdateLayout();OutputScroll.ChangeView(null,Math.Max(0,line*18-36),null,true);
    }
    private void StartButton_Click(object sender,RoutedEventArgs e)=>StartRequested?.Invoke(this,EventArgs.Empty);
    private void StopButton_Click(object sender,RoutedEventArgs e)=>StopRequested?.Invoke(this,EventArgs.Empty);
    private void PowerShellButton_Click(object sender,RoutedEventArgs e)=>PowerShellRequested?.Invoke(this,EventArgs.Empty);
    private void CodexButton_Click(object sender,RoutedEventArgs e)=>CodexRequested?.Invoke(this,EventArgs.Empty);
    private void DemoAlertButton_Click(object sender,RoutedEventArgs e)=>DemoAlertRequested?.Invoke(this,EventArgs.Empty);
    // Printable characters typed into the box are relayed live to the shell, then the box is
    // cleared so the shell's own echo (in the output view) is the single source of truth.
    private async void InputText_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(_relayingInput)return;
        var typed=InputText.Text;
        if(typed.Length>0){_relayingInput=true;InputText.Text=string.Empty;_relayingInput=false;}
        PartialInputChanged?.Invoke(this,EventArgs.Empty);
        if(typed.Length>0&&!InputText.IsReadOnly&&SendKeysAsync is {} send)await send(typed);
    }
    // Control and navigation keys are translated to their terminal byte sequences and forwarded live.
    private async void InputText_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(InputText.IsReadOnly)return;
        var sequence=TranslateKey(e.Key);
        if(sequence is null)return;
        e.Handled=true;
        if(SendKeysAsync is {} send)await send(sequence);
    }
    private static string? TranslateKey(VirtualKey key)
    {
        if(IsControlDown()&&key>=VirtualKey.A&&key<=VirtualKey.Z)return ((char)(key-VirtualKey.A+1)).ToString();
        return key switch
        {
            VirtualKey.Enter=>"\r",
            VirtualKey.Tab=>"\t",
            VirtualKey.Back=>"\x7f",
            VirtualKey.Escape=>"\x1b",
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
    }
    private static bool IsControlDown()=>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    private void SearchText_TextChanged(object sender,TextChangedEventArgs e)=>RefreshSearch();
    private void SearchText_KeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;MoveToNextMatch();}else if(e.Key==VirtualKey.Escape){e.Handled=true;CloseSearch();}}
    private void PreviousSearchButton_Click(object sender,RoutedEventArgs e)=>MoveToPreviousMatch();
    private void NextSearchButton_Click(object sender,RoutedEventArgs e)=>MoveToNextMatch();
    private void CloseSearchButton_Click(object sender,RoutedEventArgs e)=>CloseSearch();
    private void Panel_SizeChanged(object sender,SizeChangedEventArgs e)=>ResizeRequested?.Invoke(this,new TerminalSize((short)Math.Clamp((int)(Panel.ActualWidth/8),20,240),(short)Math.Clamp((int)(Panel.ActualHeight/18),5,100)));
    private static SolidColorBrush ThemeBrush(string key)=>(SolidColorBrush)Application.Current.Resources[key];
}
