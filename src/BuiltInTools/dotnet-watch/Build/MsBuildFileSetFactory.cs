﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Graph;

namespace Microsoft.DotNet.Watch
{
    /// <summary>
    /// Used to collect a set of files to watch.
    ///
    /// Invokes msbuild to evaluate <see cref="TargetName"/> on the root project, which traverses all project dependencies and collects
    /// items that are to be watched. The target can be customized by defining CustomCollectWatchItems target. This is currently done by Razor SDK
    /// to collect razor and cshtml files.
    ///
    /// Consider replacing with <see cref="Build.Graph.ProjectGraph"/> traversal (https://github.com/dotnet/sdk/issues/40214).
    /// </summary>
    internal class MSBuildFileSetFactory(
        string rootProjectFile,
        IEnumerable<string> buildArguments,
        EnvironmentOptions environmentOptions,
        ProcessRunner processRunner,
        IReporter reporter)
    {
        private const string TargetName = "GenerateWatchList";
        private const string WatchTargetsFileName = "DotNetWatch.targets";

        public string RootProjectFile => rootProjectFile;

        // Virtual for testing.
        public virtual async ValueTask<EvaluationResult?> TryCreateAsync(bool? requireProjectGraph, CancellationToken cancellationToken)
        {
            var watchList = Path.GetTempFileName();
            try
            {
                var projectDir = Path.GetDirectoryName(rootProjectFile);
                var arguments = GetMSBuildArguments(watchList);
                var capturedOutput = new List<OutputLine>();

                var processSpec = new ProcessSpec
                {
                    Executable = environmentOptions.MuxerPath,
                    WorkingDirectory = projectDir,
                    Arguments = arguments,
                    OnOutput = line =>
                    {
                        lock (capturedOutput)
                        {
                            capturedOutput.Add(line);
                        }
                    }
                };

                reporter.Verbose($"Running MSBuild target '{TargetName}' on '{rootProjectFile}'");

                var exitCode = await processRunner.RunAsync(processSpec, reporter, isUserApplication: false, launchResult: null, cancellationToken);

                var success = exitCode == 0 && File.Exists(watchList);

                if (!success)
                {
                    reporter.Error($"Error(s) finding watch items project file '{Path.GetFileName(rootProjectFile)}'.");
                    reporter.Output($"MSBuild output from target '{TargetName}':");
                }

                BuildOutput.ReportBuildOutput(reporter, capturedOutput, success, projectDisplay: null);
                if (!success)
                {
                    return null;
                }

                using var watchFile = File.OpenRead(watchList);
                var result = await JsonSerializer.DeserializeAsync<MSBuildFileSetResult>(watchFile, cancellationToken: cancellationToken);
                Debug.Assert(result != null);

                var fileItems = new Dictionary<string, FileItem>();
                foreach (var (projectPath, projectItems) in result.Projects)
                {
                    foreach (var filePath in projectItems.Files)
                    {
                        AddFile(filePath, staticWebAssetPath: null);
                    }

                    foreach (var staticFile in projectItems.StaticFiles)
                    {
                        AddFile(staticFile.FilePath, staticFile.StaticWebAssetPath);
                    }

                    void AddFile(string filePath, string? staticWebAssetPath)
                    {
                        if (!fileItems.TryGetValue(filePath, out var existingFile))
                        {
                            fileItems.Add(filePath, new FileItem
                            {
                                FilePath = filePath,
                                ContainingProjectPaths = [projectPath],
                                StaticWebAssetPath = staticWebAssetPath,
                            });
                        }
                        else if (!existingFile.ContainingProjectPaths.Contains(projectPath))
                        {
                            // linked files might be included to multiple projects:
                            existingFile.ContainingProjectPaths.Add(projectPath);
                        }
                    }
                }

                reporter.Verbose($"Watching {fileItems.Count} file(s) for changes");
#if DEBUG

                foreach (var file in fileItems.Values)
                {
                    reporter.Verbose($"  -> {file.FilePath} {file.StaticWebAssetPath}");
                }

                Debug.Assert(fileItems.Values.All(f => Path.IsPathRooted(f.FilePath)), "All files should be rooted paths");
#endif

                // Load the project graph after the project has been restored:
                ProjectGraph? projectGraph = null;
                if (requireProjectGraph != null)
                {
                    projectGraph = TryLoadProjectGraph(requireProjectGraph.Value, cancellationToken);
                    if (projectGraph == null && requireProjectGraph == true)
                    {
                        return null;
                    }
                }

                return new EvaluationResult(fileItems, projectGraph);
            }
            finally
            {
                File.Delete(watchList);
            }
        }

        private IReadOnlyList<string> GetMSBuildArguments(string watchListFilePath)
        {
            var watchTargetsFile = FindTargetsFile();

            var arguments = new List<string>
            {
                "msbuild",
                "/restore",
                "/nologo",
                "/v:m",
                rootProjectFile,
                "/t:" + TargetName
            };

#if !DEBUG
            if (environmentOptions.TestFlags.HasFlag(TestFlags.RunningAsTest))
#endif
            {
                arguments.Add($"/bl:{Path.Combine(environmentOptions.TestOutput, "DotnetWatch.GenerateWatchList.binlog")}");
            }

            arguments.AddRange(buildArguments);

            // Set dotnet-watch reserved properties after the user specified propeties,
            // so that the former take precedence.

            if (environmentOptions.SuppressHandlingStaticContentFiles)
            {
                arguments.Add("/p:DotNetWatchContentFiles=false");
            }

            arguments.Add("/p:_DotNetWatchListFile=" + watchListFilePath);
            arguments.Add("/p:DotNetWatchBuild=true"); // extensibility point for users
            arguments.Add("/p:DesignTimeBuild=true"); // don't do expensive things
            arguments.Add("/p:CustomAfterMicrosoftCommonTargets=" + watchTargetsFile);
            arguments.Add("/p:CustomAfterMicrosoftCommonCrossTargetingTargets=" + watchTargetsFile);

            return arguments;
        }

        private static string FindTargetsFile()
        {
            var assemblyDir = Path.GetDirectoryName(typeof(MSBuildFileSetFactory).Assembly.Location);
            Debug.Assert(assemblyDir != null);

            var searchPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "assets"),
                Path.Combine(assemblyDir, "assets"),
                AppContext.BaseDirectory,
                assemblyDir,
            };

