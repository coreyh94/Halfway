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
    private IReadOnlyList<int> _searchMatches = [];
    private int _currentSearchMatch = -1;
    public TerminalSessionView(SessionMetadata metadata) { InitializeComponent(); Metadata = metadata; TitleText.Text = metadata.DisplayName; if(metadata.Kind==AgentKind.Primary){PowerShellButton.Visibility=Visibility.Visible;CodexButton.Visibility=Visibility.Visible;DemoAlertButton.Visibility=Visibility.Visible;} SetStatus(metadata.LastStatus); }
    public SessionMetadata Metadata { get; }
    public string PartialInput => InputText.Text;
    public bool IsSearchOpen => SearchPanel.Visibility == Visibility.Visible;
    public event EventHandler<string>? InputSubmitted;
    public event EventHandler? PartialInputChanged;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? PowerShellRequested;
    public event EventHandler? CodexRequested;
    public event EventHandler? DemoAlertRequested;
    public event EventHandler<TerminalSize>? ResizeRequested;
    public void SetStatus(AgentStatus status) { StatusText.Text=status.ToString().ToUpperInvariant(); var active=status is AgentStatus.Queued or AgentStatus.Running or AgentStatus.Waiting; StartButton.IsEnabled=!active; StartButton.Content=status == AgentStatus.Disconnected ? "Restart" : "Start"; StopButton.IsEnabled=active; InputText.IsReadOnly=status is not (AgentStatus.Running or AgentStatus.Waiting); }
    public void Append(string output) { var plain=Regex.Replace(output,"\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])",string.Empty);var combined=OutputText.Text+plain;OutputText.Text=combined.Length<=MaximumOutputCharacters?combined:combined[^MaximumOutputCharacters..];if(IsSearchOpen)RefreshSearch();else{OutputScroll.UpdateLayout();OutputScroll.ChangeView(null,OutputScroll.ScrollableHeight,null,true);} }
    public void ClearOutput() { OutputText.Text=string.Empty; RefreshSearch(); }
    public void FocusInput()=>InputText.Focus(FocusState.Programmatic);
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
        var matches=new TextHighlighter { Background=new SolidColorBrush(Windows.UI.Color.FromArgb(140,90,96,110)) };
        foreach(var start in _searchMatches)matches.Ranges.Add(new TextRange { StartIndex=start,Length=SearchText.Text.Length });
        OutputText.TextHighlighters.Add(matches);
        var current=new TextHighlighter { Background=new SolidColorBrush(Windows.UI.Color.FromArgb(255,241,199,122)) };
        current.Ranges.Add(new TextRange { StartIndex=_searchMatches[_currentSearchMatch],Length=SearchText.Text.Length });OutputText.TextHighlighters.Add(current);
        SearchResultText.Text=$"{_currentSearchMatch+1} of {_searchMatches.Count}";
        var line=OutputText.Text.AsSpan(0,_searchMatches[_currentSearchMatch]).Count('\n');OutputScroll.UpdateLayout();OutputScroll.ChangeView(null,Math.Max(0,line*18-36),null,true);
    }
    private void StartButton_Click(object sender,RoutedEventArgs e)=>StartRequested?.Invoke(this,EventArgs.Empty);
    private void StopButton_Click(object sender,RoutedEventArgs e)=>StopRequested?.Invoke(this,EventArgs.Empty);
    private void PowerShellButton_Click(object sender,RoutedEventArgs e)=>PowerShellRequested?.Invoke(this,EventArgs.Empty);
    private void CodexButton_Click(object sender,RoutedEventArgs e)=>CodexRequested?.Invoke(this,EventArgs.Empty);
    private void DemoAlertButton_Click(object sender,RoutedEventArgs e)=>DemoAlertRequested?.Invoke(this,EventArgs.Empty);
    private void InputText_TextChanged(object sender,TextChangedEventArgs e)=>PartialInputChanged?.Invoke(this,EventArgs.Empty);
    private void InputText_KeyDown(object sender,KeyRoutedEventArgs e){if(e.Key!=VirtualKey.Enter)return;e.Handled=true;var text=InputText.Text;InputText.Text=string.Empty;InputSubmitted?.Invoke(this,text);}
    private void SearchText_TextChanged(object sender,TextChangedEventArgs e)=>RefreshSearch();
    private void SearchText_KeyDown(object sender,KeyRoutedEventArgs e){if(e.Key==VirtualKey.Enter){e.Handled=true;MoveToNextMatch();}else if(e.Key==VirtualKey.Escape){e.Handled=true;CloseSearch();}}
    private void PreviousSearchButton_Click(object sender,RoutedEventArgs e)=>MoveToPreviousMatch();
    private void NextSearchButton_Click(object sender,RoutedEventArgs e)=>MoveToNextMatch();
    private void CloseSearchButton_Click(object sender,RoutedEventArgs e)=>CloseSearch();
    private void Panel_SizeChanged(object sender,SizeChangedEventArgs e)=>ResizeRequested?.Invoke(this,new TerminalSize((short)Math.Clamp((int)(Panel.ActualWidth/8),20,240),(short)Math.Clamp((int)(Panel.ActualHeight/18),5,100)));
}
