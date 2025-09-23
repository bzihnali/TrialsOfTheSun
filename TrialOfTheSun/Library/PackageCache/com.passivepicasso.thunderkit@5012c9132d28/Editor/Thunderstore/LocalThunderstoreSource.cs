﻿using SharpCompress.Archives;
using SharpCompress.Readers;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ThunderKit.Core.Data;
using UnityEngine;

namespace ThunderKit.Integrations.Thunderstore
{
    using PV = Core.Data.PackageVersion;
    public class LocalThunderstoreSource : PackageSource
    {
        private static readonly string[] EmptyStringArray = new string[0];

        private void InitializeSource(object sender, System.EventArgs e) => LoadPackages();

        private void OnEnable()
        {
            InitializeSources -= InitializeSource;
            InitializeSources += InitializeSource;
        }
        private void OnDisable()
        {
            InitializeSources -= InitializeSource;
        }
        private void OnDestroy()
        {
            InitializeSources -= InitializeSource;
        }

        public string LocalRepositoryPath;

        public override string Name => "Local Thunderstore";
        public override string SourceGroup => "Thunderstore";

        protected override string VersionIdToGroupId(string dependencyId) => dependencyId.Substring(0, dependencyId.LastIndexOf("-"));
        protected override void OnLoadPackages()
        {
            if (string.IsNullOrEmpty(LocalRepositoryPath)) return;
            var potentialPackages = Directory.GetFiles(LocalRepositoryPath, "*.zip", SearchOption.TopDirectoryOnly);
            foreach (var filePath in potentialPackages)
            {
                using (var archive = ArchiveFactory.Open(filePath))
                    foreach (var entry in archive.Entries)
                    {
                        if (!"manifest.json".Equals(Path.GetFileName(entry.Key))) continue;

                        var manifestJson = string.Empty;
                        using (var reader = new StreamReader(entry.OpenEntryStream()))
                            manifestJson = reader.ReadToEnd();

                        var tsp = JsonUtility.FromJson<PackageVersion>(manifestJson);

                        var versionId = Path.GetFileNameWithoutExtension(filePath);
                        var author = versionId.Split('-')[0];

                        AddPackageGroup(new PackageGroupInfo
                        {
                            Author = author,
                            Name = tsp.name,
                            Description = tsp.description,
                            DependencyId = $"{author}-{tsp.name}",
                            Versions = new PackageVersionInfo[] {
                                new PackageVersionInfo(tsp.version_number, versionId, tsp.dependencies, string.Empty)
                            }
                        });
                        break;
                    }
            }
        }

        protected override void OnInstallPackageFiles(PV version, string packageDirectory)
        {
            var potentialPackages = Directory.GetFiles(LocalRepositoryPath, "*.zip", SearchOption.TopDirectoryOnly);
            foreach (var filePath in potentialPackages)
            {
                using (var archive = ArchiveFactory.Open(filePath))
                {
                    var manifestJsonEntry = archive.Entries.First(entry => entry.Key.Contains("manifest.json"));
                    var manifestJson = string.Empty;
                    using (var reader = new StreamReader(manifestJsonEntry.OpenEntryStream()))
                        manifestJson = reader.ReadToEnd();

                    var version_full_name = Path.GetFileNameWithoutExtension(filePath);
                    var author = version_full_name.Split('-')[0];
                    var manifest = JsonUtility.FromJson<PackageVersion>(manifestJson);
                    var full_name = $"{author}-{manifest.name}";
                    if (full_name != version.groupDependencyId) continue;

                    foreach (var entry in archive.Entries.Where(entry => entry.IsDirectory))
                        Directory.CreateDirectory(Path.Combine(packageDirectory, entry.Key));

                    var extractOptions = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };
                    foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                        entry.WriteToDirectory(packageDirectory, extractOptions);
                }
            }
        }

        protected override Task ReloadPagesAsyncInternal()
        {
            return Task.CompletedTask;
        }
    }
}