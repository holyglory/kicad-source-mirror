using Google.Protobuf;
using Kiapi.Schematic.Types;
using KiCad.Automation.Model;

namespace KiCad.Automation.Native;

// Groups reference screen-owned objects; they do not own their lifetime. Check
// both snapshots before a delta can remove or reinterpret any member identity.
internal static class SchematicGroupGraph
{
    internal static void Validate(IReadOnlyDictionary<Guid, IMessage> items)
    {
        var groups = items.Where(pair => pair.Value is Group).ToDictionary(pair => pair.Key, pair => (Group)pair.Value);
        var parents = new Dictionary<Guid, Guid>();
        var childCounts = groups.Keys.ToDictionary(id => id, _ => 0);
        foreach (var (id, group) in groups)
        {
            var members = new HashSet<Guid>();
            foreach (var member in group.Items)
            {
                if (!Guid.TryParseExact(member.Value, "D", out var child) || child == Guid.Empty
                    || member.Value != child.ToString("D") || !items.ContainsKey(child))
                    throw Invalid("Group members must identify existing objects on the same screen.");
                if (child == id || !members.Add(child))
                    throw Invalid("A group cannot contain itself or duplicate member references.");
                if (!parents.TryAdd(child, id))
                    throw Invalid("An object cannot belong to more than one group.");
                if (groups.ContainsKey(child)) ++childCounts[id];
            }
        }
        // Leaf elimination avoids stack exhaustion on deeply nested input.
        var ready = new Queue<Guid>(childCounts.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        int visited = 0;
        while (ready.TryDequeue(out var id))
        {
            ++visited;
            if (parents.TryGetValue(id, out var parent) && --childCounts[parent] == 0) ready.Enqueue(parent);
        }
        if (visited != groups.Count) throw Invalid("Group membership contains a cycle.");
    }

    private static AutomationException Invalid(string message) => new("unsupported_schematic_delta", message);
}
