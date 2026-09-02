using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Gates for the claims published in README.md and docs/BENCHMARKS.md.
    /// If one of these fails, a published number has gone stale: fix the
    /// sentence the failure message names, or the code, before shipping.
    /// </summary>
    public class PublishedClaimsTests
    {
        // README says 20 KB core, 50 KB extensions; bounds catch a doubling without flaking on growth.
        private const long CoreSizeBoundBytes = 40 * 1024;
        private const long ExtensionsSizeBoundBytes = 100 * 1024;

        private static readonly Func<int> SucceedingAction = static () => 42;

        [Fact]
        public void Shot_SuccessfulSyncCall_AllocatesZeroBytes()
        {
            // Warm up so JIT and first-call setup do not count.
            for (int i = 0; i < 10_000; i++)
            {
                Carom.Shot(SucceedingAction);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 10_000; i++)
            {
                Carom.Shot(SucceedingAction);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0,
                $"Carom.Shot allocated {allocated} bytes over 10,000 successful calls. " +
                "This breaks the README claim 'Zero allocations on the successful hot path' " +
                "and the 0 B per call figure in docs/BENCHMARKS.md.");
        }

        [Fact]
        public void CaromAssembly_StaysWithinPublishedSizeBound()
        {
            var path = typeof(Carom).Assembly.Location;
            Assert.False(string.IsNullOrEmpty(path), "Carom assembly has no file location; cannot verify its size.");

            long size = new FileInfo(path).Length;

            Assert.True(size <= CoreSizeBoundBytes,
                $"Carom.dll is {size} bytes, over the {CoreSizeBoundBytes} byte bound. " +
                "The README claims a 20 KB core (the 'Tiny Footprint' bullet and the package table). " +
                "Update the README sizes and this bound together, deliberately.");
        }

        [Fact]
        public void CaromExtensionsAssembly_StaysWithinPublishedSizeBound()
        {
            // Carom.Tests does not reference Carom.Extensions, so check the src build output; a solution-level build always produces it.
            var extensionsBin = Path.Combine(FindRepoRoot(), "src", "Carom.Extensions", "bin");
            if (!Directory.Exists(extensionsBin))
            {
                return;
            }

            var built = Directory.GetFiles(extensionsBin, "Carom.Extensions.dll", SearchOption.AllDirectories);
            foreach (var path in built)
            {
                long size = new FileInfo(path).Length;
                Assert.True(size <= ExtensionsSizeBoundBytes,
                    $"Carom.Extensions.dll at {path} is {size} bytes, over the {ExtensionsSizeBoundBytes} byte bound. " +
                    "The README claims 50 KB extensions (the 'Tiny Footprint' bullet and the package table). " +
                    "Update the README sizes and this bound together, deliberately.");
            }
        }

        [Fact]
        public void CaromAssembly_ReferencesOnlyTheFramework()
        {
            var references = typeof(Carom).Assembly.GetReferencedAssemblies();

            foreach (var reference in references)
            {
                var name = reference.Name ?? string.Empty;
                bool isFramework = name == "netstandard"
                    || name == "mscorlib"
                    || name == "System"
                    || name.StartsWith("System.", StringComparison.Ordinal);

                Assert.True(isFramework,
                    $"Carom references assembly '{name}', which is not part of the framework. " +
                    "This breaks the README claim 'Zero package dependencies on every target'.");
            }
        }

        [Fact]
        public void CaromPackage_DeclaresZeroPackageDependencies()
        {
            var csproj = Path.Combine(FindRepoRoot(), "src", "Carom", "Carom.csproj");
            Assert.True(File.Exists(csproj), $"Expected {csproj} to exist.");

            var text = File.ReadAllText(csproj);

            Assert.False(text.Contains("<PackageReference"),
                "src/Carom/Carom.csproj has a PackageReference. The Carom package would ship with " +
                "a dependency, breaking the README claim 'Zero package dependencies on every target'.");
        }

        [Fact]
        public void CaromExtensionsPackage_DependsOnlyOnCarom()
        {
            var csproj = Path.Combine(FindRepoRoot(), "src", "Carom.Extensions", "Carom.Extensions.csproj");
            Assert.True(File.Exists(csproj), $"Expected {csproj} to exist.");

            var lines = File.ReadAllLines(csproj);

            Assert.False(lines.Any(l => l.Contains("<PackageReference")),
                "src/Carom.Extensions/Carom.Extensions.csproj has a PackageReference. The package would " +
                "ship with a dependency beyond Carom, breaking the README dependency claim.");

            var projectRefs = lines.Where(l => l.Contains("<ProjectReference")).ToArray();
            Assert.True(projectRefs.All(l => l.Contains("Carom.csproj")),
                "src/Carom.Extensions/Carom.Extensions.csproj references a project other than Carom. " +
                "The README says Carom.Extensions depends only on Carom.");
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Carom.sln")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                "Could not find Carom.sln above the test output directory. " +
                "These gates read source files from the repository and must run from a checkout.");
        }
    }
}
