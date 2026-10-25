using System.Text.RegularExpressions;

namespace AgenticWorkflows.Tests;

public sealed partial class DocumentationTests
{
    [Fact]
    public void Repository_documentation_relative_file_links_resolve()
    {
        string root = SourceScriptTests.RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Append(Path.Combine(root, "README.md"));
        var failures = new List<string>();
        foreach (string file in files)
        {
            string text = CodeBlock().Replace(File.ReadAllText(file), "");
            foreach (Match match in Link().Matches(text))
            {
                string target = match.Groups[1].Value.Split('#')[0];
                if (target.Length == 0 || Uri.TryCreate(target, UriKind.Absolute, out _)) continue;
                string path = Path.GetFullPath(Uri.UnescapeDataString(target), Path.GetDirectoryName(file)!);
                if (!File.Exists(path) && !Directory.Exists(path)) failures.Add(Path.GetRelativePath(root, file) + ": " + target);
            }
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlock();
    [GeneratedRegex(@"\[[^\]\n]+\]\(([^\s)]+)\)")]
    private static partial Regex Link();
}
