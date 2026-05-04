namespace SocialAgent.Providers.Threads;

internal class ThreadsListResponse<T>
{
    public List<T>? Data { get; set; }
    public ThreadsPaging? Paging { get; set; }
}

internal class ThreadsPaging
{
    public ThreadsCursors? Cursors { get; set; }
    public string? Next { get; set; }
}

internal class ThreadsCursors
{
    public string? Before { get; set; }
    public string? After { get; set; }
}
