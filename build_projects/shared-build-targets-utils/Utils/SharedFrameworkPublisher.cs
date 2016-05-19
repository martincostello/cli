﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.DotNet.InternalAbstractions;
using Microsoft.DotNet.Cli.Build.Framework;
using Newtonsoft.Json;

using static Microsoft.DotNet.Cli.Build.Framework.BuildHelpers;
using static Microsoft.DotNet.Cli.Build.FS;

namespace Microsoft.DotNet.Cli.Build
{
    public class SharedFrameworkPublisher
    {
        public static string s_sharedFrameworkName = "Microsoft.NETCore.App";
        
        private string _sharedFrameworkTemplateSourceRoot;
        private string _sharedFrameworkNugetVersion;
        private string _sharedFrameworkRid;
        private string _sharedFrameworkSourceRoot;
        private string _repoRoot;
        private string _corehostLockedDirectory;
        private string _corehostLatestDirectory;

        private Crossgen _crossgenUtil = new Crossgen(BuildPackageVersions.CoreCLRVersion);
        private string _corehostPackageSource;

        public SharedFrameworkPublisher(
            string repoRoot, 
            string corehostLockedDirectory,
            string corehostLatestDirectory,
            string corehostPackageSource,
            string sharedFrameworkNugetVersion)
        {
            _repoRoot = repoRoot;
            _corehostLockedDirectory = corehostLockedDirectory;
            _corehostLatestDirectory = corehostLatestDirectory;
            _corehostPackageSource = corehostPackageSource;

            _sharedFrameworkTemplateSourceRoot = Path.Combine(repoRoot, "src", "sharedframework", "framework");
            _sharedFrameworkNugetVersion = sharedFrameworkNugetVersion;

            _sharedFrameworkRid = ComputeSharedFrameworkRid();

            _sharedFrameworkSourceRoot = GenerateSharedFrameworkProject(
                _sharedFrameworkNugetVersion, 
                _sharedFrameworkTemplateSourceRoot, 
                _sharedFrameworkRid);
        }

        public string GetSharedFrameworkPublishPath(string outputRootDirectory)
        {
            return Path.Combine(
                outputRootDirectory,
                "shared",
                s_sharedFrameworkName,
                _sharedFrameworkNugetVersion);
        }

        private string ComputeSharedFrameworkRid()
        {
            return RuntimeEnvironment.OperatingSystemPlatform == Platform.Windows 
                ? $"win7-{RuntimeEnvironment.RuntimeArchitecture}"
                : RuntimeEnvironment.GetRuntimeIdentifier();
        }

        public void PublishSharedFramework(string outputRootDirectory, string commitHash, DotNetCli dotnetCli)
        {
            dotnetCli.Restore(
                "--verbosity", "verbose",
                "--disable-parallel",
                "--infer-runtimes",
                "--fallbacksource", _corehostPackageSource)
                .WorkingDirectory(_sharedFrameworkSourceRoot)
                .Execute()
                .EnsureSuccessful();

            // We publish to a sub folder of the PublishRoot so tools like heat and zip can generate folder structures easier.
            string sharedFrameworkNameAndVersionRoot = GetSharedFrameworkPublishPath(outputRootDirectory);
            if (Directory.Exists(sharedFrameworkNameAndVersionRoot))
            {
                Utils.DeleteDirectory(sharedFrameworkNameAndVersionRoot);
            }

            dotnetCli.Publish(
                "--output", sharedFrameworkNameAndVersionRoot,
                "-r", _sharedFrameworkRid,
                _sharedFrameworkSourceRoot)
                .Execute()
                .EnsureSuccessful();

            // Clean up artifacts that dotnet-publish generates which we don't need
            PublishMutationUtilties.CleanPublishOutput(sharedFrameworkNameAndVersionRoot, "framework");

            // Rename the .deps file
            var destinationDeps = Path.Combine(sharedFrameworkNameAndVersionRoot, $"{s_sharedFrameworkName}.deps.json");
            File.Move(Path.Combine(sharedFrameworkNameAndVersionRoot, "framework.deps.json"), destinationDeps);
            PublishMutationUtilties.ChangeEntryPointLibraryName(destinationDeps, null);

            // Generate RID fallback graph
            GenerateRuntimeGraph(dotnetCli, destinationDeps);

            CopyHostArtifactsToSharedFramework(outputRootDirectory);
            
            if (File.Exists(Path.Combine(sharedFrameworkNameAndVersionRoot, "mscorlib.ni.dll")))
            {
                // Publish already places the crossgen'd version of mscorlib into the output, so we can
                // remove the IL version
                File.Delete(Path.Combine(sharedFrameworkNameAndVersionRoot, "mscorlib.dll"));
            }

            _crossgenUtil.CrossgenDirectory(sharedFrameworkNameAndVersionRoot, sharedFrameworkNameAndVersionRoot);

            // Generate .version file for sharedfx
            var version = _sharedFrameworkNugetVersion;
            var content = $@"{commitHash}{Environment.NewLine}{version}{Environment.NewLine}";
            File.WriteAllText(Path.Combine(sharedFrameworkNameAndVersionRoot, ".version"), content);

            return;
        }

