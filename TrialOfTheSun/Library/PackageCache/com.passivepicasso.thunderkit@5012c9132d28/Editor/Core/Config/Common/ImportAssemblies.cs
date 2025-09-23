﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ThunderKit.Common;
using ThunderKit.Core.Data;
using ThunderKit.Core.UIElements;
using ThunderKit.Core.Utilities;
using UnityEditor;
using UnityEngine;
#if UNITY_2019_1_OR_NEWER
using UnityEngine.UIElements;
using UnityEditor.UIElements;
#elif UNITY_2018
using UnityEditor.Experimental.UIElements;
using UnityEngine.Experimental.UIElements;
#endif

namespace ThunderKit.Core.Config
{
    [Serializable]
    public class ImportAssemblies : OptionalExecutor
    {
        public static IReadOnlyList<BlacklistProcessor> BlacklistProcessors { get; private set; }
        public static IReadOnlyList<WhitelistProcessor> WhitelistProcessors { get; private set; }
        public static IReadOnlyList<AssemblyProcessor> AssemblyProcessors { get; private set; }

        [InitializeOnLoadMethod]
        static void InitializeConfigurators()
        {
            var configurationAssemblies = AppDomain.CurrentDomain
                            .GetAssemblies()
                            .Where(asm => asm != null)
                            .Where(asm => asm.GetCustomAttribute<ImportExtensionsAttribute>() != null);
            var loadedTypes = configurationAssemblies
#if NET_4_6
                .Where(asm => !asm.IsDynamic)
#else
                .Where(asm =>
                {
                    if (asm.ManifestModule is System.Reflection.Emit.ModuleBuilder mb)
                        return !mb.IsTransient();
                    return true;
                })
#endif
               .SelectMany(asm =>
               {
                   try
                   {
                       return asm.GetTypes();
                   }
                   catch (ReflectionTypeLoadException e)
                   {
                       return e.Types.Where(t => t != null);
                   }
               }).ToArray();

            BlacklistProcessors = CreateImporters<BlacklistProcessor>(loadedTypes);
            WhitelistProcessors = CreateImporters<WhitelistProcessor>(loadedTypes);
            AssemblyProcessors = CreateImporters<AssemblyProcessor>(loadedTypes);
        }

        private static List<T> CreateImporters<T>(Type[] loadedTypes) where T : ImportExtension => loadedTypes.Where(t => typeof(T).IsAssignableFrom(t))
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Select(t => CreateInstance(t) as T)
                .Where(t => t != null)
                .OrderByDescending(t => t.Priority)
                .ToList();

        public override int Priority => Constants.Priority.AssemblyImport;
        public override string Description => "Import's Assemblies from Game identified in ThunderKit Settings";

        public override bool Execute()
        {
            var settings = ThunderKitSetting.GetOrCreateSettings<ThunderKitSettings>();
            var packageName = Path.GetFileNameWithoutExtension(settings.GameExecutable);

            AssertDestinations(packageName);

            try
            {
                AssetDatabase.StartAssetEditing();
                EditorApplication.LockReloadAssemblies();

                var blackList = BuildAssemblyBlacklist();
                var whitelist = BuildBinaryWhitelist(settings);

                var packagePath = Path.Combine("Packages", packageName);
                var managedAssemblies = Directory.EnumerateFiles(settings.ManagedAssembliesPath, "*.dll", SearchOption.AllDirectories).Distinct().ToList();

                ImportFilteredAssemblies(packagePath, managedAssemblies, blackList, whitelist);

                var pluginsPath = Path.Combine(settings.GameDataPath, "Plugins");
                if (Directory.Exists(pluginsPath))
                {
                    var packagePluginsPath = Path.Combine(packagePath, "plugins");
                    var plugins = Directory.EnumerateFiles(pluginsPath, $"*", SearchOption.AllDirectories);
                    ImportFilteredAssemblies(packagePluginsPath, plugins, blackList, whitelist);
                }
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
                AssetDatabase.StopAssetEditing();
            }
            return true;
        }

