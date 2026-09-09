using RelaxKonOS.Client.Services;
using System.Diagnostics;
var entries = Enumerable.Range(0, 200).Select(i => new SettingsSearchEntry($"setting.{i}", "system",
    $"Environment {i}", "System", "HostMachine", i % 2 == 0 ? "HelperUnavailable" : "",
    $"environment {i} PATH 路径 环境变量 パス 環境変数")).ToArray();
var index = new SettingsSearchIndex(entries);
foreach (var term in new[] { "path", "路径", "環境変数" })
    if (index.Search(term).Count != 200) throw new Exception("alias or case search failed");
if (index.Search("PATH 199").Single().SettingId != "setting.199") throw new Exception("multi-term search failed");
if (index.Search("PATH").Count(e => e.Reason == "HelperUnavailable") != 100) throw new Exception("unavailable entries disappeared");
if (index.Search(" ").Count != 0 || index.Search("no-match").Count != 0) throw new Exception("empty result failed");
for (var i = 0; i < 50; i++) index.Search("PATH");
var timings = new double[1000];
for (var i = 0; i < timings.Length; i++)
{
    var start = Stopwatch.GetTimestamp();
    index.Search("PATH");
    timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
Array.Sort(timings);
Console.WriteLine($"200 entries, 1000 cached queries: p95={timings[949]:F3} ms, max={timings[^1]:F3} ms; alias/case/multi-term/unavailable/empty checks passed. No UI rendering measured.");
