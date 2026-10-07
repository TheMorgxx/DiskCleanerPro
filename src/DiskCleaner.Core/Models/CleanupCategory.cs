namespace DiskCleaner.Core.Models;

/// <summary>Смысловая категория для группировки/фильтрации в UI.</summary>
public enum CleanupCategory
{
    UserTemp,
    WindowsTemp,
    NvidiaDxCache,
    DirectXShaderCache,
    CrashDumps,
    BrowserCache,
    ElectronAppCache,
    JetBrainsCache,
    GradleCache,
    NuGetCache,
    PipCache,
    NodePackageManagerCache,
    PlaywrightCache,
    AndroidCache,
    GameEngineCache,
    RobloxCache,
    CapCutCache,
    WindowsThumbnailCache,
    DeliveryOptimizationCache,
    WindowsUpdateCache,
    DriveRootTempFolder,
    ShaderCacheGeneric,
}
