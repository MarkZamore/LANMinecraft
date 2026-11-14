namespace Minecraft;

internal static class PreparationLabels
{
    internal static string[] NumberRepeated(params string[] labels)
    {
        var steps = labels.GroupBy(label => label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (Total: group.Count(), Seen: 0), StringComparer.Ordinal);
        var numbered = new string[labels.Length];
        for (var index = 0; index < labels.Length; index++)
        {
            var label = labels[index];
            var (total, seen) = steps[label];
            numbered[index] = total > 1 ? $"{label} {seen + 1}/{total}" : label;
            steps[label] = (total, seen + 1);
        }
        return numbered;
    }
}