        private static void AssertDestinations(string packageName)
        {
            var destinationFolder = Path.Combine("Packages", packageName);
            if (!Directory.Exists(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            destinationFolder = Path.Combine("Packages", packageName, "plugins");
            if (!Directory.Exists(destinationFolder))
                Directory.CreateDirectory(destinationFolder);
        }

        private static HashSet<string> BuildBinaryWhitelist(ThunderKitSettings settings)
        {
            string[] installedGameAssemblies = Array.Empty<string>();
            if (Directory.Exists(settings.PackagePath))
                installedGameAssemblies = Directory.EnumerateFiles(settings.PackagePath, "*", SearchOption.AllDirectories)
                                       .Select(path => Path.GetFileName(path))
                                       .Distinct()
                                       .ToArray();

            var whitelist = new HashSet<string>(installedGameAssemblies);

            var enumerable = whitelist as IEnumerable<string>;

            foreach (var processor in WhitelistProcessors)
                enumerable = processor.Process(enumerable);
            return whitelist;
        }
        /// <summary>
        /// Collect list of Assemblies that should not be imported from the game.
        /// These are assemblies that would be automatically provided by Unity to the environment
        /// </summary>
        /// <param name="byEditorFiles"></param>
        private static HashSet<string> BuildAssemblyBlacklist(bool byEditorFiles = false)
        {
            var result = new HashSet<string>();
            if (byEditorFiles)
            {
                var editorPath = Path.GetDirectoryName(EditorApplication.applicationPath);
                var extensionsFolder = Path.Combine(editorPath, "Data", "Managed");
                foreach (var asmFile in Directory.GetFiles(extensionsFolder, "*.dll", SearchOption.AllDirectories))
                {
                    result.Add(Path.GetFileName(asmFile));
                }
            }
            else
            {
                var blackList = AppDomain.CurrentDomain.GetAssemblies()
#if NET_4_6
                .Where(asm => !asm.IsDynamic)
#else
                .Where(asm =>
                {
                    if (asm.ManifestModule is System.Reflection.Emit.ModuleBuilder mb)
                        return !mb.IsTransient();

                    return true;
                })
#endif
                .Select(asm => asm.Location)
                    .Select(location =>
                    {
                        try
                        {
                            return Path.GetFileName(location);
                        }
                        catch
                        {
                            return string.Empty;
                        }
                    })
                    .OrderBy(s => s);
                foreach (var asm in blackList)
                    result.Add(asm);
            }

            var enumerable = result as IEnumerable<string>;

            foreach (var processor in BlacklistProcessors)
                enumerable = processor.Process(enumerable);

            return new HashSet<string>(enumerable);
        }

        private static void ImportFilteredAssemblies(string destinationFolder, IEnumerable<string> assemblies, HashSet<string> blackList, HashSet<string> whitelist)
        {
            foreach (var assemblyPath in assemblies)
            {
                var asmPath = assemblyPath.Replace("\\", "/");
                foreach (var processor in AssemblyProcessors)
                    asmPath = processor.Process(asmPath);

                string assemblyFileName = Path.GetFileName(asmPath);
                if (!whitelist.Contains(assemblyFileName)
                  && blackList.Contains(assemblyFileName))
                    continue;

                var destinationFile = Path.Combine(destinationFolder, assemblyFileName);

                var destinationMetaData = Path.Combine(destinationFolder, $"{assemblyFileName}.meta");

                try
                {
                    if (File.Exists(destinationFile)) File.Delete(destinationFile);
                    File.Copy(asmPath, destinationFile);

                    PackageHelper.WriteAssemblyMetaData(asmPath, destinationMetaData);
                }
                catch
                {
                    Debug.LogWarning($"Could not update assembly: {destinationFile}", AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationFile));
                }
            }
        }
    }

}