namespace Halfway.Core;

public enum WorkspaceFocusTarget
{
    Primary,
    SubAgent
}

public static class WorkspaceNavigation
{
    public static IReadOnlyList<Guid> SidebarOrder(IEnumerable<SessionMetadata> sessions) =>
        sessions
            .OrderBy(session => session.Kind == AgentKind.Primary ? 0 : 1)
            .ThenBy(session => session.DisplayOrder)
            .Select(session => session.Id)
            .ToArray();

    public static Guid? Move(IReadOnlyList<Guid> orderedIds, Guid? selectedId, int offset)
    {
        if (orderedIds.Count == 0) return null;
        var currentIndex = selectedId is Guid id ? IndexOf(orderedIds, id) : -1;
        if (currentIndex < 0) return offset < 0 ? orderedIds[^1] : orderedIds[0];
        return orderedIds[(currentIndex + offset % orderedIds.Count + orderedIds.Count) % orderedIds.Count];
    }

    public static Guid? SelectTarget(WorkspaceFocusTarget target, Guid? selectedPrimaryId, Guid? selectedSubAgentId) =>
        target == WorkspaceFocusTarget.Primary ? selectedPrimaryId : selectedSubAgentId;

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var index = 0; index < ids.Count; index++)
            if (ids[index] == id) return index;
        return -1;
    }
}
