using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The codec source pins in <c>ci.yml</c> and <c>codecs.yml</c> must name the same upstream tags.
///
/// <para><c>ci.yml</c> builds the decoders that a release actually ships, in the run that packs
/// them. <c>codecs.yml</c> builds the same pair monthly as a canary, so that an upstream tag which
/// moved or a toolchain which broke is discovered weeks before a tag meets it. That warning is
/// only worth anything while both files name the SAME versions: bump one and forget the other, and
/// the canary goes on cheerfully testing a combination nobody ships, while the thing that does
/// ship has never been built until the moment someone tags it.</para>
///
/// <para>This is config, not behaviour, so a unit test is the right place for it — the invariant
/// is visible in two text files and in nothing else. Nothing at build or run time reads these
/// values, so no other check could notice them drifting apart.</para>
/// </summary>
public class CodecPinAgreementTests(ITestOutputHelper output)
{
    private static readonly string[] Pins = ["VPX_TAG", "OPUS_TAG", "VPX_REPO", "OPUS_REPO"];

    [Fact]
    public void Both_workflows_build_the_same_codec_versions()
    {
        var ci = WorkflowEnv("ci.yml");
        var canary = WorkflowEnv("codecs.yml");

        foreach (var pin in Pins)
        {
            Assert.True(ci.ContainsKey(pin), $"ci.yml has no {pin}: the release would build unpinned sources");
            Assert.True(canary.ContainsKey(pin), $"codecs.yml has no {pin}: the canary tests nothing recognisable");
            output.WriteLine($"{pin}: {ci[pin]}");
            Assert.Equal(canary[pin], ci[pin]);
        }
    }

    /// <summary>A release must build its own decoders rather than fetch another run's, which is
    /// how three releases shipped or failed wrongly. Artifacts expire; a run cannot.</summary>
    [Fact]
    public void The_release_builds_its_own_natives_rather_than_fetching_another_runs()
    {
        var ci = File.ReadAllText(Workflow("ci.yml"));

        Assert.DoesNotContain("--workflow codecs.yml", ci, StringComparison.Ordinal);
        foreach (var rid in new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64" })
            Assert.Contains($"name: cupricodecs-{rid}", ci, StringComparison.Ordinal);
    }

    private static string Workflow(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CupriFace.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("repo root not found");
        return Path.Combine(root, ".github", "workflows", file);
    }

    /// <summary>The workflow's top-level <c>env:</c> block, which is the only one these pins live
    /// in. Read by indentation rather than with a YAML parser: the tests project takes no
    /// dependency on one, and the shape here is two-space keys under a column-zero heading.</summary>
    private static Dictionary<string, string> WorkflowEnv(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var inEnv = false;
        foreach (var raw in File.ReadAllLines(Workflow(file)))
        {
            if (raw.StartsWith("env:", StringComparison.Ordinal)) { inEnv = true; continue; }
            if (!inEnv) continue;
            // Any other column-zero line ends the block; blank lines and comments do not.
            if (raw.Length > 0 && !char.IsWhiteSpace(raw[0])) break;
            var match = Regex.Match(raw, @"^\s{2}([A-Z0-9_]+):\s*(\S+)\s*$");
            if (match.Success) map[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return map;
    }
}
