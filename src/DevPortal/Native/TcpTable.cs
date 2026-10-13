using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DevPortal.Discovery;

namespace DevPortal.Native;

internal static class TcpTable
{
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, bool sort, int family, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct Row4
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Row6
    {
        public fixed byte LocalAddr[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddr[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    public static IReadOnlyList<Listener> GetListeners()
    {
        var result = new List<Listener>();
        result.AddRange(Read<Row4>(AddressFamily.InterNetwork, row => new Listener(
            ToPort(row.LocalPort), new IPAddress(row.LocalAddr).ToString(), (int)row.OwningPid)));
        result.AddRange(Read<Row6>(AddressFamily.InterNetworkV6, ToListener6));
        return result;
    }

    private static unsafe Listener ToListener6(Row6 row)
    {
        var bytes = new ReadOnlySpan<byte>(row.LocalAddr, 16);
        return new Listener(ToPort(row.LocalPort), new IPAddress(bytes).ToString(), (int)row.OwningPid);
    }

    private static int ToPort(uint raw) => (ushort)IPAddress.NetworkToHostOrder((short)raw);

    private static List<Listener> Read<TRow>(AddressFamily family, Func<TRow, Listener> map) where TRow : struct
    {
        var size = 0;
        GetExtendedTcpTable(0, ref size, false, (int)family, TcpTableOwnerPidListener, 0);

        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var error = GetExtendedTcpTable(buffer, ref size, false, (int)family, TcpTableOwnerPidListener, 0);
                if (error == ErrorInsufficientBuffer)
                    continue;
                if (error != 0)
                    return [];

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TRow>();
                var rows = new List<Listener>(count);
                for (var i = 0; i < count; i++)
                    rows.Add(map(Marshal.PtrToStructure<TRow>(buffer + 4 + i * rowSize)));
                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
