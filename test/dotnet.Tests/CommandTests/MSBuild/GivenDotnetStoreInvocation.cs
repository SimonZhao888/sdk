// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.Cli.Commands.Store;

namespace Microsoft.DotNet.Cli.MSBuild.Tests
{
    [Collection(TestConstants.UsesStaticTelemetryState)]
    public class GivenDotnetStoreInvocation : IClassFixture<NullCurrentSessionIdFixture>
    {
        string[] ExpectedPrefix = ["-maxcpucount", "--verbosity:m", "-tlp:default=auto", "-nologo", "--target:ComposeStore", "<project>"];
        static readonly string[] ArgsPrefix = ["--manifest", "<project>"];
        private static readonly string WorkingDirectory =
            TestPathUtilities.FormatAbsolutePath(nameof(GivenDotnetStoreInvocation));

        [Theory]
        [InlineData("-m")]
        [InlineData("--manifest")]
        public void ItAddsProjectToMsbuildInvocation(string optionName)
        {
            var msbuildPath = "<msbuildpath>";
            string[] args = new string[] { optionName, "<project>" };
            StoreCommand.FromArgs(args, msbuildPath)
                .GetArgumentTokensToMSBuild().Should().Contain(ExpectedPrefix);
        }

        [Theory]
        [InlineData(new string[] { "-f", "<tfm>" }, @"--property:TargetFramework=<tfm>")]
        [InlineData(new string[] { "--framework", "<tfm>" }, @"--property:TargetFramework=<tfm>")]
        [InlineData(new string[] { "-r", "<rid>" }, @"--property:RuntimeIdentifier=<rid> --property:_CommandLineDefinedRuntimeIdentifier=true")]
        [InlineData(new string[] { "-r", "linux-amd64" }, @"--property:RuntimeIdentifier=linux-x64 --property:_CommandLineDefinedRuntimeIdentifier=true")]
        [InlineData(new string[] { "--runtime", "<rid>" }, @"--property:RuntimeIdentifier=<rid> --property:_CommandLineDefinedRuntimeIdentifier=true")]
        [InlineData(new string[] { "--use-current-runtime" }, "--property:UseCurrentRuntimeIdentifier=True")]
        [InlineData(new string[] { "--ucr" }, "--property:UseCurrentRuntimeIdentifier=True")]
        [InlineData(new string[] { "--manifest", "one.xml", "--manifest", "two.xml", "--manifest", "three.xml" }, @"--property:AdditionalProjects=<cwd>one.xml%3B<cwd>two.xml%3B<cwd>three.xml")]
        [InlineData(new string[] { "--disable-build-servers" }, "--property:UseRazorBuildServer=false --property:UseSharedCompilation=false /nodeReuse:false")]
        public void MsbuildInvocationIsCorrect(string[] args, string expectedAdditionalArgs)
        {
            CommandDirectoryContext.PerformActionWithBasePath(WorkingDirectory, () =>
            {
                args = ArgsPrefix.Concat(args).ToArray();
                string[] expectedarr =
                    (string.IsNullOrEmpty(expectedAdditionalArgs) ? "" : $" {expectedAdditionalArgs}")
                    .Replace("<cwd>", WorkingDirectory)
                    .Split(" ", StringSplitOptions.RemoveEmptyEntries);

                var msbuildPath = "<msbuildpath>";
                List<string> expected = [.. ExpectedPrefix, .. expectedarr];
                expected.Should().BeSubsetOf(
                    StoreCommand.FromArgs(args, msbuildPath).GetArgumentTokensToMSBuild()
                );
            });
        }

        [Theory]
        [InlineData("-o")]
        [InlineData("--output")]
        public void ItAddsOutputPathToMsBuildInvocation(string optionName)
        {
            string path = Path.Combine("some", "path");
            var args = ArgsPrefix.Concat(new string[] { optionName, path }).ToArray();

            var msbuildPath = "<msbuildpath>";
            StoreCommand.FromArgs(args, msbuildPath)
                .GetArgumentTokensToMSBuild().Should().BeEquivalentTo([..ExpectedPrefix, $"--property:ComposeDir={Path.GetFullPath(path)}", "--property:_CommandLineDefinedOutputPath=true"]);
        }
    }
}
