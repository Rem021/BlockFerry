using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BlockFerry.Core.System;

internal readonly record struct WindowsProtectedDataNativeResult(
    IntPtr Data,
    uint Length);

internal interface IWindowsProtectedDataNative
{
    WindowsProtectedDataNativeResult Transform(byte[] input, byte[] entropy, bool protect);

    void CopyToManaged(IntPtr source, byte[] destination);

    void SecureZero(IntPtr data, uint length);

    void Free(IntPtr data);
}

public sealed class WindowsCurrentUserProtectedData : IProtectedData
{
    private readonly IWindowsProtectedDataNative native;

    public WindowsCurrentUserProtectedData()
        : this(WindowsProtectedDataNative.Instance)
    {
    }

    internal WindowsCurrentUserProtectedData(IWindowsProtectedDataNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        this.native = native;
    }

    public byte[] Protect(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> entropy,
        int maximumOutputBytes)
    {
        EnsureWindows();
        return Transform(plaintext, entropy, protect: true, maximumOutputBytes);
    }

    public byte[] Unprotect(
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> entropy,
        int maximumOutputBytes)
    {
        EnsureWindows();
        return Transform(ciphertext, entropy, protect: false, maximumOutputBytes);
    }

    private byte[] Transform(
        ReadOnlySpan<byte> input,
        ReadOnlySpan<byte> entropy,
        bool protect,
        int maximumOutputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes);
        var inputBytes = input.ToArray();
        var entropyBytes = entropy.ToArray();
        WindowsProtectedDataNativeResult output = default;
        try
        {
            output = native.Transform(inputBytes, entropyBytes, protect);
            if (output.Length > (uint)maximumOutputBytes || output.Length > int.MaxValue)
            {
                throw new ProtectedDataLimitException(
                    "The protected payload exceeded its fixed output bound.");
            }

            var result = new byte[checked((int)output.Length)];
            if (result.Length > 0)
            {
                native.CopyToManaged(output.Data, result);
            }

            return result;
        }
        finally
        {
            try
            {
                if (output.Data != IntPtr.Zero)
                {
                    try
                    {
                        if (!protect)
                        {
                            native.SecureZero(output.Data, output.Length);
                        }
                    }
                    finally
                    {
                        native.Free(output.Data);
                    }

                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(inputBytes);
                CryptographicOperations.ZeroMemory(entropyBytes);
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Current-user DPAPI protection requires Windows.");
        }
    }
}

internal sealed class WindowsProtectedDataNative : IWindowsProtectedDataNative
{
    private const uint CryptProtectUiForbidden = 0x00000001;
    private const int ZeroChunkLength = 4096;
    private static readonly byte[] ZeroChunk = new byte[ZeroChunkLength];

    private WindowsProtectedDataNative()
    {
    }

    public static WindowsProtectedDataNative Instance { get; } = new();

    public WindowsProtectedDataNativeResult Transform(byte[] input, byte[] entropy, bool protect)
    {
        GCHandle inputHandle = default;
        GCHandle entropyHandle = default;
        try
        {
            var inputBlob = Pin(input, ref inputHandle);
            var entropyBlob = Pin(entropy, ref entropyHandle);
            NativeDataBlob output;
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            if (!succeeded)
            {
                throw new CryptographicException(
                    protect
                        ? "Current-user data protection failed."
                        : "Current-user protected data could not be authenticated.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            return new WindowsProtectedDataNativeResult(output.Data, output.Length);
        }
        finally
        {
            if (entropyHandle.IsAllocated)
            {
                entropyHandle.Free();
            }

            if (inputHandle.IsAllocated)
            {
                inputHandle.Free();
            }
        }
    }

    public void CopyToManaged(IntPtr source, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length > 0)
        {
            Marshal.Copy(source, destination, 0, destination.Length);
        }
    }

    public void SecureZero(IntPtr data, uint length)
    {
        var remaining = length;
        var offset = 0;
        while (remaining > 0)
        {
            var count = checked((int)Math.Min(remaining, ZeroChunkLength));
            Marshal.Copy(ZeroChunk, 0, IntPtr.Add(data, offset), count);
            remaining -= checked((uint)count);
            offset = checked(offset + count);
        }
    }

    public void Free(IntPtr data)
    {
        _ = LocalFree(data);
    }

    private static NativeDataBlob Pin(byte[] bytes, ref GCHandle handle)
    {
        if (bytes.Length == 0)
        {
            return default;
        }

        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new NativeDataBlob
        {
            Length = checked((uint)bytes.Length),
            Data = handle.AddrOfPinnedObject(),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDataBlob
    {
        public uint Length;
        public IntPtr Data;
    }

#pragma warning disable SYSLIB1054
    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref NativeDataBlob dataIn,
        string? description,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        out NativeDataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref NativeDataBlob dataIn,
        IntPtr description,
        ref NativeDataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        out NativeDataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}
