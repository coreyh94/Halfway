namespace Halfway.Core.Vt;

public enum VtColorKind { Default, Indexed, Rgb }

/// <summary>A terminal colour: the default (theme) colour, a 0-255 palette index, or a truecolor RGB.</summary>
public readonly struct VtColor : IEquatable<VtColor>
{
    public static readonly VtColor Default = new(VtColorKind.Default, 0, 0, 0, 0);

    private VtColor(VtColorKind kind, int index, byte r, byte g, byte b)
    {
        Kind = kind; Index = index; R = r; G = g; B = b;
    }

    public VtColorKind Kind { get; }
    public int Index { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public static VtColor Indexed(int index) => new(VtColorKind.Indexed, index, 0, 0, 0);
    public static VtColor Rgb(byte r, byte g, byte b) => new(VtColorKind.Rgb, 0, r, g, b);

    public bool Equals(VtColor other) => Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is VtColor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine((int)Kind, Index, R, G, B);
}

[Flags]
public enum VtCellAttributes { None = 0, Bold = 1, Faint = 2, Italic = 4, Underline = 8, Reverse = 16 }

public readonly struct VtCell : IEquatable<VtCell>
{
    public VtCell(char glyph, VtColor foreground, VtColor background, VtCellAttributes attributes)
    {
        Glyph = glyph; Foreground = foreground; Background = background; Attributes = attributes;
    }

    public char Glyph { get; }
    public VtColor Foreground { get; }
    public VtColor Background { get; }
    public VtCellAttributes Attributes { get; }

    public static readonly VtCell Blank = new(' ', VtColor.Default, VtColor.Default, VtCellAttributes.None);

    public bool Equals(VtCell other) => Glyph == other.Glyph && Foreground.Equals(other.Foreground) && Background.Equals(other.Background) && Attributes == other.Attributes;
    public override bool Equals(object? obj) => obj is VtCell other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Glyph, Foreground, Background, (int)Attributes);
}

/// <summary>A rendered snapshot of the emulator: one cell array per visible line plus the cursor.</summary>
public sealed class VtSnapshot
{
    public VtSnapshot(IReadOnlyList<VtCell[]> lines, int columns, int cursorRow, int cursorColumn, bool cursorVisible)
    {
        Lines = lines; Columns = columns; CursorRow = cursorRow; CursorColumn = cursorColumn; CursorVisible = cursorVisible;
    }

    public IReadOnlyList<VtCell[]> Lines { get; }
    public int Columns { get; }
    public int CursorRow { get; }
    public int CursorColumn { get; }
    public bool CursorVisible { get; }
}

/// <summary>
/// A compact VT/ANSI terminal emulator: a 2-D screen buffer driven by an escape-sequence parser.
/// It handles the sequences that ordinary shells and full-screen TUIs (cursor addressing, erase,
/// SGR colour, scroll regions, the alternate screen) actually emit, so their output renders in
/// place rather than as an ever-growing log. It is intentionally not a conformant DEC terminal.
/// </summary>
public sealed class TerminalEmulator
{
    private const int MaxScrollback = 2000;

    private int _cols;
    private int _rows;
    private VtCell[][] _primary;
    private VtCell[][] _alternate;
    private readonly List<VtCell[]> _scrollback = new();
    private bool _useAlternate;

    private int _row;
    private int _col;
    private int _savedRow;
    private int _savedCol;
    private bool _wrapPending;
    private int _scrollTop;
    private int _scrollBottom;
    private bool _cursorVisible = true;

    private VtColor _fg = VtColor.Default;
    private VtColor _bg = VtColor.Default;
    private VtCellAttributes _attrs = VtCellAttributes.None;

    private ParseState _state = ParseState.Ground;
    private readonly System.Text.StringBuilder _params = new();
    private readonly System.Text.StringBuilder _osc = new();

    private enum ParseState { Ground, Escape, Csi, Osc, OscEscape }

