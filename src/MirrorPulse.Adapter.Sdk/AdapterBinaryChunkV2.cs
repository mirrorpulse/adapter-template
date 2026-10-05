using System.Buffers.Binary;
using System.Text;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>V2 prefixes the unchanged v1 chunk with an explicit UTF-8 root address.</summary>
public static class AdapterBinaryChunkV2Codec
{
    public const int MaximumChunkBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(AdapterBinaryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        new AdapterFileAddress(chunk.RootKey!, "").Validate();
        byte[] root = StrictUtf8.GetBytes(chunk.RootKey!);
        if (root.Length > 1024 || chunk.Data.Length > MaximumChunkBytes)
            throw new InvalidDataException("ChunkTooLarge");
        byte[] legacy = AdapterBinaryChunkCodec.Encode(chunk);
        byte[] result = new byte[6 + root.Length + legacy.Length];
        "MPB2"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4, 2), checked((ushort)root.Length));
        root.CopyTo(result, 6);
        legacy.CopyTo(result, 6 + root.Length);
        return result;
    }

    public static AdapterBinaryChunk Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || !bytes[..4].SequenceEqual("MPB2"u8))
            throw new InvalidDataException("InvalidV2Chunk");
        int rootLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]);
        if (rootLength is < 1 or > 1024 || bytes.Length < 6 + rootLength ||
            bytes.Length > 6 + 1024 + 109 + MaximumChunkBytes)
            throw new InvalidDataException("InvalidV2ChunkLength");
        string root;
        try { root = StrictUtf8.GetString(bytes.Slice(6, rootLength)); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("InvalidRootEncoding", exception); }
        new AdapterFileAddress(root, "").Validate();
        AdapterBinaryChunk chunk = AdapterBinaryChunkCodec.Decode(bytes[(6 + rootLength)..]);
        if (chunk.Data.Length > MaximumChunkBytes) throw new InvalidDataException("ChunkTooLarge");
        return chunk with { RootKey = root };
    }
}

/// <summary>Validates a stream incrementally without retaining its data.</summary>
public sealed class AdapterStreamBinding
{
    private readonly Guid _requestId;
    private readonly Guid _instanceId;
    private readonly Guid _sessionId;
    private readonly Guid _streamId;
    private readonly string _rootKey;
    private readonly long _end;
    private long _next;

    public AdapterStreamBinding(Guid requestId, Guid instanceId, Guid sessionId, Guid streamId,
        string rootKey, long offset, long length)
    {
        if (requestId == Guid.Empty || instanceId == Guid.Empty || sessionId == Guid.Empty || streamId == Guid.Empty)
            throw new ArgumentException("Nonempty stream identity required.");
        new AdapterFileAddress(rootKey, "").Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _requestId = requestId;
        _instanceId = instanceId;
        _sessionId = sessionId;
        _streamId = streamId;
        _rootKey = rootKey;
        _next = offset;
        _end = checked(offset + length);
    }

    public bool Completed { get; private set; }

    public void Accept(AdapterBinaryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (Completed || chunk.RequestId != _requestId || chunk.InstanceId != _instanceId ||
            chunk.WorkerSessionId != _sessionId || chunk.StreamId != _streamId ||
            chunk.RootKey != _rootKey || chunk.Offset != _next ||
            chunk.Data.Length > AdapterBinaryChunkV2Codec.MaximumChunkBytes ||
            chunk.Data.Length > _end - _next || (!chunk.EndOfStream && chunk.Data.IsEmpty))
            throw new InvalidDataException("StreamBindingMismatch");
        long next = checked(_next + chunk.Data.Length);
        if (chunk.EndOfStream != (next == _end)) throw new InvalidDataException("StreamLengthMismatch");
        _next = next;
        Completed = chunk.EndOfStream;
    }
}
