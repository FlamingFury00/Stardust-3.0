using System.Net.Sockets;
using Google.FlatBuffers;
using RLBot.Flat;

namespace Stardust.Simulator.Protocol;

/// <summary>
/// Server side of one RLBot v5 socket: u16 big-endian length prefix + flatbuffer payload.
/// Writes CorePackets and reads InterfacePackets, exactly as the real RLBotServer does.
/// </summary>
public sealed class RLBotConnection : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream network;
    private readonly BufferedStream reader;
    private readonly BufferedStream writer;
    private readonly byte[] header = new byte[2];
    private readonly byte[] payload = new byte[ushort.MaxValue];

    public RLBotConnection(TcpClient client)
    {
        this.client = client;
        client.NoDelay = true;
        network = client.GetStream();
        reader = new BufferedStream(network, 1 << 17);
        writer = new BufferedStream(network, 1 << 17);
    }

    public string AgentId { get; set; } = "";
    public bool WantsBallPredictions { get; set; } = true;
    public bool WantsComms { get; set; } = true;

    [ThreadStatic] private static FlatBufferBuilder? sharedBuilder;

    /// <summary>Serializes a core message once so the same bytes can be written to many bots.</summary>
    public static byte[] Frame(CoreMessageUnion message)
    {
        FlatBufferBuilder builder = sharedBuilder ??= new FlatBufferBuilder(1 << 16);
        builder.Clear();
        builder.Finish(CorePacket.Pack(builder, new CorePacketT { Message = message }).Value);
        ArraySegment<byte> bytes = builder.DataBuffer.ToArraySegment(
            builder.DataBuffer.Position, builder.DataBuffer.Length - builder.DataBuffer.Position);
        if (bytes.Count > ushort.MaxValue)
            throw new InvalidOperationException($"Core message {message.Type} is too large ({bytes.Count} bytes).");
        var framed = new byte[bytes.Count + 2];
        framed[0] = (byte)(bytes.Count >> 8);
        framed[1] = (byte)(bytes.Count & 0xFF);
        Array.Copy(bytes.Array!, bytes.Offset, framed, 2, bytes.Count);
        return framed;
    }

    public void Queue(CoreMessageUnion message) => WriteFramed(Frame(message));

    public void WriteFramed(byte[] framed) => writer.Write(framed, 0, framed.Length);

    public void Flush() => writer.Flush();

    /// <summary>Blocks until the next interface message arrives or the timeout expires.</summary>
    public InterfacePacketT Read(int timeoutMilliseconds)
    {
        client.ReceiveTimeout = timeoutMilliseconds;
        reader.ReadExactly(header, 0, 2);
        int size = (header[0] << 8) | header[1];
        reader.ReadExactly(payload, 0, size);
        return InterfacePacket.GetRootAsInterfacePacket(new ByteBuffer(payload, 0)).UnPack();
    }

    public void Dispose()
    {
        try { reader.Dispose(); } catch (IOException) { }
        try { writer.Dispose(); } catch (IOException) { }
        client.Dispose();
    }
}
