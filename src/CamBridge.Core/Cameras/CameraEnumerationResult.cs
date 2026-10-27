namespace CamBridge.Core;

/// <summary>Cameras found across all providers, plus providers that failed to enumerate.</summary>
public sealed record CameraEnumerationResult(
    IReadOnlyList<CameraInfo> Cameras,
    IReadOnlyList<ProviderFailure> Failures);

public sealed record ProviderFailure(string ProviderId, Exception Exception);
