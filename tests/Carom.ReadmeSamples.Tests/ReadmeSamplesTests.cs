using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Carom.ReadmeSamples.Tests
{
    /// <summary>
    /// The samples compile because this project builds ReadmeSamples.cs. These tests keep
    /// that file and README.md saying the same thing, so a README edit that is not compiled
    /// fails here.
    /// </summary>
    public class ReadmeSamplesTests
    {
        public static IEnumerable<object[]> Samples => new[]
        {
            new object[] { "basic-usage" },
            new object[] { "attempt-timeout" },
            new object[] { "delay-hint" },
        };

        [Theory]
        [MemberData(nameof(Samples))]
        public void ReadmeSample_MatchesTheCompiledSample(string name)
        {
            var root = FindRepoRoot();
            var readme = File.ReadAllText(Path.Combine(root, "README.md"));
            var compiled = File.ReadAllText(Path.Combine(root, "tests", "Carom.ReadmeSamples.Tests", "ReadmeSamples.cs"));

            var fromReadme = ReadmeBlock(readme, name);
            var fromSource = CompiledFragments(compiled, name);

            Assert.True(fromReadme != null, $"README.md has no `<!-- sample: {name} -->` block.");
            Assert.True(Normalize(fromReadme!) == Normalize(fromSource),
                $"README sample '{name}' differs from tests/Carom.ReadmeSamples.Tests/ReadmeSamples.cs. " +
                "Change both together so the README text is the code that compiles.");
        }

        private static string? ReadmeBlock(string readme, string name)
        {
            var m = Regex.Match(readme,
                "<!-- sample: " + Regex.Escape(name) + " -->\\s*```csharp\\r?\\n(.*?)```",
                RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string CompiledFragments(string source, string name)
        {
            var parts = new List<string>();
            foreach (Match m in Regex.Matches(source,
                "// readme:begin " + Regex.Escape(name) + "\\r?\\n(.*?)// readme:end " + Regex.Escape(name),
                RegexOptions.Singleline))
            {
                parts.Add(m.Groups[1].Value);
            }
            Assert.True(parts.Count > 0, $"ReadmeSamples.cs has no fragment named '{name}'.");
            return string.Join("\n", parts);
        }

        private static string Normalize(string text) =>
            string.Join("\n", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Carom.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "Could not find Carom.sln above the test output directory.");
            return dir!.FullName;
        }
    }
}
