// -----------------------------------------------------------------------
// <copyright file="NuGetInstaller.cs" company="Altemiq">
// Copyright (c) Altemiq. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Altemiq.NuGet.Promote;

using global::NuGet.Common;
using global::NuGet.Protocol;

/// <summary>
/// The <see cref="NuGet"/> installer.
/// </summary>
internal static class NuGetInstaller
{
    /// <summary>
    /// Gets the source key.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="root">The configuration root.</param>
    /// <returns>The source key.</returns>
    public static string? GetSourceKey(string source, string? root = default)
    {
        var settings = global::NuGet.Configuration.Settings.LoadDefaultSettings(root);
        var repository = GetRepositories(settings, [source]).FirstOrDefault();
        return repository?.PackageSource.Credentials?.Password;
    }

    /// <summary>
    /// Extracts the package.
    /// </summary>
    /// <param name="nupkg">The nuget package.</param>
    /// <param name="log">The log.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The package identity, and folder that it was extracted to.</returns>
    public static async Task<(global::NuGet.Packaging.Core.PackageIdentity? Identity, DirectoryInfo? SourceFolder)> ExtractAsync(FileInfo nupkg, ILogger log, CancellationToken cancellationToken)
    {
        var fileStream = File.OpenRead(nupkg.FullName);
        await using (fileStream.ConfigureAwait(false))
        {
            return await ExtractAsync(fileStream, log, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Extracts the package.
    /// </summary>
    /// <param name="stream">The nuget stream.</param>
    /// <param name="log">The log.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The package identity, and folder that it was extracted to.</returns>
    public static async Task<(global::NuGet.Packaging.Core.PackageIdentity? Identity, DirectoryInfo? SourceFolder)> ExtractAsync(Stream stream, ILogger log, CancellationToken cancellationToken)
    {
        var reader = new global::NuGet.Packaging.PackageArchiveReader(stream);
        var nuspecReader = await reader.GetNuspecReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!nuspecReader.GetVersion().IsPrerelease)
        {
            return default;
        }

        var identity = nuspecReader.GetIdentity();
        var sourceFolder = new DirectoryInfo(Path.Join(Path.GetTempPath(), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{identity.Id}/{identity.Version}")));
        sourceFolder.Create();

        var files = await reader.GetFilesAsync(cancellationToken).ConfigureAwait(false);
        IList<string> packageFiles = [.. files.Where(file => global::NuGet.Packaging.PackageHelper.IsPackageFile(file, global::NuGet.Packaging.PackageSaveMode.Defaultv3))];
        var packageFileExtractor = new global::NuGet.Packaging.PackageFileExtractor(packageFiles, global::NuGet.Packaging.XmlDocFileSaveMode.None);

        _ = await reader.CopyFilesAsync(sourceFolder.FullName, packageFiles, packageFileExtractor.ExtractPackageFile, log, cancellationToken).ConfigureAwait(false);

        return (identity, sourceFolder);
    }

    /// <summary>
    /// Checks if a package exists.
    /// </summary>
    /// <param name="name">The package ID.</param>
    /// <param name="version">The package version.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="log">The log.</param>
    /// <param name="root">The configuration root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the package exists in the sources; otherwise <see langword="false"/>.</returns>
    public static ValueTask<bool> PackageExistsAsync(
        string name,
        global::NuGet.Versioning.NuGetVersion version,
        IEnumerable<string>? sources = default,
        ILogger? log = default,
        string? root = default,
        CancellationToken cancellationToken = default)
    {
        var packageIdentity = new global::NuGet.Packaging.Core.PackageIdentity(name, version);

        return packageIdentity.HasVersion
            ? GetPackages(name, sources ?? [], log ?? NullLogger.Instance, root, cancellationToken)
                .AnyAsync(info => info.Listed && global::NuGet.Versioning.VersionComparer.Default.Equals(info.Version, packageIdentity.Version), cancellationToken)
            : new(result: false);
    }

    /// <summary>
    /// Gets the package versions.
    /// </summary>
    /// <param name="name">The package ID.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="log">The log.</param>
    /// <param name="root">The configuration root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The listed versions of the package.</returns>
    public static IAsyncEnumerable<global::NuGet.Versioning.NuGetVersion> GetPackageVersions(
        string name,
        IEnumerable<string>? sources = default,
        ILogger? log = default,
        string? root = default,
        CancellationToken cancellationToken = default) =>
        GetPackages(name, sources ?? [], log ?? NullLogger.Instance, root, cancellationToken)
            .Where(package => package is { Listed: true, HasVersion: true })
            .Select(package => package.Version);

    /// <summary>
    /// Installs the package with the specified name and version.
    /// </summary>
    /// <param name="name">The package ID.</param>
    /// <param name="version">The package version.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="noCache">Set to true to ignore the cache.</param>
    /// <param name="directDownload">Set to true to directly download.</param>
    /// <param name="log">The log.</param>
    /// <param name="root">The root of the settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The nuget package path, and whether it can be removed.</returns>
    public static async Task<(FileInfo? NuPkg, bool CanRemove)> InstallAsync(
        string name,
        global::NuGet.Versioning.NuGetVersion? version,
        IEnumerable<string>? sources = default,
        bool noCache = default,
        bool directDownload = default,
        ILogger? log = default,
        string? root = default,
        CancellationToken cancellationToken = default)
    {
        // get the package identity
        log ??= NullLogger.Instance;
        var enumerableSources = sources ?? [];
        var settings = global::NuGet.Configuration.Settings.LoadDefaultSettings(root);
        var packageIdentity = version is null
            ? await GetLatestPackage(enumerableSources, name, includePrerelease: true, log, settings, cancellationToken).ConfigureAwait(false)
            : await GetPackage(enumerableSources, name, version, log, settings, cancellationToken).ConfigureAwait(false);

        return packageIdentity is null
            ? throw new InvalidOperationException($"Failed to find any valid version of {name}")
            : await InstallAsync(packageIdentity, sources, noCache, directDownload, log, root, cancellationToken).ConfigureAwait(false);

        static async Task<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo?> GetLatestPackage(
            IEnumerable<string> sources,
            string packageId,
            bool includePrerelease,
            ILogger log,
            global::NuGet.Configuration.ISettings settings,
            CancellationToken cancellationToken)
        {
            global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo? latest = default;
            foreach (var repository in GetRepositories(settings, sources))
            {
                var package = await GetLatestPackage(repository, packageId, includePrerelease, log, cancellationToken).ConfigureAwait(false);
                if (package is null)
                {
                    continue;
                }

                latest ??= package;
                if (package.Version > latest.Version)
                {
                    latest = package;
                }
            }

            return latest;

            static async Task<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo?> GetLatestPackage(
                global::NuGet.Protocol.Core.Types.SourceRepository source,
                string packageId,
                bool includePrerelease,
                ILogger log,
                CancellationToken cancellationToken)
            {
                return await GetPackages(source, packageId, log, cancellationToken)
                    .Where(package => package.Listed && (!package.HasVersion || !package.Version.IsPrerelease || includePrerelease))
                    .OrderByDescending(package => package.Version, global::NuGet.Versioning.VersionComparer.Default)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        static async Task<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo?> GetPackage(
            IEnumerable<string> sources,
            string packageId,
            global::NuGet.Versioning.SemanticVersion version,
            ILogger log,
            global::NuGet.Configuration.ISettings settings,
            CancellationToken cancellationToken)
        {
            foreach (var repository in GetRepositories(settings, sources))
            {
                if (await GetPackage(repository, packageId, version, log, cancellationToken).ConfigureAwait(false) is { } package)
                {
                    return package;
                }
            }

            return default;

            static async Task<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo?> GetPackage(
                global::NuGet.Protocol.Core.Types.SourceRepository source,
                string packageId,
                global::NuGet.Versioning.SemanticVersion version,
                ILogger log,
                CancellationToken cancellationToken)
            {
                return await GetPackages(source, packageId, log, cancellationToken)
                    .FirstOrDefaultAsync(package => package is { Listed: true, HasVersion: true } && global::NuGet.Versioning.VersionComparer.Default.Compare(package.Version, version) == 0, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static Task<(FileInfo? NuPkg, bool CanRemove)> InstallAsync(
        global::NuGet.Packaging.Core.PackageIdentity package,
        IEnumerable<string>? sources = default,
        bool noCache = default,
        bool directDownload = default,
        ILogger? log = default,
        string? root = default,
        CancellationToken cancellationToken = default)
    {
        return InstallPackageAsync(
            package ?? throw new ArgumentNullException(nameof(package)),
            sources ?? [],
            global::NuGet.Configuration.Settings.LoadDefaultSettings(root),
            !noCache,
            !directDownload,
            log ?? NullLogger.Instance,
            cancellationToken);

        static async Task<(FileInfo? NuPkg, bool CanRemove)> InstallPackageAsync(
            global::NuGet.Packaging.Core.PackageIdentity package,
            IEnumerable<string> sources,
            global::NuGet.Configuration.ISettings settings,
            bool useCache,
            bool addToCache,
            ILogger log,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read package file from remote or cache
            log.LogInformation($"Downloading package {package}");
            return package is global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo info
                ? await DownloadPackage(info, info.Source, settings, useCache, addToCache, log, cancellationToken).ConfigureAwait(false)
                : await DownloadPackage(package, sources, settings, useCache, addToCache, log, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(FileInfo NuPkg, bool CanRemove)> DownloadPackage(
        global::NuGet.Packaging.Core.PackageIdentity package,
        global::NuGet.Protocol.Core.Types.SourceRepository repository,
        global::NuGet.Configuration.ISettings settings,
        bool useCache,
        bool addToCache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (useCache
            && GlobalPackagesFolderUtility.GetPackage(
                package,
                global::NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings)) is { PackageStream: FileStream fileStream })
        {
            logger.LogInformation($"Found {package} in global package folder");
            return (new(fileStream.Name), false);
        }

        using var sourceCacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext();

        return await DownloadPackage(
            package,
            repository,
            sourceCacheContext,
            settings,
            addToCache,
            logger,
            cancellationToken).ConfigureAwait(false) is { NuPkg: { } nuPkg, CanRemove: var canRemove }
            ? (nuPkg, canRemove)
            : throw new PackageNotFoundProtocolException(package);
    }

    private static async Task<(FileInfo? NuPkg, bool CanRemove)> DownloadPackage(
        global::NuGet.Packaging.Core.PackageIdentity package,
        IEnumerable<string> sources,
        global::NuGet.Configuration.ISettings settings,
        bool useCache,
        bool addToCache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (useCache
            && GlobalPackagesFolderUtility.GetPackage(
                package,
                global::NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings)) is { PackageStream: FileStream fileStream })
        {
            logger.LogInformation($"Found {package} in global package folder");
            return (new(fileStream.Name), false);
        }

        using var sourceCacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext();
        foreach (var repository in GetRepositories(settings, sources))
        {
            if (await DownloadPackage(
                    package,
                    repository,
                    sourceCacheContext,
                    settings,
                    addToCache,
                    logger,
                    cancellationToken).ConfigureAwait(false) is { NuPkg: not null } result)
            {
                return result;
            }
        }

        throw new PackageNotFoundProtocolException(package);
    }

    private static async Task<(FileInfo? NuPkg, bool CanRemove)> DownloadPackage(
        global::NuGet.Packaging.Core.PackageIdentity package,
        global::NuGet.Protocol.Core.Types.SourceRepository repository,
        global::NuGet.Protocol.Core.Types.SourceCacheContext cacheContext,
        global::NuGet.Configuration.ISettings settings,
        bool addToCache,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var findPackagesByIdResource = await repository
            .GetResourceAsync<global::NuGet.Protocol.Core.Types.FindPackageByIdResource>(cancellationToken)
            .ConfigureAwait(false);
        if (await findPackagesByIdResource
                .GetPackageDownloaderAsync(package, cacheContext, logger, cancellationToken)
                .ConfigureAwait(false) is not { } packageDownloader)
        {
            logger.LogWarning($"Package {package} not found in repository {repository}");
            return default;
        }

        logger.LogInformation($"Getting {package} from {repository}");

        var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        if (!await packageDownloader.CopyNupkgFileToAsync(tempFile, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to fetch package {package} from source {repository}");
        }

        if (!addToCache)
        {
            return (new(tempFile), true);
        }

        var stream = File.OpenRead(tempFile);
        await using (stream.ConfigureAwait(false))
        {
            var downloadCacheContext = new global::NuGet.Protocol.Core.Types.PackageDownloadContext(cacheContext);
            using var downloadResourceResult = await GlobalPackagesFolderUtility
                .AddPackageAsync(
                    repository.PackageSource.Source,
                    package,
                    stream,
                    global::NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings),
                    downloadCacheContext.ParentId,
                    global::NuGet.Packaging.Signing.ClientPolicyContext.GetClientPolicy(settings, logger),
                    logger,
                    cancellationToken).ConfigureAwait(false);
        }

        return (new(tempFile), true);
    }

    private static global::NuGet.Protocol.Core.Types.SourceRepository[] GetRepositories(global::NuGet.Configuration.ISettings settings, IEnumerable<string>? sources)
    {
        var enabledSources = global::NuGet.Configuration.SettingsUtility.GetEnabledSources(settings);
        var repositories = sources?
            .Where(source => !string.IsNullOrEmpty(source))
            .Select(source => GetFromMachineSources(source, enabledSources) ?? global::NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(source)).ToArray();
        return repositories?.Length > 0
            ? repositories
            : enabledSources.Select(packageSource => global::NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(packageSource)).ToArray();

        static global::NuGet.Configuration.PackageSource ResolveSource(IEnumerable<global::NuGet.Configuration.PackageSource> availableSources, string source)
        {
            var resolvedSource = availableSources.FirstOrDefault(f => f.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                                                                      || f.Name.Equals(source, StringComparison.OrdinalIgnoreCase));

            return resolvedSource ?? new global::NuGet.Configuration.PackageSource(source);
        }

        static global::NuGet.Protocol.Core.Types.SourceRepository? GetFromMachineSources(string source, IEnumerable<global::NuGet.Configuration.PackageSource> enabledSources)
        {
            var resolvedSource = ResolveSource(enabledSources, source);
            return resolvedSource.ProtocolVersion == 2
                ? global::NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV2(resolvedSource)
                : global::NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(resolvedSource.Source);
        }
    }

    private static IAsyncEnumerable<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo> GetPackages(
        string name,
        IEnumerable<string> sources,
        ILogger log,
        string? root = default,
        CancellationToken cancellationToken = default)
    {
        var settings = global::NuGet.Configuration.Settings.LoadDefaultSettings(root);

        return GetRepositories(settings, sources)
            .ToAsyncEnumerable()
            .SelectMany(repository => GetPackages(repository, name, log, cancellationToken));
    }

    private static async IAsyncEnumerable<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo> GetPackages(
        global::NuGet.Protocol.Core.Types.SourceRepository source,
        string packageId,
        ILogger log,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<global::NuGet.Protocol.Core.Types.SourcePackageDependencyInfo>? returnValues = default;

        try
        {
            var dependencyInfoResource = await source.GetResourceAsync<global::NuGet.Protocol.Core.Types.DependencyInfoResource>(cancellationToken).ConfigureAwait(false);
            using var sourceCacheContext = new global::NuGet.Protocol.Core.Types.SourceCacheContext { IgnoreFailedSources = true };
            returnValues = await dependencyInfoResource.ResolvePackages(packageId, global::NuGet.Frameworks.NuGetFramework.AgnosticFramework, sourceCacheContext, log, cancellationToken).ConfigureAwait(false);
        }
        catch (global::NuGet.Protocol.Core.Types.FatalProtocolException e)
        {
            log.LogError(e.Message);
        }

        if (returnValues is null)
        {
            yield break;
        }

        foreach (var returnValue in returnValues)
        {
            yield return returnValue;
        }
    }
}