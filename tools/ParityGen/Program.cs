using System.Text;
using Islet.Core.Features;
var sb = new StringBuilder();
sb.AppendLine("# Feature parity: vorssaint-utils → Islet (Windows)");
sb.AppendLine();
sb.AppendLine("Generated from `src/Islet.Core/Features/FeatureCatalog.cs`; regenerate whenever a feature's status changes.");
sb.AppendLine();
string Status(FeatureInfo i) => i.Status switch
{
    WindowsStatus.Ported => "✅ Ported",
    WindowsStatus.Planned => "🛠️ Planned",
    WindowsStatus.NativeToggle => "🔁 Windows-setting toggle",
    WindowsStatus.Native => "🪟 Built into Windows",
    _ => "➖ Not on Windows",
};
var all = FeatureCatalog.All;
sb.AppendLine($"**{all.Count(i => i.Status == WindowsStatus.Ported)} ported**, " +
              $"{all.Count(i => i.Status is WindowsStatus.Planned or WindowsStatus.NativeToggle)} planned, " +
              $"{all.Count(i => i.Status == WindowsStatus.Native)} built into Windows, " +
              $"{all.Count(i => i.Status == WindowsStatus.Dropped)} not applicable — {all.Count} upstream features.");
sb.AppendLine();
foreach (var group in Enum.GetValues<FeatureGroup>())
{
    sb.AppendLine($"## {FeatureCatalog.GroupTitle(group)}");
    sb.AppendLine();
    sb.AppendLine("| Upstream id | Feature | Status | Milestone | Windows notes |");
    sb.AppendLine("|---|---|---|---|---|");
    foreach (var i in FeatureCatalog.InGroup(group))
        sb.AppendLine($"| `{i.Id}` | {i.Title} | {Status(i)} | {i.Milestone} | {(i.WindowsNote ?? "").Replace("|", "/")} |");
    sb.AppendLine();
}
File.WriteAllText(args[0], sb.ToString());
Console.WriteLine("wrote " + args[0]);
