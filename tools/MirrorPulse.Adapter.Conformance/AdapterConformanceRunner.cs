using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Conformance;

/// <summary>Runs the controlled memory-source profile through real process and pipe boundaries.</summary>
public static class AdapterConformanceRunner
{
    public static async Task RunAsync(string executable, string transferCache, CancellationToken cancellationToken,
        IReadOnlyList<string>? workerArguments = null)
    {
        string pipeName = "mp-conformance-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Guid instance = Guid.NewGuid();
        Guid session = Guid.NewGuid();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in workerArguments ?? []) start.ArgumentList.Add(argument);
        foreach (string argument in new[] { "--instance-id", instance.ToString("D"), "--worker-session-id", session.ToString("D"), "--pipe-name", pipeName })
            start.ArgumentList.Add(argument);
        start.Environment["MP_TRANSFER_CACHE_DIR"] = transferCache;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Worker launch failed.");
        Task output = DrainAsync(process.StandardOutput, cancellationToken);
        Task error = DrainAsync(process.StandardError, cancellationToken);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var wire = new HostWire(pipe, instance, session);
            AdapterControlFrame hello = await wire.ReadControlAsync(cancellationToken).ConfigureAwait(false);
            Check(hello.MessageType == "Hello" && hello.ProtocolVersion == 1 && !hello.IsResponse, "Hello bootstrap");
            AdapterHello offer = AdapterHandshake.ReadHello(hello.Payload);
            Check(AdapterHandshake.Negotiate(offer, 3).SelectedVersion == 2, "V2 negotiation");
            AdapterRootBinding[] roots = [
                new("left", true, new Dictionary<string, string> { ["sourcePath"] = "left-source" }),
                new("right", true, new Dictionary<string, string> { ["sourcePath"] = "right-source" }),
                new("offline", false, new Dictionary<string, string> { ["sourcePath"] = "offline-source" })];
            await wire.SendAsync("Ready", hello.RequestId, true, new AdapterReady(2, AdapterHandshake.V2Capabilities, roots, new Dictionary<string, string>()), cancellationToken).ConfigureAwait(false);
            AdapterControlFrame connected = await wire.ReadControlAsync(cancellationToken).ConfigureAwait(false);
            Check(connected.MessageType == "Connected" && connected.ProtocolVersion == 2 && !connected.IsResponse, "Connected selected version");
            foreach (string root in new[] { "left", "right" })
            {
                AdapterControlFrame list = await wire.RequestAsync("List", root, new { rootKey = root, path = "", pageSize = 1, cursor = (string?)null }, cancellationToken).ConfigureAwait(false);
                Check(list.MessageType == "DirectoryPage" && list.Payload.GetProperty("entries").GetArrayLength() == 1, "One bounded directory page");
                JsonElement entry = list.Payload.GetProperty("entries")[0];
                Check(entry.GetProperty("remoteId").GetString() == "readme.txt", "Same raw remote ID in both roots");
                long length = entry.GetProperty("length").GetInt64();
                byte[] read = await wire.ReadRangeAsync(root, "readme.txt", length, cancellationToken).ConfigureAwait(false);
                Check(Encoding.UTF8.GetString(read) == "Memory source: " + root + "\n", "Root-scoped bytes");
            }
            AdapterControlFrame unknown = await wire.RequestAsync("Stat", "unknown", new { rootKey = "unknown", path = "readme.txt" }, cancellationToken).ConfigureAwait(false);
            Check(unknown.MessageType == "OperationError" && unknown.Payload.GetProperty("code").GetString() == "UnknownRoot", "Unknown root rejected");
            AdapterControlFrame offline = await wire.RequestAsync("Stat", "offline", new { rootKey = "offline", path = "readme.txt" }, cancellationToken).ConfigureAwait(false);
            Check(offline.MessageType == "OperationError" && offline.Payload.GetProperty("code").GetString() == "RootOffline", "Disabled root stays offline");
            Guid canceledOperation = Guid.NewGuid();
            Guid canceledRequest = Guid.NewGuid();
            Guid canceledStream = Guid.NewGuid();
            await wire.SendAsync("Upload", canceledRequest, false, new
            {
                rootKey = "left",
                path = "canceled.bin",
                operationId = canceledOperation,
                streamId = canceledStream,
                length = 2,
                preconditions = new AdapterMutationPreconditions()
            }, cancellationToken).ConfigureAwait(false);
            AdapterControlFrame uploadReady = await wire.ReadResponseAsync(canceledRequest, "left", cancellationToken).ConfigureAwait(false);
            Check(uploadReady.MessageType == "UploadReady", "Cancelable upload ready");
            await wire.SendChunkAsync(new(canceledRequest, instance, session, canceledStream, 0, new byte[] { 1 }, false) { RootKey = "left" }, cancellationToken).ConfigureAwait(false);
            Guid cancelId = Guid.NewGuid();
            await wire.SendAsync("Cancel", cancelId, false, new { rootKey = "left", targetRequestId = canceledRequest, operationId = canceledOperation }, cancellationToken).ConfigureAwait(false);
            AdapterControlFrame canceled = await wire.ReadResponseAsync(canceledRequest, "left", cancellationToken).ConfigureAwait(false);
            Check(canceled.MessageType == "OperationError" && canceled.Payload.GetProperty("code").GetString() == "Canceled", "Canceled target terminates");
            AdapterControlFrame cancelAck = await wire.ReadResponseAsync(cancelId, "left", cancellationToken).ConfigureAwait(false);
            Check(cancelAck.MessageType == "CancelAck" && cancelAck.Payload.GetProperty("targetRequestId").GetGuid() == canceledRequest, "Cancel acknowledged after cleanup");
            Check(!Directory.EnumerateFiles(transferCache).Any(), "Canceled lease was actually removed");
            Check(await wire.StatAsync("left", "canceled.bin", cancellationToken).ConfigureAwait(false) is null, "Canceled upload never committed");
            byte[] content = new byte[AdapterBinaryChunkV2Codec.MaximumChunkBytes + 17];
            Random.Shared.NextBytes(content);
            string revision = await wire.UploadAsync("left", "uploaded.bin", content, cancellationToken).ConfigureAwait(false);
            Check(await wire.StatAsync("right", "uploaded.bin", cancellationToken).ConfigureAwait(false) is null, "Upload did not cross roots");
            Guid moveOperation = Guid.NewGuid();
            var move = new AdapterOperationRequest(moveOperation, "left", "uploaded.bin", "right", "moved.bin", new(revision));
            AdapterControlFrame moved = await wire.RequestAsync("Move", "left", move, cancellationToken).ConfigureAwait(false);
            Check(moved.MessageType == "MutationComplete" && moved.Payload.GetProperty("operationId").GetGuid() == moveOperation, "Move operation binding");
            AdapterControlFrame retried = await wire.RequestAsync("Move", "left", move, cancellationToken).ConfigureAwait(false);
            Check(retried.MessageType == "MutationComplete" && retried.Payload.GetProperty("revision").GetString() == revision, "Stable mutation retry");
            AdapterControlFrame equivalent = await wire.RequestAsync("Move", "left", new
            {
                preconditions = new { destinationMustBeAbsent = true, expectedRevision = revision },
                futureOptionalField = "ignored",
                path = "uploaded.bin",
                rootKey = "left",
                operationId = moveOperation,
                destinationPath = "moved.bin",
                destinationRootKey = "right",
            }, cancellationToken).ConfigureAwait(false);
            Check(equivalent.MessageType == "MutationComplete" && equivalent.Payload.GetProperty("revision").GetString() == revision,
                "Semantic retry ignores JSON property order and optional fields");
            AdapterControlFrame rebound = await wire.RequestAsync("Move", "left", move with { DestinationPath = "rebound.bin" }, cancellationToken).ConfigureAwait(false);
            Check(rebound.MessageType == "OperationError" && rebound.Payload.GetProperty("code").GetString() == "OperationBindingMismatch",
                "Accepted operation cannot be rebound");
            Check(await wire.StatAsync("right", "rebound.bin", cancellationToken).ConfigureAwait(false) is null, "Rebound mutation did not execute");
            byte[] firstRange = await wire.ReadRangeAsync("right", "moved.bin", 32, cancellationToken).ConfigureAwait(false);
            Check(firstRange.AsSpan().SequenceEqual(content.AsSpan(0, 32)), "Cross-root move bytes");
            AdapterControlFrame created = await wire.RequestAsync("CreateDirectory", "left", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "empty"), cancellationToken).ConfigureAwait(false);
            Check(created.MessageType == "MutationComplete", "Create directory");
            AdapterControlFrame deleted = await wire.RequestAsync("Delete", "right", new AdapterOperationRequest(Guid.NewGuid(), "right", "moved.bin", Preconditions: new(revision)), cancellationToken).ConfigureAwait(false);
            Check(deleted.MessageType == "MutationComplete", "Delete");
            Check(await wire.StatAsync("right", "moved.bin", cancellationToken).ConfigureAwait(false) is null, "Deleted file absent");
            Check(!Directory.EnumerateFiles(transferCache).Any(), "Successful upload lease removed");
            await wire.SendAsync("Stop", Guid.NewGuid(), false, new { }, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            Check(process.ExitCode == 0, "Worker clean exit");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { } }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidDataException("Conformance failed: " + name);
    }

    private sealed class HostWire(Stream pipe, Guid instance, Guid session)
    {
        public async Task SendAsync(string message, Guid request, bool response, object payload, CancellationToken token)
        {
            var frame = new AdapterControlFrame(2, message, request, instance, session, response,
                AdapterProtocolJson.ToElement(payload));
            await WriteAsync(AdapterProtocolJson.Encode(frame), token).ConfigureAwait(false);
        }

        public async Task<AdapterControlFrame> ReadControlAsync(CancellationToken token)
        {
            AdapterControlFrame frame = AdapterProtocolJson.Decode<AdapterControlFrame>(await ReadAsync(token).ConfigureAwait(false));
            Check(frame.InstanceId == instance && frame.WorkerSessionId == session && frame.RequestId != Guid.Empty, "Frame session");
            return frame;
        }

        public async Task<AdapterControlFrame> ReadResponseAsync(Guid request, string root, CancellationToken token)
        {
            AdapterControlFrame response = await ReadControlAsync(token).ConfigureAwait(false);
            Check(response.IsResponse && response.ProtocolVersion == 2 && response.RequestId == request && response.Payload.GetProperty("rootKey").GetString() == root, "Response root correlation");
            return response;
        }

        public async Task<AdapterControlFrame> RequestAsync(string message, string root, object payload, CancellationToken token)
        {
            Guid request = Guid.NewGuid();
            await SendAsync(message, request, false, payload, token).ConfigureAwait(false);
            return await ReadResponseAsync(request, root, token).ConfigureAwait(false);
        }

        public async Task<string?> StatAsync(string root, string path, CancellationToken token)
        {
            AdapterControlFrame response = await RequestAsync("Stat", root, new { rootKey = root, path }, token).ConfigureAwait(false);
            Check(response.MessageType == "StatResult", "Stat response");
            return response.Payload.TryGetProperty("revision", out JsonElement revision) && revision.ValueKind != JsonValueKind.Null ? revision.GetString() : null;
        }

        public async Task<byte[]> ReadRangeAsync(string root, string path, long length, CancellationToken token)
        {
            Guid request = Guid.NewGuid();
            await SendAsync("ReadRange", request, false, new AdapterReadRequest(root, path, 0, length), token).ConfigureAwait(false);
            AdapterControlFrame ready = await ReadResponseAsync(request, root, token).ConfigureAwait(false);
            Check(ready.MessageType == "ReadRangeReady" && ready.Payload.GetProperty("length").GetInt64() == length, "Declared range length");
            AdapterBinaryChunk chunk = AdapterBinaryChunkV2Codec.Decode(await ReadAsync(token).ConfigureAwait(false));
            var binding = new AdapterStreamBinding(request, instance, session, ready.Payload.GetProperty("streamId").GetGuid(), root, 0, length);
            binding.Accept(chunk);
            Check(binding.Completed, "Range completion");
            return chunk.Data.ToArray();
        }

        public async Task<string> UploadAsync(string root, string path, byte[] bytes, CancellationToken token)
        {
            Guid request = Guid.NewGuid();
            Guid operation = Guid.NewGuid();
            Guid stream = Guid.NewGuid();
            await SendAsync("Upload", request, false, new { rootKey = root, path, operationId = operation, streamId = stream, length = bytes.Length, preconditions = new AdapterMutationPreconditions() }, token).ConfigureAwait(false);
            AdapterControlFrame ready = await ReadResponseAsync(request, root, token).ConfigureAwait(false);
            Check(ready.MessageType == "UploadReady" && ready.Payload.GetProperty("streamId").GetGuid() == stream && ready.Payload.GetProperty("operationId").GetGuid() == operation, "Upload association");
            for (int offset = 0; offset < bytes.Length;)
            {
                int length = Math.Min(AdapterBinaryChunkV2Codec.MaximumChunkBytes, bytes.Length - offset);
                await SendChunkAsync(new(request, instance, session, stream, offset, bytes.AsMemory(offset, length), offset + length == bytes.Length) { RootKey = root }, token).ConfigureAwait(false);
                offset += length;
            }
            AdapterControlFrame completed = await ReadResponseAsync(request, root, token).ConfigureAwait(false);
            Check(completed.MessageType == "UploadComplete" && completed.Payload.GetProperty("operationId").GetGuid() == operation, "Upload completed");
            return completed.Payload.GetProperty("revision").GetString()!;
        }

        public Task SendChunkAsync(AdapterBinaryChunk chunk, CancellationToken token) => WriteAsync(AdapterBinaryChunkV2Codec.Encode(chunk), token);

        private async Task WriteAsync(byte[] bytes, CancellationToken token)
        {
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)bytes.Length));
            await pipe.WriteAsync(prefix, token).ConfigureAwait(false);
            await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
            await pipe.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadAsync(CancellationToken token)
        {
            byte[] prefix = new byte[4];
            await pipe.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (length > AdapterPipeFrameLimits.MaxPayloadBytes) throw new InvalidDataException("FrameTooLarge");
            byte[] payload = new byte[length];
            await pipe.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            return payload;
        }
    }
}
