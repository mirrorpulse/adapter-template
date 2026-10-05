using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.SampleWorker;

public static class SampleWorkerMarker;

/// <summary>An ephemeral example source; only bounded transfer leases touch disk.</summary>
internal sealed class MemoryWorker : IAsyncDisposable
{
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumSourceBytes = 8 * 1024 * 1024;
    private readonly AdapterControlChannel _channel;
    private readonly AdapterWorkerProcessArguments _arguments;
    private readonly Dictionary<string, AdapterRootBinding> _roots;
    private readonly string _cache;
    private readonly Dictionary<(string Root, string Path), byte[]> _files = new();
    private readonly HashSet<(string Root, string Path)> _directories = [];
    private readonly Dictionary<Guid, PendingUpload> _uploads = new();
    private readonly Dictionary<Guid, AcceptedOperation> _accepted = new();
    private readonly Queue<Guid> _acceptedOrder = new();

    public MemoryWorker(AdapterControlChannel channel, AdapterWorkerProcessArguments arguments,
        IReadOnlyList<AdapterRootBinding> roots, string cache)
    {
        _channel = channel;
        _arguments = arguments;
        _roots = roots.ToDictionary(root => root.RootKey, StringComparer.Ordinal);
        if (roots.Count > 64) throw new InvalidDataException("RootLimit");
        _cache = cache;
        foreach (AdapterRootBinding root in roots)
            _files[(root.RootKey, "readme.txt")] = Encoding.UTF8.GetBytes("Memory source: " + root.RootKey + "\n");
    }

