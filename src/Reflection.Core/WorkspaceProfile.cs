namespace Reflection.Core;

// Daily data keeps its existing location. Private content has its own complete store.
public sealed record WorkspaceProfile(string BaseRoot, bool IsPrivate = false)
{
    public string Root => IsPrivate ? Path.Combine(Path.GetFullPath(BaseRoot), "private") : Path.GetFullPath(BaseRoot);
    public WorkspaceProfile Other => this with { IsPrivate = !IsPrivate };
}
