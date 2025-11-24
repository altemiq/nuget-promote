// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Altemiq">
// Copyright (c) Altemiq. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.CommandLine;
using Altemiq.NuGet.Promote;

await CreateRootCommand().Parse(args).InvokeAsync().ConfigureAwait(false);

static RootCommand CreateRootCommand()
{
    var labelOption = new Option<string>("-l", "--label") { Description = "The new label, or leave blank to promote to a full release.", Recursive = true };
    var sourceOption = new Option<string>("-s", "--source") { Description = "Package source (URL, UNC/folder path or package source name) to use. Defaults to DefaultPushSource if specified in NuGet.Config.", Recursive = true };
    var destinationOption = new Option<string>("-d", "--destination") { Description = "Package destination (URL, UNC/folder path or package source name) to use. Defaults to source.", Recursive = true };
    var apiKeyOption = new Option<string>("-k", "--api-key") { Description = "The API key for the server.", Recursive = true };
    var recurseOption = new Option<bool>("-r", "--recurse") { Description = "Whether to recursively promote dependencies", Recursive = true };
    var configFileOption = new Option<FileInfo>("--config-file") { Description = "The nuget config file to use" };
    var command = new RootCommand
    {
        CreateSourceCommand(labelOption, sourceOption, destinationOption, apiKeyOption, recurseOption, configFileOption),
        CreateNupkgCommand(labelOption, sourceOption, apiKeyOption, recurseOption, configFileOption),
        CreateZipCommand(labelOption, sourceOption, apiKeyOption, recurseOption, configFileOption),
        labelOption,
        sourceOption,
        destinationOption,
        apiKeyOption,
        recurseOption,
    };

    return command;

    static Command CreateSourceCommand(
        Option<string> labelOption,
        Option<string> sourceOption,
        Option<string> destinationOption,
        Option<string> apiKeyOption,
        Option<bool> recurseOption,
        Option<FileInfo> configFileOption)
    {
        var nameArgument = new Argument<string[]>("NAME") { Description = "The package to promote" };
        var versionOption = new Option<NuGet.Versioning.NuGetVersion?>("-v", "--version")
        {
            CustomParser = result => result.Tokens is [var token] && NuGet.Versioning.NuGetVersion.TryParse(token.Value, out var version) ? version : default,
        };

        var command = new Command("source", "Releases the specified name and version")
        {
            nameArgument,
            versionOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var version = parseResult.GetValue(versionOption);
            if (version is { IsPrerelease: false })
            {
                return;
            }

            var names = parseResult.GetValue(nameArgument)!;
            var label = parseResult.GetValue(labelOption);
            var source = parseResult.GetValue(sourceOption);
            var destination = parseResult.GetValue(destinationOption) ?? source;
            var apiKey = parseResult.GetValue(apiKeyOption);
            var recurse = parseResult.GetValue(recurseOption);
            var configFile = parseResult.GetValue(configFileOption);

            var packageCache = new Dictionary<string, NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                if (await NuGetInstaller.InstallAsync(name, version, source is null ? default : new[] { source }, log: ConsoleLogger.Instance, root: Environment.CurrentDirectory, cancellationToken: cancellationToken)
                        .ConfigureAwait(true)
                    is { NuPkg: { Exists: true } nupkg, CanRemove: var canRemove })
                {
                    await PromoteNuPkg(
                        nupkg,
                        label,
                        destination,
                        apiKey,
                        recurse,
                        packageCache,
                        canRemove,
                        configFile,
                        Environment.CurrentDirectory,
                        ConsoleLogger.Instance,
                        cancellationToken).ConfigureAwait(true);
                }
            }
        });

        return command;
    }

    static Command CreateNupkgCommand(
        Option<string> labelOption,
        Option<string> sourceOption,
        Option<string> apiKeyOption,
        Option<bool> recurseOption,
        Option<FileInfo> configFileOption)
    {
        var nupkgArgument = new Argument<FileInfo>("NUPKG") { Description = "The package to promote" }.AcceptExistingOnly();

        var command = new Command("nupkg", "Releases the specified nupkg file")
        {
            nupkgArgument,
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            var nupkg = parseResult.GetValue(nupkgArgument)!;
            var label = parseResult.GetValue(labelOption);
            var source = parseResult.GetValue(sourceOption);
            var apiKey = parseResult.GetValue(apiKeyOption);
            var recurse = parseResult.GetValue(recurseOption);
            var configFile = parseResult.GetValue(configFileOption);

            return PromoteNuPkg(nupkg, label, source, apiKey, recurse, new Dictionary<string, NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase), removeSource: false, configFile, Environment.CurrentDirectory, ConsoleLogger.Instance, cancellationToken);
        });

        return command;
    }

    static Command CreateZipCommand(
        Option<string> labelOption,
        Option<string> sourceOption,
        Option<string> apiKeyOption,
        Option<bool> recurseOption,
        Option<FileInfo> configFileOption)
    {
        var zipArgument = new Argument<FileInfo>("ZIP") { Description = "The zip package containing the packages to promote" }.AcceptExistingOnly();

        var command = new Command("zip", "Releases the specified nupkg files contained in the zip file")
        {
            zipArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var zip = parseResult.GetValue(zipArgument)!;
            var label = parseResult.GetValue(labelOption);
            var source = parseResult.GetValue(sourceOption);
            var apiKey = parseResult.GetValue(apiKeyOption);
            var recurse = parseResult.GetValue(recurseOption);
            var configFile = parseResult.GetValue(configFileOption);

            // unzip the file
            var packageVersionCache = new Dictionary<string, NuGet.Versioning.NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            var archive =
#if NET10_0_OR_GREATER
                await System.IO.Compression.ZipFile.OpenReadAsync(zip.FullName, cancellationToken).ConfigureAwait(false);
#else
                System.IO.Compression.ZipFile.OpenRead(zip.FullName);
#endif
#if NET10_0_OR_GREATER
            await using (archive.ConfigureAwait(false))
#else
            using (archive)
#endif
            {
                foreach (var nupkg in archive.Entries
                             .Where(x => x.Name.EndsWith(
                                 NuGet.Configuration.NuGetConstants.PackageExtension,
                                 StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => Path.GetFileNameWithoutExtension(x.Name), NuPkgComparer.Instance))
                {
                    // extract the nupkg
                    NuGet.Packaging.Core.PackageIdentity? identity;
                    DirectoryInfo? sourceFolder;
                    var stream =
#if NET10_0_OR_GREATER
                        await nupkg.OpenAsync(cancellationToken).ConfigureAwait(false);
#else
                        nupkg.Open();
#endif
                    await using (stream.ConfigureAwait(false))
                    {
                        (identity, sourceFolder) = await NuGetInstaller
                            .ExtractAsync(stream, ConsoleLogger.Instance, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await PromoteFolder(
                        identity,
                        sourceFolder,
                        label,
                        source,
                        apiKey,
                        recurse,
                        packageVersionCache,
                        configFile,
                        Environment.CurrentDirectory,
                        ConsoleLogger.Instance,
                        GetStream,
                        GetVersion,
                        cancellationToken).ConfigureAwait(false);

                    if (sourceFolder?.Exists is true)
                    {
                        sourceFolder.Delete(recursive: true);
                    }

                    Stream? GetStream(string name)
                    {
                        return archive.GetEntry(name)?.Open();
                    }

                    IAsyncEnumerable<NuGet.Versioning.NuGetVersion> GetVersion(string name)
                    {
                        return archive.Entries
                            .Select(x => NuGet.Protocol.LocalFolderUtility.GetVersionFromFileName(
                                x.Name,
                                name,
                                NuGet.Packaging.Core.PackagingCoreConstants.NupkgExtension))
                            .Where(v => v is not null)
                            .ToAsyncEnumerable();
                    }
                }
            }
        });

        return command;
    }

    static async Task PromoteNuPkg(FileInfo nupkg, string? label, string? source, string? apiKey, bool recurse, IDictionary<string, NuGet.Versioning.NuGetVersion> packageVersionCache, bool removeSource, FileInfo? configFile, string? root, NuGet.Common.ILogger log, CancellationToken cancellationToken = default)
    {
        var (identity, sourceFolder) = await NuGetInstaller.ExtractAsync(nupkg, log, cancellationToken).ConfigureAwait(false);
        if (removeSource)
        {
            nupkg.Delete();
        }

        await PromoteFolder(identity, sourceFolder, label, source, apiKey, recurse, packageVersionCache, configFile, root, log, GetStream, _ => AsyncEnumerable.Empty<NuGet.Versioning.NuGetVersion>(), cancellationToken).ConfigureAwait(false);

        Stream? GetStream(string fileName)
        {
            var localName = new FileInfo(Path.Join(Path.GetTempPath(), fileName));
            if (!removeSource)
            {
                var folderName = nupkg.Directory?.FullName ?? string.Empty;
                localName = new(Path.Join(folderName, fileName));
            }

            if (localName.Exists)
            {
                return localName.OpenRead();
            }

            return default;
        }
    }

    static async Task PromoteFolder(
        NuGet.Packaging.Core.PackageIdentity? identity,
        DirectoryInfo? sourceFolder,
        string? label,
        string? source,
        string? apiKey,
        bool recurse,
        IDictionary<string, NuGet.Versioning.NuGetVersion> packageVersionCache,
        FileInfo? configFile,
        string? root,
        NuGet.Common.ILogger log,
        Func<string, Stream?> getDependentStream,
        Func<string, IAsyncEnumerable<NuGet.Versioning.NuGetVersion>> getPackageVersions,
        CancellationToken cancellationToken = default)
    {
        if (identity is null || sourceFolder is null || !identity.HasVersion)
        {
            return;
        }

        IList<string>? sources = source is null
            ? default
            : [source];

        var destinationVersion = new NuGet.Versioning.NuGetVersion(
            identity.Version.Major,
            identity.Version.Minor,
            identity.Version.Patch,
            identity.Version.Revision,
            label ?? string.Empty,
            identity.Version.Metadata ?? string.Empty);

        var destinationFolder = new DirectoryInfo(Path.Join(Path.GetTempPath(), string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{identity.Id}/{destinationVersion}")));

        // rename the source to destination
        if (destinationFolder.Exists)
        {
            destinationFolder.Delete(recursive: true);
        }

        sourceFolder.MoveTo(destinationFolder.FullName);

        var destinationNuSpec = Path.Join(destinationFolder.FullName, $"{identity.Id}{NuGet.Configuration.NuGetConstants.ManifestExtension}");
        var document = new System.Xml.XmlDocument();
        using (var reader = System.Xml.XmlReader.Create(destinationNuSpec))
        {
            document.Load(reader);
        }

        var namespaceManager = new System.Xml.XmlNamespaceManager(document.NameTable);
        namespaceManager.AddNamespace("n", document.DocumentElement!.GetAttribute("xmlns"));
        if (document.SelectSingleNode("/n:package/n:metadata/n:version", namespaceManager) is not { } node)
        {
            return;
        }

        node.InnerText = destinationVersion.ToNormalizedString();

        var dependencies = document.SelectNodes("/n:package/n:metadata/n:dependencies/n:dependency", namespaceManager);
        var groupDependencies = document.SelectNodes("/n:package/n:metadata/n:dependencies/n:group/n:dependency", namespaceManager);

        foreach (var attributes in GetNodes(dependencies, groupDependencies).Select(dependency => dependency.Attributes))
        {
            if (attributes is null)
            {
                continue;
            }

            var dependencyName = attributes["id"]!.Value;

            var versionAttribute = attributes["version"]!;
            var dependencyVersion = new NuGet.Versioning.NuGetVersion(versionAttribute.Value);

            if (!dependencyVersion.IsPrerelease)
            {
                continue;
            }

            if (!packageVersionCache.TryGetValue(dependencyName, out var newDependencyVersion))
            {
                var versions = await NuGetInstaller.GetPackageVersions(dependencyName, sources, log, root, cancellationToken)
                    .Union(getPackageVersions(dependencyName))
                    .OrderBy(p => p, NuGet.Versioning.VersionComparer.Default)
                    .ToListAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (!PackageExists(versions, dependencyVersion))
                {
                    continue;
                }

                newDependencyVersion = new(
                    dependencyVersion.Major,
                    dependencyVersion.Minor,
                    dependencyVersion.Patch,
                    dependencyVersion.Revision,
                    label ?? string.Empty,
                    dependencyVersion.Metadata ?? string.Empty);

                if (!PackageExists(versions, newDependencyVersion))
                {
                    if (GetNextRelease(versions, newDependencyVersion) is { } nextVersion)
                    {
                        newDependencyVersion = nextVersion;
                    }
                    else if (recurse)
                    {
                        // see if this package exists locally
                        var localFileName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{dependencyName}.{dependencyVersion}{NuGet.Configuration.NuGetConstants.PackageExtension}");
                        var stream = getDependentStream(localFileName);

                        if (stream is not null)
                        {
                            NuGet.Packaging.Core.PackageIdentity? dependencyIdentity;
                            DirectoryInfo? dependencySourceDirectory;
                            await using (stream.ConfigureAwait(false))
                            {
                                (dependencyIdentity, dependencySourceDirectory) = await NuGetInstaller.ExtractAsync(stream, log, cancellationToken).ConfigureAwait(false);
                            }

                            await PromoteFolder(dependencyIdentity, dependencySourceDirectory, label, source, apiKey, recurse, packageVersionCache, configFile, root, log, getDependentStream, getPackageVersions, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var (dependencyNupkg, canRemoveDependency) = await NuGetInstaller.InstallAsync(dependencyName, dependencyVersion, sources, log: log, cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (dependencyNupkg?.Exists != true)
                            {
                                throw new InvalidOperationException();
                            }

                            await PromoteNuPkg(dependencyNupkg, label, source, apiKey, recurse, packageVersionCache, canRemoveDependency, configFile, root, log, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                packageVersionCache.Add(dependencyName, newDependencyVersion);

                static bool PackageExists(IEnumerable<NuGet.Versioning.NuGetVersion> versions, NuGet.Versioning.NuGetVersion desired)
                {
                    return versions.Any(version => NuGet.Versioning.VersionComparer.Default.Equals(version, desired));
                }

                static NuGet.Versioning.NuGetVersion? GetNextRelease(IEnumerable<NuGet.Versioning.NuGetVersion> versions, NuGet.Versioning.NuGetVersion desired)
                {
                    var min = new NuGet.Versioning.NuGetVersion(desired.Major, desired.Minor, desired.Patch);
                    var max = new NuGet.Versioning.NuGetVersion(min.Major, min.Minor + 1, 0);

                    var range = new NuGet.Versioning.VersionRange(min, includeMinVersion: true, max, includeMaxVersion: false);

                    return versions.FirstOrDefault(version => !version.IsPrerelease && range.Satisfies(version));
                }
            }

            versionAttribute.Value = newDependencyVersion.ToNormalizedString();
        }

        document.Save(destinationNuSpec);

        // pack this up
        var manifestDirectory = Path.GetDirectoryName(destinationNuSpec);
        var outputDirectory = Path.GetDirectoryName(manifestDirectory);
        var packArgs = new NuGet.Commands.PackArgs
        {
            Path = Path.GetFileName(destinationNuSpec),
            CurrentDirectory = manifestDirectory,
            OutputDirectory = outputDirectory,
            Logger = log,
            Exclude = [],
            Symbols = true,
            Deterministic = true,
        };

        var packageCreated = false;
        var packRunner = new NuGet.Commands.PackCommandRunner(packArgs, createProjectFactory: null);
        try
        {
            packageCreated = packRunner.RunPackageBuild();
        }
        catch (NuGet.Packaging.Core.PackagingException ex)
        {
            log.LogError(ex.Message);
        }

        if (packageCreated)
        {
            var packageName = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{identity.Id}.{destinationVersion}");
            var nupkgPath = Path.Combine(outputDirectory ?? string.Empty, packageName + NuGet.Configuration.NuGetConstants.PackageExtension);
            var symbolsNupkgPath = Path.Combine(outputDirectory ?? string.Empty, packageName + NuGet.Configuration.NuGetConstants.SymbolsExtension);
            if (GetNupkgPath(nupkgPath, symbolsNupkgPath) is not { } upload)
            {
                return;
            }

            apiKey ??= NuGetInstaller.GetSourceKey(source!, root);
            var settings = NuGet.Configuration.Settings.LoadDefaultSettings(root);
            await NuGet.Commands.PushRunner
                .Run(
                    settings,
                    new NuGet.Configuration.PackageSourceProvider(settings),
                    [upload],
                    source,
                    apiKey,
                    symbolSource: null,
                    symbolApiKey: apiKey,
                    10,
                    disableBuffering: false,
                    noSymbols: !upload.Contains(NuGet.Configuration.NuGetConstants.SymbolsExtension, StringComparison.Ordinal),
                    noServiceEndpoint: false,
                    skipDuplicate: true,
                    allowInsecureConnections: true,
                    log)
                .ConfigureAwait(false);

            DeleteIfExists(nupkgPath);
            DeleteIfExists(symbolsNupkgPath);

            packageVersionCache.TryAdd(identity.Id, destinationVersion);

            static void DeleteIfExists(string file)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            static string? GetNupkgPath(string nupkgPath, string symbolsNupkgPath)
            {
                var nupkgFileInfo = new FileInfo(nupkgPath);
                var symbolsNupkgFileInfo = new FileInfo(symbolsNupkgPath);
                return (NupkgFileInfo: nupkgFileInfo, SymbolsNupkgFileInfo: symbolsNupkgFileInfo) switch
                {
                    ({ Exists: true }, { Exists: true }) files when files.NupkgFileInfo.Length >= files.SymbolsNupkgFileInfo.Length => files.NupkgFileInfo.FullName,
                    (_, { Exists: true }) files => files.SymbolsNupkgFileInfo.FullName,
                    _ => default,
                };
            }
        }

        destinationFolder.Delete(recursive: true);

        static IEnumerable<System.Xml.XmlNode> GetNodes(params System.Xml.XmlNodeList?[]? lists)
        {
            if (lists is null)
            {
                yield break;
            }

            foreach (var list in lists)
            {
                if (list is null)
                {
                    continue;
                }

                for (var i = 0; i < list.Count; i++)
                {
                    var node = list[i];
                    if (node is not null)
                    {
                        yield return node;
                    }
                }
            }
        }
    }
}

/// <content>
/// The program class.
/// </content>
internal static partial class Program
{
    private sealed class NuPkgComparer : IComparer<string>
    {
        public static readonly IComparer<string> Instance = new NuPkgComparer();

        int IComparer<string>.Compare(string? x, string? y) => (x, y) switch
        {
            (null, null) => 0,
            (null, not null) => -1,
            (not null, null) => 1,
            var (first, second) when first.Length == second.Length => string.CompareOrdinal(first, second),
            var (first, second) => first.Length.CompareTo(second.Length),
        };
    }
}