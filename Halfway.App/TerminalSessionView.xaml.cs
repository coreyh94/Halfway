using System.Text;
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
    // Minimal single-line terminal discipline: interprets carriage return, backspace, tab,
    // cursor left/right/column, erase-line, window-title (OSC) and screen-clear so ordinary shell
    // editing, prompts and progress render in place instead of accumulating as raw escape noise.
    // This is deliberately NOT a full 2D cursor-addressed emulator, so alternate-screen TUIs are
    // still only approximated.
    public void Append(string output)
    {
        var sb = new StringBuilder(OutputText.Text);
        RenderInto(sb, output);
        var rendered = BlankLineRunPattern.Replace(sb.ToString(), "\n\n");
        if (rendered.Length > MaximumOutputCharacters) rendered = rendered[^MaximumOutputCharacters..];
        OutputText.Text = rendered;
        if (IsSearchOpen) RefreshSearch();
        else { OutputScroll.UpdateLayout(); OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true); }
    }

    private static void RenderInto(StringBuilder sb, string s)
    {
        int lineStart = LastLineStart(sb);
        int col = sb.Length - lineStart;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            switch (c)
            {
                case '\x1b': i = HandleEscape(sb, s, i, ref lineStart, ref col); continue;
                case '\n': sb.Append('\n'); lineStart = sb.Length; col = 0; break;
                case '\r': col = 0; break;
                case '\b': if (col > 0) col--; break;
                case '\t': { int adv = 8 - (col % 8); for (int k = 0; k < adv; k++) WriteChar(sb, lineStart, ref col, ' '); break; }
                default:
                    if (c < 0x20) break; // drop bell and other C0 control characters
                    WriteChar(sb, lineStart, ref col, c);
                    break;
            }
            i++;
        }
    }

    private static int LastLineStart(StringBuilder sb)
    {
        for (int k = sb.Length - 1; k >= 0; k--) if (sb[k] == '\n') return k + 1;
        return 0;
    }

    private static void WriteChar(StringBuilder sb, int lineStart, ref int col, char c)
    {
        int pos = lineStart + col;
        if (pos < sb.Length) sb[pos] = c;
        else { while (sb.Length < pos) sb.Append(' '); sb.Append(c); }
        col++;
    }

    private static int HandleEscape(StringBuilder sb, string s, int i, ref int lineStart, ref int col)
    {
        if (i + 1 >= s.Length) return i + 1;
        char n = s[i + 1];
        if (n == '[')
        {
            int j = i + 2;
            while (j < s.Length && s[j] >= 0x20 && s[j] <= 0x3f) j++;
            if (j >= s.Length) return s.Length;
            HandleCsi(sb, s.Substring(i + 2, j - (i + 2)), s[j], ref lineStart, ref col);
            return j + 1;
        }
        if (n == ']') // OSC (e.g. window title) terminated by BEL or ST
        {
            int j = i + 2;
            while (j < s.Length && s[j] != '\a' && !(s[j] == '\x1b' && j + 1 < s.Length && s[j + 1] == '\\')) j++;
            if (j >= s.Length) return s.Length;
            return s[j] == '\a' ? j + 1 : j + 2;
        }
        if (n == 'c') { sb.Clear(); lineStart = 0; col = 0; return i + 2; } // full reset
        if (n is '(' or ')' or '*' or '+') return i + 3; // charset designation
        return i + 2;
    }

    private static void HandleCsi(StringBuilder sb, string param, char final, ref int lineStart, ref int col)
    {
        int First(int def)
        {
            var head = param.Split(';')[0];
            return int.TryParse(head, out var v) ? v : def;
        }
        switch (final)
        {
            case 'D': col = Math.Max(0, col - Math.Max(1, First(1))); break;
            case 'C': col += Math.Max(1, First(1)); break;
            case 'G': col = Math.Max(0, First(1) - 1); break;
            case 'H': case 'f':
            {
                var parts = param.Split(';');
                col = parts.Length >= 2 && int.TryParse(parts[1], out var c2) ? Math.Max(0, c2 - 1) : 0;
                break;
            }
            case 'K':
            {
                int mode = First(0);
                if (mode == 0) { if (lineStart + col < sb.Length) sb.Length = lineStart + col; }
                else if (mode == 2) sb.Length = lineStart;
                else for (int k = 0; k < col && lineStart + k < sb.Length; k++) sb[lineStart + k] = ' ';
                break;
            }
            case 'J':
                if (First(0) is 2 or 3) { sb.Clear(); lineStart = 0; col = 0; }
                break;
            case 'h': case 'l':
                if (param.StartsWith("?") && param.Contains("1049")) { sb.Clear(); lineStart = 0; col = 0; }
                break;
        }
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
    private void ClearButton_Click(object sender,RoutedEventArgs e){ClearOutput();FocusInput();}
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
