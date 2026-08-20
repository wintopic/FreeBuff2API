using System.Net;
using System.Runtime.InteropServices;

namespace FreeBuffLauncher;

internal static class PortOwner
{
    private const int AfInet = 2;
    private const int ErrorInsufficientBuffer = 122;

    public static int? FindListeningProcessId(int port)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableClass.OwnerPidListener, 0);
        if (result != ErrorInsufficientBuffer || size <= 0) return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableClass.OwnerPidListener, 0);
            if (result != 0) return null;
            var count = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(rowPointer);
                var localPort = (ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort);
                if (localPort == port) return unchecked((int)row.OwningPid);
                rowPointer = IntPtr.Add(rowPointer, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return null;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table,
        ref int size,
        bool order,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        OwnerPidListener = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }
}
