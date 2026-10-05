using System.Text.Json;

namespace MirrorPulse.Adapter.Sdk;

public sealed record AdapterVersionRange(int Minimum, int Maximum);

public sealed record AdapterHello(AdapterVersionRange SupportedVersions, IReadOnlyList<string> Capabilities);

public sealed record AdapterRootBinding(string RootKey, bool Enabled, IReadOnlyDictionary<string, string> Configuration);

public sealed record AdapterReady(int SelectedVersion, IReadOnlyList<string> Capabilities,
    IReadOnlyList<AdapterRootBinding> Roots, IReadOnlyDictionary<string, string> Configuration);

public sealed record AdapterNegotiation(int? SelectedVersion, string? ErrorCode)
{
    public bool Accepted => SelectedVersion is not null;
}

/// <summary>Bootstrap Hello always uses envelope v1, including for v2-only Workers.</summary>
public static class AdapterHandshake
{
    private static readonly string[] RequiredV2Capabilities =
        ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];

    public static IReadOnlyList<string> V2Capabilities { get; } = Array.AsReadOnly(RequiredV2Capabilities);

    public static AdapterNegotiation Negotiate(AdapterHello offer, int configuredRootCount,
        AdapterVersionRange? hostVersions = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentOutOfRangeException.ThrowIfNegative(configuredRootCount);
        hostVersions ??= new(1, 2);
        if (offer.SupportedVersions is null || offer.SupportedVersions.Minimum < 1 ||
            offer.SupportedVersions.Maximum < offer.SupportedVersions.Minimum ||
            hostVersions.Minimum < 1 || hostVersions.Maximum < hostVersions.Minimum)
            return new(null, "InvalidVersionOffer");
        int selected = Math.Min(offer.SupportedVersions.Maximum, hostVersions.Maximum);
        if (selected < Math.Max(offer.SupportedVersions.Minimum, hostVersions.Minimum))
            return new(null, "ProtocolVersionUnsupported");
        if (selected == 2 && (offer.Capabilities is null ||
            RequiredV2Capabilities.Any(c => !offer.Capabilities.Contains(c, StringComparer.Ordinal))))
            return new(null, "RequiredCapabilityMissing");
        if (selected == 1 && configuredRootCount > 1)
            return new(null, "MultipleRootsRequireProtocolV2");
        return new(selected, null);
    }

    /// <summary>Older Workers had no version offer; malformed offers never use this fallback.</summary>
    public static AdapterHello ReadHello(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("InvalidHello");
        if (!payload.TryGetProperty("supportedVersions", out _)) return new(new(1, 1), []);
        return AdapterProtocolJson.Decode<AdapterHello>(System.Text.Encoding.UTF8.GetBytes(payload.GetRawText()));
    }

    public static void ValidateReady(AdapterReady ready, AdapterHello offer)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(offer);
        AdapterNegotiation result = Negotiate(offer, ready.Roots?.Count ?? 0,
            new(ready.SelectedVersion, ready.SelectedVersion));
        if (!result.Accepted) throw new InvalidDataException(result.ErrorCode);
        if (ready.SelectedVersion == 2 && (ready.Roots is null || ready.Roots.Count == 0 ||
            RequiredV2Capabilities.Any(c => !ready.Capabilities.Contains(c, StringComparer.Ordinal))))
            throw new InvalidDataException("InvalidReady");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (AdapterRootBinding root in ready.Roots ?? [])
        {
            new AdapterFileAddress(root.RootKey, "").Validate();
            if (!keys.Add(root.RootKey) || root.Configuration is null) throw new InvalidDataException("InvalidRoots");
        }
    }
}