        private void GenerateRuntimeGraph(DotNetCli dotnetCli, string destinationDeps)
        {
            string runtimeGraphGeneratorRuntime = null;
            switch (RuntimeEnvironment.OperatingSystemPlatform)
            {
                case Platform.Windows:
                    runtimeGraphGeneratorRuntime = "win";
                    break;
                case Platform.Linux:
                    runtimeGraphGeneratorRuntime = "linux";
                    break;
                case Platform.Darwin:
                    runtimeGraphGeneratorRuntime = "osx";
                    break;
            }
            if (!string.IsNullOrEmpty(runtimeGraphGeneratorRuntime))
            {
                var runtimeGraphGeneratorName = "RuntimeGraphGenerator";
                var runtimeGraphGeneratorProject = Path.Combine(Dirs.RepoRoot, "tools", runtimeGraphGeneratorName);
                var runtimeGraphGeneratorOutput = Path.Combine(Dirs.Output, "tools", runtimeGraphGeneratorName);

                dotnetCli.Publish(
                    "--output", runtimeGraphGeneratorOutput,
                    runtimeGraphGeneratorProject).Execute().EnsureSuccessful();
                var runtimeGraphGeneratorExe = Path.Combine(runtimeGraphGeneratorOutput, $"{runtimeGraphGeneratorName}{Constants.ExeSuffix}");

                Cmd(runtimeGraphGeneratorExe, "--project", _sharedFrameworkSourceRoot, "--deps", destinationDeps, runtimeGraphGeneratorRuntime)
                    .Execute()
                    .EnsureSuccessful();
            }
            else
            {
                throw new Exception($"Could not determine rid graph generation runtime for platform {RuntimeEnvironment.OperatingSystemPlatform}");
            }
        }

        private void CopyHostArtifactsToSharedFramework(string sharedFrameworkPublishRoot)
        {
            File.Copy(
                Path.Combine(_corehostLockedDirectory, HostArtifactNames.DotnetHostBaseName),
                Path.Combine(sharedFrameworkPublishRoot, HostArtifactNames.DotnetHostBaseName), true);
            File.Copy(
               Path.Combine(_corehostLockedDirectory, HostArtifactNames.DotnetHostBaseName),
               Path.Combine(sharedFrameworkPublishRoot, $"corehost{Constants.ExeSuffix}"), true);
            File.Copy(
                Path.Combine(_corehostLockedDirectory, HostArtifactNames.DotnetHostFxrBaseName),
                Path.Combine(sharedFrameworkPublishRoot, HostArtifactNames.DotnetHostFxrBaseName), true);

            // Hostpolicy should be the latest and not the locked version as it is supposed to evolve for
            // the framework and has a tight coupling with coreclr's API in the framework.
            File.Copy(
                Path.Combine(_corehostLatestDirectory, HostArtifactNames.HostPolicyBaseName),
                Path.Combine(sharedFrameworkPublishRoot, HostArtifactNames.HostPolicyBaseName), true);
        }

        private string GenerateSharedFrameworkProject(
            string sharedFrameworkNugetVersion,
            string sharedFrameworkTemplatePath, 
            string rid)
        {
            string sharedFrameworkProjectPath = Path.Combine(Dirs.Intermediate, "sharedFramework", "framework");
            Utils.DeleteDirectory(sharedFrameworkProjectPath);
            CopyRecursive(sharedFrameworkTemplatePath, sharedFrameworkProjectPath, true);

            string templateFile = Path.Combine(sharedFrameworkProjectPath, "project.json.template");
            JObject sharedFrameworkProject = JsonUtils.ReadProject(templateFile);
            sharedFrameworkProject["dependencies"]["Microsoft.NETCore.App"] = sharedFrameworkNugetVersion;
            ((JObject)sharedFrameworkProject["runtimes"]).RemoveAll();
            sharedFrameworkProject["runtimes"][rid] = new JObject();

            string projectJsonPath = Path.Combine(sharedFrameworkProjectPath, "project.json");
            JsonUtils.WriteProject(sharedFrameworkProject, projectJsonPath);

            Rm(templateFile);

            return sharedFrameworkProjectPath;
        }
    }
}
