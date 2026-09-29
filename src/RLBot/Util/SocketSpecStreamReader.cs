using System.Net.Sockets;
using Google.FlatBuffers;
using RLBot.Flat;

namespace RLBot.Util;

/**
 * Reads messages to RLBot server according to spec:
 * https://wiki.rlbot.org/framework/sockets-specification/
 */
class SpecStreamReader
{
    private readonly NetworkStream _stream;
    private readonly byte[] _ushortReader = new byte[2];
    private readonly byte[] _payloadReader = new byte[ushort.MaxValue];
    private int _headerBytes, _payloadBytes;
    private int _payloadSize = -1;

    public SpecStreamReader(NetworkStream stream)
    {
        _stream = stream;
    }

    /// <summary>Attempt to read an incoming message</summary>
    /// <exception cref="SocketException">Thrown if there are no incoming messages.</exception>
    public CorePacket ReadOne()
    {
        // A nonblocking socket can report WouldBlock after any fragment, including one byte of
        // the length prefix. Preserve progress across calls instead of restarting ReadExactly
        // in the middle of a frame. Read directly: BufferedStream can copy buffered bytes before
        // its underlying read throws, hiding that partial progress from this state machine.
        Fill(_ushortReader, ref _headerBytes, 2);
        if (_payloadSize < 0)
        {
            _payloadSize = ReadBigEndian(_ushortReader);
            if (_payloadSize == 0) throw new InvalidDataException("Empty RLBot frame.");
        }
        Fill(_payloadReader, ref _payloadBytes, _payloadSize);
        
        ByteBuffer byteBuffer = new(_payloadReader, 0);
        _headerBytes = _payloadBytes = 0;
        _payloadSize = -1;
        return CorePacket.GetRootAsCorePacket(byteBuffer);
    }

    private void Fill(byte[] destination, ref int received, int length)
    {
        while (received < length)
        {
            int count = _stream.Read(destination, received, length - received);
            if (count == 0) throw new EndOfStreamException("RLBot closed the connection during a frame.");
            received += count;
        }
    }

    private static ushort ReadBigEndian(Span<byte> bytes)
    {
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }
}