    public TerminalEmulator(int columns, int rows)
    {
        _cols = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        _primary = NewGrid(_rows, _cols);
        _alternate = NewGrid(_rows, _cols);
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    public int Columns => _cols;
    public int Rows => _rows;
    public bool IsAlternateScreen => _useAlternate;

    private VtCell[][] Active => _useAlternate ? _alternate : _primary;

    private VtCell Erased => new(' ', VtColor.Default, _bg, VtCellAttributes.None);

    private static VtCell[][] NewGrid(int rows, int cols)
    {
        var grid = new VtCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            grid[r] = new VtCell[cols];
            for (var c = 0; c < cols; c++) grid[r][c] = VtCell.Blank;
        }
        return grid;
    }

    public void Reset()
    {
        _primary = NewGrid(_rows, _cols);
        _alternate = NewGrid(_rows, _cols);
        _scrollback.Clear();
        _useAlternate = false;
        _row = _col = _savedRow = _savedCol = 0;
        _wrapPending = false;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _cursorVisible = true;
        _fg = VtColor.Default; _bg = VtColor.Default; _attrs = VtCellAttributes.None;
        _state = ParseState.Ground;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == _cols && rows == _rows) return;
        _primary = Resized(_primary, rows, columns);
        _alternate = Resized(_alternate, rows, columns);
        _cols = columns;
        _rows = rows;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _row = Math.Clamp(_row, 0, _rows - 1);
        _col = Math.Clamp(_col, 0, _cols - 1);
        _wrapPending = false;
    }

    private static VtCell[][] Resized(VtCell[][] grid, int rows, int cols)
    {
        var next = NewGrid(rows, cols);
        var copyRows = Math.Min(rows, grid.Length);
        for (var r = 0; r < copyRows; r++)
        {
            var copyCols = Math.Min(cols, grid[r].Length);
            for (var c = 0; c < copyCols; c++) next[r][c] = grid[r][c];
        }
        return next;
    }

    public VtSnapshot Snapshot()
    {
        var lines = new List<VtCell[]>(_scrollback.Count + _rows);
        var cursorRowOffset = 0;
        if (!_useAlternate)
        {
            foreach (var line in _scrollback) lines.Add(line);
            cursorRowOffset = _scrollback.Count;
        }
        var active = Active;
        for (var r = 0; r < _rows; r++)
        {
            var copy = new VtCell[_cols];
            Array.Copy(active[r], copy, _cols);
            lines.Add(copy);
        }
        return new VtSnapshot(lines, _cols, cursorRowOffset + _row, _col, _cursorVisible);
    }

    public void Process(string data)
    {
        foreach (var ch in data) Step(ch);
    }

    private void Step(char ch)
    {
        switch (_state)
        {
            case ParseState.Ground: Ground(ch); break;
            case ParseState.Escape: Escape(ch); break;
            case ParseState.Csi: Csi(ch); break;
            case ParseState.Osc: Osc(ch); break;
            case ParseState.OscEscape:
                _state = ParseState.Ground;
                if (ch != '\\') Ground(ch);
                break;
        }
    }

    private void Ground(char ch)
    {
        if (_pendingCharset) { _pendingCharset = false; return; }
        switch (ch)
        {
            case '\x1b': _state = ParseState.Escape; break;
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\r': _col = 0; _wrapPending = false; break;
            case '\b': if (_col > 0) _col--; _wrapPending = false; break;
            case '\t': Tab(); break;
            case '\a': break;
            default:
                if (ch >= ' ') Put(ch);
                break;
        }
    }

    private void Escape(char ch)
    {
        switch (ch)
        {
            case '[': _params.Clear(); _state = ParseState.Csi; break;
            case ']': _osc.Clear(); _state = ParseState.Osc; break;
            case '(': case ')': case '*': case '+': _state = ParseState.Ground; _pendingCharset = true; break;
            case '7': _savedRow = _row; _savedCol = _col; _state = ParseState.Ground; break;
            case '8': _row = Math.Clamp(_savedRow, 0, _rows - 1); _col = Math.Clamp(_savedCol, 0, _cols - 1); _wrapPending = false; _state = ParseState.Ground; break;
            case 'M': ReverseIndex(); _state = ParseState.Ground; break;
            case 'D': LineFeed(); _state = ParseState.Ground; break;
            case 'E': _col = 0; LineFeed(); _state = ParseState.Ground; break;
            case 'c': Reset(); _state = ParseState.Ground; break;
            default: _state = ParseState.Ground; break;
        }
    }

    private bool _pendingCharset;

    private void Csi(char ch)
    {
        if (ch is >= (char)0x20 and <= (char)0x3f) { _params.Append(ch); return; }
        DispatchCsi(ch);
        _state = ParseState.Ground;
    }

    private void Osc(char ch)
    {
        if (ch == '\a') { _state = ParseState.Ground; return; }
        if (ch == '\x1b') { _state = ParseState.OscEscape; return; }
        _osc.Append(ch);
    }

    private void Tab()
    {
        _wrapPending = false;
        var next = (_col / 8 + 1) * 8;
        _col = Math.Min(next, _cols - 1);
    }

    private void Put(char ch)
    {
        if (_wrapPending)
        {
            _col = 0;
            LineFeed();
            _wrapPending = false;
        }
        Active[_row][_col] = new VtCell(ch, _fg, _bg, _attrs);
        if (_col == _cols - 1) _wrapPending = true;
        else _col++;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (_row == _scrollBottom) ScrollUp(1);
        else if (_row < _rows - 1) _row++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_row == _scrollTop) ScrollDown(1);
        else if (_row > 0) _row--;
    }

    private void ScrollUp(int n)
    {
        var grid = Active;
        for (var k = 0; k < n; k++)
        {
            if (!_useAlternate && _scrollTop == 0)
            {
                var evicted = grid[_scrollTop];
                _scrollback.Add(evicted);
                if (_scrollback.Count > MaxScrollback) _scrollback.RemoveAt(0);
            }
            for (var r = _scrollTop; r < _scrollBottom; r++) grid[r] = grid[r + 1];
            grid[_scrollBottom] = BlankRow();
        }
    }

    private void ScrollDown(int n)
    {
        var grid = Active;
        for (var k = 0; k < n; k++)
        {
            for (var r = _scrollBottom; r > _scrollTop; r--) grid[r] = grid[r - 1];
            grid[_scrollTop] = BlankRow();
        }
    }

    private VtCell[] BlankRow()
    {
        var row = new VtCell[_cols];
        var blank = Erased;
        for (var c = 0; c < _cols; c++) row[c] = blank;
        return row;
    }

    private int[] ParamValues()
    {
        var text = _params.ToString();
        if (text.Length > 0 && (text[0] == '?' || text[0] == '>' || text[0] == '!')) text = text[1..];
        if (text.Length == 0) return Array.Empty<int>();
        var parts = text.Split(';');
        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++) values[i] = int.TryParse(parts[i], out var v) ? v : 0;
        return values;
    }

    private int Param(int index, int fallback)
    {
        var values = ParamValues();
        return index < values.Length ? values[index] : fallback;
    }

    private bool IsPrivate => _params.Length > 0 && _params[0] == '?';

    private void DispatchCsi(char final)
    {
        switch (final)
        {
            case 'A': MoveCursor(-Math.Max(1, Param(0, 1)), 0); break;
            case 'B': MoveCursor(Math.Max(1, Param(0, 1)), 0); break;
            case 'C': MoveCursor(0, Math.Max(1, Param(0, 1))); break;
            case 'D': MoveCursor(0, -Math.Max(1, Param(0, 1))); break;
            case 'E': _col = 0; MoveCursor(Math.Max(1, Param(0, 1)), 0); break;
            case 'F': _col = 0; MoveCursor(-Math.Max(1, Param(0, 1)), 0); break;
            case 'G': _col = Math.Clamp(Math.Max(1, Param(0, 1)) - 1, 0, _cols - 1); _wrapPending = false; break;
            case 'd': _row = Math.Clamp(Math.Max(1, Param(0, 1)) - 1, 0, _rows - 1); _wrapPending = false; break;
            case 'H': case 'f':
                _row = Math.Clamp(Math.Max(1, Param(0, 1)) - 1, 0, _rows - 1);
                _col = Math.Clamp(Math.Max(1, Param(1, 1)) - 1, 0, _cols - 1);
                _wrapPending = false;
                break;
            case 'J': EraseDisplay(Param(0, 0)); break;
            case 'K': EraseLine(Param(0, 0)); break;
            case 'm': ApplySgr(); break;
            case 'L': InsertLines(Math.Max(1, Param(0, 1))); break;
            case 'M': DeleteLines(Math.Max(1, Param(0, 1))); break;
            case '@': InsertChars(Math.Max(1, Param(0, 1))); break;
            case 'P': DeleteChars(Math.Max(1, Param(0, 1))); break;
            case 'X': EraseChars(Math.Max(1, Param(0, 1))); break;
            case 'S': ScrollUp(Math.Max(1, Param(0, 1))); break;
            case 'T': ScrollDown(Math.Max(1, Param(0, 1))); break;
            case 'r': SetScrollRegion(); break;
            case 's': _savedRow = _row; _savedCol = _col; break;
            case 'u': _row = Math.Clamp(_savedRow, 0, _rows - 1); _col = Math.Clamp(_savedCol, 0, _cols - 1); _wrapPending = false; break;
            case 'h': SetMode(true); break;
            case 'l': SetMode(false); break;
            default: break;
        }
    }

    private void MoveCursor(int dRow, int dCol)
    {
        _wrapPending = false;
        _row = Math.Clamp(_row + dRow, 0, _rows - 1);
        _col = Math.Clamp(_col + dCol, 0, _cols - 1);
    }

    private void SetScrollRegion()
    {
        var top = Math.Max(1, Param(0, 1)) - 1;
        var bottom = Param(1, _rows) - 1;
        if (bottom <= 0 || bottom >= _rows) bottom = _rows - 1;
        if (top < bottom)
        {
            _scrollTop = Math.Clamp(top, 0, _rows - 1);
            _scrollBottom = Math.Clamp(bottom, 0, _rows - 1);
        }
        _row = 0; _col = 0; _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        var grid = Active;
        var blank = Erased;
        switch (mode)
        {
            case 0:
                for (var c = _col; c < _cols; c++) grid[_row][c] = blank;
                for (var r = _row + 1; r < _rows; r++) grid[r] = BlankRow();
                break;
            case 1:
                for (var r = 0; r < _row; r++) grid[r] = BlankRow();
                for (var c = 0; c <= _col && c < _cols; c++) grid[_row][c] = blank;
                break;
            case 2:
                for (var r = 0; r < _rows; r++) grid[r] = BlankRow();
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var row = Active[_row];
        var blank = Erased;
        switch (mode)
        {
            case 0: for (var c = _col; c < _cols; c++) row[c] = blank; break;
            case 1: for (var c = 0; c <= _col && c < _cols; c++) row[c] = blank; break;
            case 2: for (var c = 0; c < _cols; c++) row[c] = blank; break;
        }
    }

    private void EraseChars(int n)
    {
        var row = Active[_row];
        var blank = Erased;
        for (var c = _col; c < Math.Min(_cols, _col + n); c++) row[c] = blank;
    }

    private void InsertChars(int n)
    {
        var row = Active[_row];
        var blank = Erased;
        for (var c = _cols - 1; c >= _col; c--)
            row[c] = c - n >= _col ? row[c - n] : blank;
    }

    private void DeleteChars(int n)
    {
        var row = Active[_row];
        var blank = Erased;
        for (var c = _col; c < _cols; c++)
            row[c] = c + n < _cols ? row[c + n] : blank;
    }

    private void InsertLines(int n)
    {
        if (_row < _scrollTop || _row > _scrollBottom) return;
        var grid = Active;
        for (var k = 0; k < n; k++)
        {
            for (var r = _scrollBottom; r > _row; r--) grid[r] = grid[r - 1];
            grid[_row] = BlankRow();
        }
    }

    private void DeleteLines(int n)
    {
        if (_row < _scrollTop || _row > _scrollBottom) return;
        var grid = Active;
        for (var k = 0; k < n; k++)
        {
            for (var r = _row; r < _scrollBottom; r++) grid[r] = grid[r + 1];
            grid[_scrollBottom] = BlankRow();
        }
    }

    private void SetMode(bool enable)
    {
        if (!IsPrivate) return;
        foreach (var code in ParamValues())
        {
            switch (code)
            {
                case 25: _cursorVisible = enable; break;
                case 47:
                case 1047:
                case 1049:
                    SwitchScreen(enable, saveRestore: code == 1049);
                    break;
            }
        }
    }

    private void SwitchScreen(bool toAlternate, bool saveRestore)
    {
        if (toAlternate == _useAlternate) return;
        if (toAlternate)
        {
            if (saveRestore) { _savedRow = _row; _savedCol = _col; }
            _alternate = NewGrid(_rows, _cols);
            _useAlternate = true;
            _row = 0; _col = 0;
        }
        else
        {
            _useAlternate = false;
            if (saveRestore) { _row = Math.Clamp(_savedRow, 0, _rows - 1); _col = Math.Clamp(_savedCol, 0, _cols - 1); }
        }
        _scrollTop = 0; _scrollBottom = _rows - 1; _wrapPending = false;
    }

    private void ApplySgr()
    {
        var values = ParamValues();
        if (values.Length == 0) { ResetSgr(); return; }
        for (var i = 0; i < values.Length; i++)
        {
            var code = values[i];
            switch (code)
            {
                case 0: ResetSgr(); break;
                case 1: _attrs |= VtCellAttributes.Bold; break;
                case 2: _attrs |= VtCellAttributes.Faint; break;
                case 3: _attrs |= VtCellAttributes.Italic; break;
                case 4: _attrs |= VtCellAttributes.Underline; break;
                case 7: _attrs |= VtCellAttributes.Reverse; break;
                case 22: _attrs &= ~(VtCellAttributes.Bold | VtCellAttributes.Faint); break;
                case 23: _attrs &= ~VtCellAttributes.Italic; break;
                case 24: _attrs &= ~VtCellAttributes.Underline; break;
                case 27: _attrs &= ~VtCellAttributes.Reverse; break;
                case 39: _fg = VtColor.Default; break;
                case 49: _bg = VtColor.Default; break;
                case 38: i = ExtendedColor(values, i, foreground: true); break;
                case 48: i = ExtendedColor(values, i, foreground: false); break;
                default:
                    if (code >= 30 && code <= 37) _fg = VtColor.Indexed(code - 30);
                    else if (code >= 40 && code <= 47) _bg = VtColor.Indexed(code - 40);
                    else if (code >= 90 && code <= 97) _fg = VtColor.Indexed(code - 90 + 8);
                    else if (code >= 100 && code <= 107) _bg = VtColor.Indexed(code - 100 + 8);
                    break;
            }
        }
    }

    private int ExtendedColor(int[] values, int i, bool foreground)
    {
        if (i + 1 >= values.Length) return i;
        var mode = values[i + 1];
        if (mode == 5 && i + 2 < values.Length)
        {
            var color = VtColor.Indexed(Math.Clamp(values[i + 2], 0, 255));
            if (foreground) _fg = color; else _bg = color;
            return i + 2;
        }
        if (mode == 2 && i + 4 < values.Length)
        {
            var color = VtColor.Rgb((byte)Math.Clamp(values[i + 2], 0, 255), (byte)Math.Clamp(values[i + 3], 0, 255), (byte)Math.Clamp(values[i + 4], 0, 255));
            if (foreground) _fg = color; else _bg = color;
            return i + 4;
        }
        return i + 1;
    }

    private void ResetSgr()
    {
        _fg = VtColor.Default; _bg = VtColor.Default; _attrs = VtCellAttributes.None;
    }
}
