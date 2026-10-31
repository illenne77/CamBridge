namespace CamBridge.Core;

/// <summary>
/// Aggregates cameras from all registered providers and routes creation back to the
/// provider that reported each camera.
/// </summary>
/// <remarks>
/// The provider set is fixed at construction, which keeps the registry lock-free.
/// Load optional providers (e.g. pylon plugins) before constructing it.
/// </remarks>
public sealed class CameraRegistry
{
    private readonly ICameraProvider[] _providers;
    private readonly Dictionary<string, ICameraProvider> _byId;

    public CameraRegistry(IEnumerable<ICameraProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToArray();
        _byId = new Dictionary<string, ICameraProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (ICameraProvider provider in _providers)
        {
            if (!_byId.TryAdd(provider.ProviderId, provider))
            {
                throw new ArgumentException($"Duplicate camera provider id '{provider.ProviderId}'.", nameof(providers));
            }
        }
    }

    public IReadOnlyList<ICameraProvider> Providers => _providers;

    /// <summary>
    /// Enumerates all providers in parallel. A provider that throws is reported in
    /// <see cref="CameraEnumerationResult.Failures"/> and does not hide other providers' cameras.
    /// Results keep provider registration order.
    /// </summary>
    public async Task<CameraEnumerationResult> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        // Task.Run isolates providers whose "async" enumeration actually blocks.
        Task<IReadOnlyList<CameraInfo>>[] tasks = _providers
            .Select(p => Task.Run(() => p.EnumerateAsync(cancellationToken), cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Individual failures are collected below.
        }

        cancellationToken.ThrowIfCancellationRequested();

        var cameras = new List<CameraInfo>();
        var failures = new List<ProviderFailure>();
        for (int i = 0; i < _providers.Length; i++)
        {
            ICameraProvider provider = _providers[i];
            Task<IReadOnlyList<CameraInfo>> task = tasks[i];

            if (!task.IsCompletedSuccessfully)
            {
                Exception error = task.Exception?.GetBaseException()
                    ?? new OperationCanceledException($"Enumeration of '{provider.ProviderId}' was canceled.");
                failures.Add(new ProviderFailure(provider.ProviderId, error));
                continue;
            }

            CameraInfo? foreign = task.Result.FirstOrDefault(
                c => !string.Equals(c.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (foreign is not null)
            {
                failures.Add(new ProviderFailure(provider.ProviderId, new InvalidOperationException(
                    $"Provider '{provider.ProviderId}' returned camera '{foreign.DeviceId}' with provider id '{foreign.ProviderId}'.")));
                continue;
            }

            cameras.AddRange(task.Result);
        }

        return new CameraEnumerationResult(cameras, failures);
    }

    /// <summary>Creates a camera through the provider named in <see cref="CameraInfo.ProviderId"/>.</summary>
    public ICamera Create(CameraInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!_byId.TryGetValue(info.ProviderId, out ICameraProvider? provider))
        {
            throw new ArgumentException($"No camera provider registered with id '{info.ProviderId}'.", nameof(info));
        }

        return provider.Create(info);
    }
}
