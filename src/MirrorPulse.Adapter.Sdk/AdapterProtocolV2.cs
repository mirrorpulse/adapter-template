using System.Text.Json;
using System.Text.Json.Serialization;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>A source address is always scoped to one configured root.</summary>
public sealed record AdapterFileAddress(string RootKey, string Path)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootKey) || RootKey.Length > 256 || RootKey.Any(char.IsControl))
            throw new InvalidDataException("InvalidRoot");
        if (Path is null || Path.Length > 32768 || Path.Contains('\\') || Path.Contains(':') ||
            Path.StartsWith('/') || Path.Split('/').Any(p => p is "." or "..") ||
            Path.Any(char.IsControl))
            throw new InvalidDataException("InvalidPath");
    }
}

/// <summary>Both source revision and destination state are checked before mutation.</summary>
public sealed record AdapterMutationPreconditions(string? ExpectedRevision = null, bool DestinationMustBeAbsent = true);

public sealed record AdapterOperationRequest(
    Guid OperationId,
    string RootKey,
    string Path,
    string? DestinationRootKey = null,
    string? DestinationPath = null,
    AdapterMutationPreconditions? Preconditions = null,
    bool IsDirectory = false);

public sealed record AdapterCreateDirectoryRequest(Guid OperationId, string RootKey, string Path, bool MustBeAbsent = true);

public sealed record AdapterReadRequest(string RootKey, string Path, long Offset, long Length, string? ExpectedRevision = null);

public sealed record AdapterOperationResult(Guid OperationId, string RootKey, string? Revision = null);

/// <summary>Wire names are stable and independent of language-specific DTO names.</summary>
public static class AdapterProtocolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Decode<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new InvalidDataException("NullPayload");

    /// <summary>Reading a v1 address is allowed only with an explicit single-root binding.</summary>
    public static AdapterFileAddress ReadAddress(JsonElement payload, int protocolVersion, string? legacyRootKey = null)
    {
        string rootKey = protocolVersion switch
        {
            2 => payload.TryGetProperty("rootKey", out JsonElement root) && root.ValueKind == JsonValueKind.String
                ? root.GetString()! : throw new InvalidDataException("RootRequired"),
            1 when !string.IsNullOrWhiteSpace(legacyRootKey) => legacyRootKey,
            1 => throw new InvalidDataException("SingleRootBindingRequired"),
            _ => throw new InvalidDataException("ProtocolVersionUnsupported"),
        };
        if (!payload.TryGetProperty("path", out JsonElement path) || path.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("PathRequired");
        var address = new AdapterFileAddress(rootKey, path.GetString()!);
        address.Validate();
        return address;
    }

    public static void ValidateMutation(AdapterOperationRequest request, bool requiresDestination)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty) throw new InvalidDataException("OperationIdRequired");
        new AdapterFileAddress(request.RootKey, request.Path).Validate();
        if (requiresDestination && (request.DestinationRootKey is null || request.DestinationPath is null))
            throw new InvalidDataException("DestinationRequired");
        if (request.DestinationRootKey is not null || request.DestinationPath is not null)
            new AdapterFileAddress(request.DestinationRootKey!, request.DestinationPath!).Validate();
        if (request.Preconditions?.ExpectedRevision is { Length: > 8192 })
            throw new InvalidDataException("RevisionTooLarge");
    }
}
