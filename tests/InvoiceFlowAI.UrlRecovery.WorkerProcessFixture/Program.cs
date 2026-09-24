using System.Diagnostics;

if (args.Length == 2 && args[0] == "--child")
{
    await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

if (args.Length == 2 && args[0] == "--job-manifest")
{
    var pidFile = (await File.ReadAllTextAsync(args[1])).Trim();
    var child = new Process
    {
        StartInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        },
    };
    child.StartInfo.ArgumentList.Add("--child");
    child.StartInfo.ArgumentList.Add(pidFile);
    if (!child.Start()) return 2;
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

return 64;