using System.Text;
using Halfway.Core.Vt;
using Xunit;

namespace Halfway.Core.Tests;

public sealed class TerminalEmulatorTests
{
    private static string LineText(VtSnapshot snapshot, int row)
    {
        var builder = new StringBuilder();
        foreach (var cell in snapshot.Lines[row]) builder.Append(cell.Glyph);
        return builder.ToString().TrimEnd();
    }

    [Fact]
    public void Writes_plain_text()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("hello");
        var snap = term.Snapshot();
        Assert.Equal("hello", LineText(snap, 0));
        Assert.Equal(0, snap.CursorRow);
        Assert.Equal(5, snap.CursorColumn);
    }

    [Fact]
    public void Carriage_return_and_line_feed_move_cursor()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("abc\r\ndef");
        var snap = term.Snapshot();
        Assert.Equal("abc", LineText(snap, 0));
        Assert.Equal("def", LineText(snap, 1));
    }

    [Fact]
    public void Wraps_at_end_of_line()
    {
        var term = new TerminalEmulator(3, 5);
        term.Process("abcd");
        var snap = term.Snapshot();
        Assert.Equal("abc", LineText(snap, 0));
        Assert.Equal("d", LineText(snap, 1));
    }

    [Fact]
    public void Cursor_addressing_overwrites_in_place()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("hello");
        term.Process("\x1b[1;1H");
        term.Process("H");
        Assert.Equal("Hello", LineText(term.Snapshot(), 0));
    }

    [Fact]
    public void Backspace_moves_cursor_back()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("abc\b\bX");
        Assert.Equal("aXc", LineText(term.Snapshot(), 0));
    }

    [Fact]
    public void Erase_display_clears_screen()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("line1\r\nline2");
        term.Process("\x1b[2J");
        var snap = term.Snapshot();
        Assert.Equal("", LineText(snap, 0));
        Assert.Equal("", LineText(snap, 1));
    }

    [Fact]
    public void Erase_to_end_of_line()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("abcdef");
        term.Process("\x1b[1;4H");
        term.Process("\x1b[K");
        Assert.Equal("abc", LineText(term.Snapshot(), 0));
    }

    [Fact]
    public void Sgr_sets_and_resets_foreground_color()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("\x1b[31mR\x1b[0mX");
        var snap = term.Snapshot();
        Assert.Equal(VtColor.Indexed(1), snap.Lines[0][0].Foreground);
        Assert.Equal(VtColor.Default, snap.Lines[0][1].Foreground);
    }

    [Fact]
    public void Truecolor_foreground_is_parsed()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("\x1b[38;2;10;20;30mZ");
        var cell = term.Snapshot().Lines[0][0];
        Assert.Equal(VtColorKind.Rgb, cell.Foreground.Kind);
        Assert.Equal(10, cell.Foreground.R);
        Assert.Equal(20, cell.Foreground.G);
        Assert.Equal(30, cell.Foreground.B);
    }

    [Fact]
    public void Line_feed_at_bottom_scrolls_into_scrollback()
    {
        var term = new TerminalEmulator(10, 2);
        term.Process("a\r\nb\r\nc");
        var snap = term.Snapshot();
        Assert.Equal("a", LineText(snap, 0));
        Assert.Equal("b", LineText(snap, 1));
        Assert.Equal("c", LineText(snap, 2));
    }

    [Fact]
    public void Alternate_screen_isolates_and_restores()
    {
        var term = new TerminalEmulator(10, 2);
        term.Process("keep\r\nkeep2");
        term.Process("\x1b[?1049h");
        term.Process("alt");
        Assert.True(term.IsAlternateScreen);
        var altSnap = term.Snapshot();
        Assert.Equal(2, altSnap.Lines.Count);
        Assert.Equal("alt", LineText(altSnap, 0));
        term.Process("\x1b[?1049l");
        Assert.False(term.IsAlternateScreen);
        Assert.Equal("keep", LineText(term.Snapshot(), 0));
    }

    [Fact]
    public void Window_title_osc_is_ignored()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("\x1b]0;Halfway\aok");
        Assert.Equal("ok", LineText(term.Snapshot(), 0));
    }

    [Fact]
    public void Resize_preserves_content_and_dimensions()
    {
        var term = new TerminalEmulator(20, 5);
        term.Process("hello");
        term.Resize(10, 3);
        Assert.Equal(10, term.Columns);
        Assert.Equal(3, term.Rows);
        Assert.Equal("hello", LineText(term.Snapshot(), 0));
    }
}
