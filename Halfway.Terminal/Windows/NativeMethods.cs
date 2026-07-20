using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Halfway.Terminal.Windows;

internal static class NativeMethods
{
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint ProcThreadAttributePseudoConsole = 0x00020016;
    internal const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        IntPtr pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(
        Coord size,
        SafeFileHandle input,
        SafeFileHandle output,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    internal static extern int ResizePseudoConsole(SafePseudoConsoleHandle pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? applicationName,
        string commandLine,
        ref SecurityAttributes processAttributes,
        ref SecurityAttributes threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Coord(short x, short y)
{
    internal short X = x;
    internal short Y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    internal int Length;
    internal IntPtr SecurityDescriptor;
    internal int InheritHandle;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    internal int Size;
    internal string? Reserved;
    internal string? Desktop;
    internal string? Title;
    internal int X;
    internal int Y;
    internal int XSize;
    internal int YSize;
    internal int XCountChars;
    internal int YCountChars;
    internal int FillAttribute;
    internal int Flags;
    internal short ShowWindow;
    internal short Reserved2;
    internal IntPtr ReservedPointer;
    internal IntPtr StandardInput;
    internal IntPtr StandardOutput;
    internal IntPtr StandardError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    internal StartupInfo StartupInfo;
    internal IntPtr AttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    internal IntPtr Process;
    internal IntPtr Thread;
    internal uint ProcessId;
    internal uint ThreadId;
}

internal sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafePseudoConsoleHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ClosePseudoConsole(handle);
        return true;
    }
}

internal sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeKernelHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
