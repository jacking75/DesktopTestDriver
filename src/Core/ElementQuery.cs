namespace DesktopTestDriver;

public static class ElementQuery
{
    public static bool Matches(ObservationNode node, ElementSelector selector, Observation observation, int depth = 0)
        => Matches(node, selector, id => observation.Nodes.SingleOrDefault(n => n.ElementId == id), depth);

    private static bool Matches(ObservationNode node, ElementSelector selector, Func<string?, ObservationNode?> lookup, int depth = 0)
    {
        if (depth > 8) throw new GuiException("InvalidSelector");
        if (selector.AutomationId != null && node.AutomationId != selector.AutomationId ||
            selector.ControlType != null && node.ControlType != selector.ControlType ||
            selector.Name != null && node.Name != selector.Name ||
            selector.ClassName != null && node.ClassName != selector.ClassName ||
            selector.FrameworkId != null && node.FrameworkId != selector.FrameworkId) return false;
        if (selector.Parent != null)
        {
            var parent = lookup(node.ParentId);
            if (parent == null || !Matches(parent, selector.Parent, lookup, depth + 1)) return false;
        }
        if (selector.AncestorAutomationId != null)
        {
            var parentId = node.ParentId; var visited = new HashSet<string>(); bool found = false;
            while (parentId != null && visited.Add(parentId))
            {
                var parent = lookup(parentId);
                if (parent == null) break;
                if (parent.AutomationId == selector.AncestorAutomationId) { found = true; break; }
                parentId = parent.ParentId;
            }
            if (!found) return false;
        }
        return true;
    }
    public static ObservationNode? Find(Observation observation, ElementSelector selector, bool allowAbsent = false)
    {
        // An unseen duplicate in a truncated subtree is just as unsafe as an unseen match.
        if (!observation.Complete || observation.ProviderErrors.Length != 0) throw new GuiException("SearchIncomplete");
        var index = selector.Parent != null || selector.AncestorAutomationId != null
            ? observation.Nodes.ToDictionary(n => n.ElementId) : null;
        ObservationNode? Lookup(string? id) => id == null ? null : index!.GetValueOrDefault(id);
        var matches = observation.Nodes.Where(n => Matches(n, selector, Lookup)).Take(2).ToArray();
        if (matches.Length > 1) throw new GuiException("AmbiguousTarget");
        if (matches.Length == 0 && !allowAbsent) throw new GuiException("ElementNotFound");
        return matches.SingleOrDefault();
    }
}