    public async Task RunAsync()
    {
        while (true)
        {
            AdapterWorkerFrame frame = await _channel.ReadNextAsync().ConfigureAwait(false);
            if (frame.Chunk is { } chunk)
            {
                await ReceiveChunkAsync(chunk).ConfigureAwait(false);
                continue;
            }
            AdapterControlFrame command = frame.Control!;
            if (command.IsResponse) throw new InvalidDataException("UnexpectedResponse");
            if (command.MessageType == "Stop") return;
            string? rootKey = null;
            Guid? operationId = null;
            try
            {
                rootKey = command.Payload.GetProperty("rootKey").GetString();
                if (command.Payload.TryGetProperty("operationId", out JsonElement operation)) operationId = operation.GetGuid();
                if (rootKey is null || !_roots.TryGetValue(rootKey, out AdapterRootBinding? root)) throw new InvalidDataException("UnknownRoot");
                if (!root.Enabled) throw new InvalidDataException("RootOffline");
                if (command.MessageType == "Cancel")
                {
                    await CancelAsync(command, rootKey).ConfigureAwait(false);
                    continue;
                }
                AdapterFileAddress address = AdapterProtocolJson.ReadAddress(command.Payload, 2);
                if (address.Path.Length > 4096) throw new InvalidDataException("PathLimit");
                switch (command.MessageType)
                {
                    case "Stat":
                        await ReplyAsync(command, "StatResult", new { rootKey, revision = Revision(rootKey, address.Path) }).ConfigureAwait(false);
                        break;
                    case "List":
                        await ListAsync(command, address).ConfigureAwait(false);
                        break;
                    case "ReadRange":
                        await ReadAsync(command, address).ConfigureAwait(false);
                        break;
                    case "Upload":
                        await BeginUploadAsync(command, address).ConfigureAwait(false);
                        break;
                    case "Move":
                    case "Delete":
                    case "CreateDirectory":
                        await MutateAsync(command, address).ConfigureAwait(false);
                        break;
                    default: throw new InvalidDataException("OperationUnsupported");
                }
            }
            catch (InvalidDataException exception)
            {
                await ReplyAsync(command, "OperationError", new { rootKey, operationId, code = exception.Message }).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadAsync(AdapterControlFrame command, AdapterFileAddress address)
    {
        if (!_files.TryGetValue((address.RootKey, address.Path), out byte[]? bytes)) throw new InvalidDataException("NotFound");
        long offset = command.Payload.GetProperty("offset").GetInt64();
        long length = command.Payload.GetProperty("length").GetInt64();
        if (offset < 0 || length is < 1 or > AdapterBinaryChunkV2Codec.MaximumChunkBytes || offset > bytes.Length || length > bytes.Length - offset)
            throw new InvalidDataException("InvalidRange");
        if (command.Payload.TryGetProperty("expectedRevision", out JsonElement expected) && expected.ValueKind == JsonValueKind.String && expected.GetString() != Revision(address.RootKey, address.Path))
            throw new InvalidDataException("RemoteConflict");
        Guid streamId = Guid.NewGuid();
        await ReplyAsync(command, "ReadRangeReady", new { rootKey = address.RootKey, streamId, length }).ConfigureAwait(false);
        await _channel.SendChunkAsync(new(command.RequestId, _arguments.InstanceId, _arguments.WorkerSessionId,
            streamId, offset, bytes.AsMemory((int)offset, (int)length), true)
        { RootKey = address.RootKey }).ConfigureAwait(false);
    }

    private async Task ListAsync(AdapterControlFrame command, AdapterFileAddress address)
    {
        int pageSize = command.Payload.GetProperty("pageSize").GetInt32();
        if (pageSize is < 1 or > 512) throw new InvalidDataException("InvalidPageSize");
        int index = 0;
        if (command.Payload.TryGetProperty("cursor", out JsonElement cursor) && cursor.ValueKind == JsonValueKind.String)
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.GetString()!));
            string[] parts = decoded.Split('\0');
            if (parts.Length != 3 || parts[0] != address.RootKey || parts[1] != address.Path ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0)
                throw new InvalidDataException("InvalidCursor");
        }
        var items = _files.Keys.Concat(_directories).Where(key => key.Root == address.RootKey && Parent(key.Path) == address.Path)
            .OrderBy(key => key.Path, StringComparer.Ordinal).ToArray();
        if (index > items.Length) throw new InvalidDataException("InvalidCursor");
        var page = items.Skip(index).Take(pageSize).Select(key => new
        {
            remoteId = key.Path,
            remoteRevision = Revision(key.Root, key.Path),
            itemKind = _directories.Contains(key) ? "Directory" : "File",
            relativePath = key.Path,
            length = _files.TryGetValue(key, out byte[]? data) ? (long?)data.Length : null,
            isDeleted = false,
        }).ToArray();
        int next = index + page.Length;
        await ReplyAsync(command, "DirectoryPage", new
        {
            rootKey = address.RootKey,
            entries = page,
            isComplete = next == items.Length,
            cursor = next == items.Length ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(address.RootKey + "\0" + address.Path + "\0" + next.ToString(CultureInfo.InvariantCulture))),
        }).ConfigureAwait(false);
    }

    private async Task BeginUploadAsync(AdapterControlFrame command, AdapterFileAddress address)
    {
        Guid operation = RequiredOperation(command);
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid stream = command.Payload.GetProperty("streamId").GetGuid();
        if (length is < 0 or > MaximumFileBytes || _uploads.Count >= 4 || address.Path.Length == 0 || stream == Guid.Empty)
            throw new InvalidDataException("UploadLimit");
        if (_uploads.Values.Any(pending => pending.OperationId == operation)) throw new InvalidDataException("OperationInProgress");
        if (_directories.Contains((address.RootKey, address.Path))) throw new InvalidDataException("ItemKindMismatch");
        CheckParent(address);
        if (_files.Count + _directories.Count >= 4096 && !_files.ContainsKey((address.RootKey, address.Path))) throw new InvalidDataException("ItemLimit");
        string fingerprint = Fingerprint(command, address);
        bool replay = TryReplay(operation, fingerprint, out _);
        if (!replay) CheckSource(command, address, creating: true);
        var lease = new AdapterTransferLease(_cache);
        try
        {
            var binding = new AdapterStreamBinding(command.RequestId, _arguments.InstanceId, _arguments.WorkerSessionId, stream,
                address.RootKey, 0, length);
            _uploads.Add(command.RequestId, new(command, address, operation, fingerprint, binding, lease, replay));
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
        await ReplyAsync(command, "UploadReady", new { rootKey = address.RootKey, operationId = operation, streamId = stream }).ConfigureAwait(false);
    }

    private async Task ReceiveChunkAsync(AdapterBinaryChunk chunk)
    {
        if (!_uploads.TryGetValue(chunk.RequestId, out PendingUpload? upload)) throw new InvalidDataException("UnexpectedChunk");
        upload.Binding.Accept(chunk);
        await upload.Lease.Stream.WriteAsync(chunk.Data).ConfigureAwait(false);
        if (!upload.Binding.Completed) return;
        _uploads.Remove(chunk.RequestId);
        try
        {
            upload.Lease.Stream.Position = 0;
            byte[] content = new byte[checked((int)upload.Lease.Stream.Length)];
            await upload.Lease.Stream.ReadExactlyAsync(content).ConfigureAwait(false);
            string revision = Convert.ToHexString(SHA256.HashData(content));
            if (upload.Replay)
            {
                if (_accepted[upload.OperationId].Revision != revision) throw new InvalidDataException("OperationBindingMismatch");
            }
            else
            {
                CheckSource(upload.Command, upload.Address, creating: true);
                int previous = _files.TryGetValue((upload.Address.RootKey, upload.Address.Path), out byte[]? old) ? old.Length : 0;
                if (_files.Values.Sum(bytes => bytes.Length) - previous + content.Length > MaximumSourceBytes)
                    throw new InvalidDataException("SourceMemoryLimit");
                _files[(upload.Address.RootKey, upload.Address.Path)] = content;
                Remember(upload.OperationId, upload.Fingerprint, revision);
            }
            await upload.Lease.DisposeAsync().ConfigureAwait(false);
            await ReplyAsync(upload.Command, "UploadComplete", new { rootKey = upload.Address.RootKey, operationId = upload.OperationId, revision }).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await ReplyAsync(upload.Command, "OperationError", new { rootKey = upload.Address.RootKey, operationId = upload.OperationId, code = exception.Message }).ConfigureAwait(false);
        }
        finally { await upload.Lease.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task MutateAsync(AdapterControlFrame command, AdapterFileAddress address)
    {
        Guid operation = RequiredOperation(command);
        string fingerprint = Fingerprint(command, address);
        if (TryReplay(operation, fingerprint, out AcceptedOperation? accepted))
        {
            await ReplyAsync(command, "MutationComplete", new { rootKey = address.RootKey, operationId = operation, revision = accepted!.Revision }).ConfigureAwait(false);
            return;
        }
        if (address.Path.Length == 0) throw new InvalidDataException("RootMutationForbidden");
        string? revision;
        if (command.MessageType == "CreateDirectory")
        {
            CheckParent(address);
            if (_files.Count + _directories.Count >= 4096) throw new InvalidDataException("ItemLimit");
            if (Revision(address.RootKey, address.Path) is not null &&
                (command.Payload.GetProperty("mustBeAbsent").GetBoolean() || !_directories.Contains((address.RootKey, address.Path))))
                throw new InvalidDataException("DestinationExists");
            _directories.Add((address.RootKey, address.Path));
            revision = "directory";
        }
        else
        {
            bool directory = command.Payload.TryGetProperty("isDirectory", out JsonElement kind) && kind.GetBoolean();
            if (directory != _directories.Contains((address.RootKey, address.Path))) throw new InvalidDataException("ItemKindMismatch");
            CheckSource(command, address, creating: false);
            if (command.MessageType == "Delete")
            {
                if (_directories.Contains((address.RootKey, address.Path)) && _files.Keys.Concat(_directories).Any(key =>
                    key.Root == address.RootKey && key.Path.StartsWith(address.Path + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("DirectoryNotEmpty");
                _files.Remove((address.RootKey, address.Path));
                _directories.Remove((address.RootKey, address.Path));
                revision = null;
            }
            else
            {
                var destination = new AdapterFileAddress(command.Payload.GetProperty("destinationRootKey").GetString()!, command.Payload.GetProperty("destinationPath").GetString()!);
                destination.Validate();
                CheckParent(destination);
                if (destination.Path.Length == 0) throw new InvalidDataException("RootMutationForbidden");
                if (!_roots.TryGetValue(destination.RootKey, out AdapterRootBinding? root) || !root.Enabled)
                    throw new InvalidDataException("UnknownDestinationRoot");
                if (Revision(destination.RootKey, destination.Path) is not null) throw new InvalidDataException("DestinationExists");
                if (!_files.Remove((address.RootKey, address.Path), out byte[]? content)) throw new InvalidDataException("DirectoryMoveUnsupported");
                _files[(destination.RootKey, destination.Path)] = content;
                revision = Convert.ToHexString(SHA256.HashData(content));
            }
        }
        Remember(operation, fingerprint, revision);
        await ReplyAsync(command, "MutationComplete", new { rootKey = address.RootKey, operationId = operation, revision }).ConfigureAwait(false);
    }

    private async Task CancelAsync(AdapterControlFrame cancel, string rootKey)
    {
        Guid target = cancel.Payload.GetProperty("targetRequestId").GetGuid();
        string status = "alreadyCompleted";
        if (_uploads.TryGetValue(target, out PendingUpload? upload))
        {
            if (upload.Address.RootKey != rootKey) throw new InvalidDataException("CancelRootMismatch");
            if (cancel.Payload.TryGetProperty("operationId", out JsonElement operation) && operation.ValueKind != JsonValueKind.Null &&
                operation.GetGuid() != upload.OperationId) throw new InvalidDataException("CancelOperationMismatch");
            _uploads.Remove(target);
            await upload.Lease.CancelAsync().ConfigureAwait(false);
            await ReplyAsync(upload.Command, "OperationError", new { rootKey, operationId = upload.OperationId, code = "Canceled" }).ConfigureAwait(false);
            status = "canceled";
        }
        await ReplyAsync(cancel, "CancelAck", new { rootKey, targetRequestId = target, status }).ConfigureAwait(false);
    }

    private void CheckSource(AdapterControlFrame command, AdapterFileAddress address, bool creating)
    {
        JsonElement conditions = command.Payload.GetProperty("preconditions");
        string? expected = conditions.TryGetProperty("expectedRevision", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string? actual = Revision(address.RootKey, address.Path);
        if (actual != expected || (creating && conditions.GetProperty("destinationMustBeAbsent").GetBoolean() && actual is not null))
            throw new InvalidDataException("RemoteConflict");
    }

    private string? Revision(string root, string path) => _files.TryGetValue((root, path), out byte[]? data)
        ? Convert.ToHexString(SHA256.HashData(data)) : _directories.Contains((root, path)) ? "directory" : null;

    private static string Parent(string path) => path.LastIndexOf('/') is var separator && separator >= 0 ? path[..separator] : "";

    private void CheckParent(AdapterFileAddress address)
    {
        string parent = Parent(address.Path);
        if (parent.Length != 0 && !_directories.Contains((address.RootKey, parent))) throw new InvalidDataException("ParentNotFound");
    }

    private static Guid RequiredOperation(AdapterControlFrame command) => command.Payload.GetProperty("operationId").GetGuid() is var operation && operation != Guid.Empty
        ? operation : throw new InvalidDataException("OperationIdRequired");

    private static string Fingerprint(AdapterControlFrame command, AdapterFileAddress address)
    {
        // Bind semantic inputs, independent of JSON order, transport IDs and
        // unknown optional fields. Content is checked separately on upload replay.
        JsonElement payload = command.Payload;
        AdapterMutationPreconditions? conditions = command.MessageType == "CreateDirectory" ? null :
            AdapterProtocolJson.Decode<AdapterMutationPreconditions>(Encoding.UTF8.GetBytes(payload.GetProperty("preconditions").GetRawText()));
        return Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new
        {
            command.MessageType,
            address.RootKey,
            address.Path,
            preconditions = conditions,
            destinationRootKey = command.MessageType == "Move" ? payload.GetProperty("destinationRootKey").GetString() : null,
            destinationPath = command.MessageType == "Move" ? payload.GetProperty("destinationPath").GetString() : null,
            length = command.MessageType == "Upload" ? (long?)payload.GetProperty("length").GetInt64() : null,
            isDirectory = payload.TryGetProperty("isDirectory", out JsonElement kind) && kind.GetBoolean(),
            mustBeAbsent = command.MessageType != "CreateDirectory" || !payload.TryGetProperty("mustBeAbsent", out JsonElement absent) || absent.GetBoolean(),
        })));
    }

    private bool TryReplay(Guid operation, string fingerprint, out AcceptedOperation? accepted)
    {
        if (!_accepted.TryGetValue(operation, out accepted)) return false;
        if (accepted.Fingerprint != fingerprint) throw new InvalidDataException("OperationBindingMismatch");
        return true;
    }

    private void Remember(Guid operation, string fingerprint, string? revision)
    {
        _accepted.Add(operation, new(fingerprint, revision));
        _acceptedOrder.Enqueue(operation);
        if (_acceptedOrder.Count > 256) _accepted.Remove(_acceptedOrder.Dequeue());
    }

    private ValueTask ReplyAsync(AdapterControlFrame command, string type, object payload) => _channel.SendAsync(type, command.RequestId, true, payload);

    public async ValueTask DisposeAsync()
    {
        foreach (PendingUpload upload in _uploads.Values) await upload.Lease.DisposeAsync().ConfigureAwait(false);
        _uploads.Clear();
    }

    private sealed record AcceptedOperation(string Fingerprint, string? Revision);
    private sealed record PendingUpload(AdapterControlFrame Command, AdapterFileAddress Address, Guid OperationId, string Fingerprint,
        AdapterStreamBinding Binding, AdapterTransferLease Lease, bool Replay);
}
