namespace DesktopTestDriver;
public static class WindowQuery
{
    public static PublicWindow? Find(PublicWindow[] windows, string? title, string? className, string? ownerWindowId, bool allowAbsent)
    {
        if (title == null && className == null && ownerWindowId == null) throw new GuiException("InvalidWindowSelector");
        var matches = windows.Where(w => (title == null || w.Title == title) && (className == null || w.ClassName == className) && (ownerWindowId == null || w.OwnerWindowId == ownerWindowId)).Take(2).ToArray();
        if (matches.Length > 1) throw new GuiException("AmbiguousTarget");
        if (matches.Length == 0 && !allowAbsent) throw new GuiException("WindowNotFound");
        return matches.SingleOrDefault();
    }
}
