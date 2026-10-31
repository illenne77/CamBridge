using System.Runtime.CompilerServices;
using CamBridge.Core;

namespace CamBridge.Sources.Pylon;

/// <summary>
/// Entry point that compiles with or without the Basler pylon SDK. Applications call
/// <see cref="TryCreate"/> and register the provider only when it returns one, so the same code
/// runs on machines without pylon (design principle 6).
/// </summary>
public static class PylonProviderFactory
{
    public const string ProviderId = "pylon";

    /// <summary>True when this assembly was built against the pylon SDK.</summary>
    public static bool IsBuiltWithPylon =>
#if HAS_PYLON
        true;
#else
        false;
#endif

    /// <returns>The provider, or null with <paramref name="unavailableReason"/> explaining why.</returns>
    public static ICameraProvider? TryCreate(PylonOptions? options, out string? unavailableReason)
    {
#if HAS_PYLON
        PylonRuntime.PrepareEnvironment();
        try
        {
            ICameraProvider provider = Create(options ?? new PylonOptions());
            unavailableReason = null;
            return provider;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Basler.Pylon.dll missing from the output (e.g. built elsewhere and copied without it).
            unavailableReason = $"The pylon .NET API could not be loaded: {ex.Message}";
            return null;
        }
#else
        _ = options;
        unavailableReason = "Built without the Basler pylon SDK. Install pylon and rebuild to enable Basler cameras.";
        return null;
#endif
    }

#if HAS_PYLON
    // Separate method: the JIT resolves Basler.Pylon types only when this is compiled, inside the try above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PylonProvider Create(PylonOptions options) => new(options);
#endif
}
