using System.Diagnostics;

namespace AiDoc.Cli.Infrastructure;

public sealed record GitState(string? CommitSha, bool WorkingTreeDirty);

public sealed class GitInspector
{
    public GitState Inspect(string repositoryPath)
    {
        var commitSha = Execute(repositoryPath, "rev-parse", "HEAD");
        var status = Execute(repositoryPath, "status", "--porcelain");
        return new GitState(commitSha, !string.IsNullOrWhiteSpace(status));
    }

    private static string? Execute(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start git.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}