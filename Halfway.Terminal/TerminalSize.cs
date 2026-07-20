namespace Halfway.Terminal;

public readonly record struct TerminalSize(short Columns, short Rows)
{
    public TerminalSize Clamp() => new(
        Math.Clamp(Columns, (short)1, short.MaxValue),
        Math.Clamp(Rows, (short)1, short.MaxValue));
}