            var targetPath = searchPaths.Select(p => Path.Combine(p, WatchTargetsFileName)).FirstOrDefault(File.Exists);
            return targetPath ?? throw new FileNotFoundException("Fatal error: could not find DotNetWatch.targets");
        }

        // internal for testing

        /// <summary>
        /// Tries to create a project graph by running the build evaluation phase on the <see cref="rootProjectFile"/>.
        /// </summary>
        internal ProjectGraph? TryLoadProjectGraph(bool projectGraphRequired, CancellationToken cancellationToken)
        {
            var globalOptions = new Dictionary<string, string>();

            foreach (var (name, value) in CommandLineOptions.ParseBuildProperties(buildArguments))
            {
                globalOptions[name] = value;
            }

            var entryPoint = new ProjectGraphEntryPoint(rootProjectFile, globalOptions);

            try
            {
                return new ProjectGraph([entryPoint], ProjectCollection.GlobalProjectCollection, projectInstanceFactory: null, cancellationToken);
            }
            catch (Exception e)
            {
                reporter.Verbose("Failed to load project graph.");

                if (e is AggregateException { InnerExceptions: var innerExceptions })
                {
                    foreach (var inner in innerExceptions)
                    {
                        Report(inner);
                    }
                }
                else
                {
                    Report(e);
                }

                void Report(Exception e)
                {
                    if (projectGraphRequired)
                    {
                        reporter.Error(e.Message);
                    }
                    else
                    {
                        reporter.Warn(e.Message);
                    }
                }
            }

            return null;
        }
    }
}
