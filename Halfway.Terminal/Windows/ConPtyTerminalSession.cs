using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Halfway.Terminal.Windows;

internal sealed class ConPtyTerminalSession : ITerminalSession
{
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly IntPtr _attributeList;
    private readonly SafeKernelHandle _process;
    private readonly SafeFileHandle _inputReadSide;
    private readonly StreamWriter _input;
    private readonly FileStream _output;
    private readonly SafeFileHandle _outputWriteSide;
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _outputGate = new();
    private readonly StringBuilder _outputBacklog = new();
    private readonly Task _readTask;
    private readonly Task _exitTask;
    private EventHandler<string>? _outputReceived;
    private int _stopping;
    private int _disposed;

    private ConPtyTerminalSession(
        SafePseudoConsoleHandle pseudoConsole,
        IntPtr attributeList,
        SafeKernelHandle process,
        SafeFileHandle inputReadSide,
        SafeFileHandle input,
        SafeFileHandle output,
        SafeFileHandle outputWriteSide,
        int processId)
    {
        _pseudoConsole = pseudoConsole;
        _attributeList = attributeList;
        _process = process;
        _inputReadSide = inputReadSide;
        _input = new StreamWriter(new FileStream(input, FileAccess.Write)) { AutoFlush = true };
        _output = new FileStream(output, FileAccess.Read, 4096, false);
        _outputWriteSide = outputWriteSide;
        ProcessId = processId;
        _readTask = Task.Factory.StartNew(
            ReadOutput,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _exitTask = Task.Factory.StartNew(
            WaitForExit,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public event EventHandler<string>? OutputReceived
    {
        add
        {
            string backlog;
            lock (_outputGate)
            {
                _outputReceived += value;
                backlog = _outputBacklog.ToString();
            }

            if (backlog.Length > 0)
            {
                value?.Invoke(this, backlog);
            }
        }
        remove
        {
            lock (_outputGate)
            {
                _outputReceived -= value;
            }
        }
    }

    public event EventHandler<TerminalExit>? Exited;

    public int ProcessId { get; }

    public Task Completion => _exitTask;

    internal static ConPtyTerminalSession Start(TerminalLaunchOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        if (!Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {options.WorkingDirectory}");
        }

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeKernelHandle? process = null;
        SafeKernelHandle? thread = null;
        IntPtr attributeList = IntPtr.Zero;

        try
        {
            ThrowIfFalse(NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0));
            ThrowIfFalse(NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0));

            var size = options.InitialSize.Clamp();
            ThrowIfFailed(NativeMethods.CreatePseudoConsole(
                new Coord(size.Columns, size.Rows),
                inputRead,
                outputWrite,
                0,
                out var pseudoConsoleHandle));
            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleHandle);

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
            ThrowIfFalse(NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize));
            ThrowIfFalse(NativeMethods.UpdateProcThreadAttribute(
                attributeList,
                0,
                NativeMethods.ProcThreadAttributePseudoConsole,
                pseudoConsole.DangerousGetHandle(),
                (nuint)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero));

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                AttributeList = attributeList,
            };
            var processSecurity = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var threadSecurity = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var commandLine = BuildCommandLine(options.FileName, options.Arguments);
            ThrowIfFalse(NativeMethods.CreateProcess(
                null,
                commandLine,
                ref processSecurity,
                ref threadSecurity,
                false,
                NativeMethods.ExtendedStartupInfoPresent,
                IntPtr.Zero,
                options.WorkingDirectory,
                ref startupInfo,
                out var processInformation));

            process = new SafeKernelHandle(processInformation.Process);
            thread = new SafeKernelHandle(processInformation.Thread);
            thread.Dispose();
            thread = null;

            var session = new ConPtyTerminalSession(
                pseudoConsole,
                attributeList,
                process,
                inputRead,
                inputWrite,
                outputRead,
                outputWrite,
                checked((int)processInformation.ProcessId));
            pseudoConsole = null;
            attributeList = IntPtr.Zero;
            process = null;
            inputRead = null;
            inputWrite = null;
            outputRead = null;
            outputWrite = null;
            return session;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            thread?.Dispose();
            process?.Dispose();
            pseudoConsole?.Dispose();
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    public async ValueTask WriteAsync(string input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _input.Write(input);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Resize(TerminalSize size)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var clamped = size.Clamp();
        ThrowIfFailed(NativeMethods.ResizePseudoConsole(
            _pseudoConsole,
            new Coord(clamped.Columns, clamped.Rows)));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            await _exitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _input.Dispose();
        _inputReadSide.Dispose();

        if (!_exitTask.IsCompleted && !_process.IsClosed && !NativeMethods.TerminateProcess(_process, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        await _exitTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        _pseudoConsole.Dispose();
        _outputWriteSide.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _readCancellation.Cancel();
            _output.Dispose();
            try
            {
                await _readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _process.Dispose();
            NativeMethods.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _inputReadSide.Dispose();
            _outputWriteSide.Dispose();
            _readCancellation.Dispose();
            _writeLock.Dispose();
        }
    }

    private void ReadOutput()
    {
        var buffer = new byte[4096];
        try
        {
            while (!_readCancellation.IsCancellationRequested)
            {
                var count = _output.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    return;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, count);
                EventHandler<string>? handler;
                lock (_outputGate)
                {
                    if (_outputBacklog.Length < 64 * 1024)
                    {
                        _outputBacklog.Append(text);
                    }

                    handler = _outputReceived;
                }

                handler?.Invoke(this, text);
            }
        }
        catch (ObjectDisposedException) when (_readCancellation.IsCancellationRequested)
        {
        }
        catch (IOException) when (_readCancellation.IsCancellationRequested)
        {
        }
    }

    private void WaitForExit()
    {
        _ = NativeMethods.WaitForSingleObject(_process, NativeMethods.Infinite);
        if (!NativeMethods.GetExitCodeProcess(_process, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Exited?.Invoke(this, new TerminalExit(unchecked((int)exitCode), Volatile.Read(ref _stopping) != 0));
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var parts = new List<string>(arguments.Count + 1) { QuoteArgument(fileName) };
        parts.AddRange(arguments.Select(QuoteArgument));
        return string.Join(' ', parts);
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var slashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', slashCount * 2 + 1).Append('"');
                slashCount = 0;
                continue;
            }

            result.Append('\\', slashCount).Append(character);
            slashCount = 0;
        }

        return result.Append('\\', slashCount * 2).Append('"').ToString();
    }

    private static void ThrowIfFalse(bool result)
    {
        if (!result)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }
}
