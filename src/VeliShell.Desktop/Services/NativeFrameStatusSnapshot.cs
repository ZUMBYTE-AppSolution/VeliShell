namespace VeliShell.Desktop.Services;

// Pure projection of current identities, separately testable without touching
// a desktop. A current binding wins over an old failed/unsupported attempt.
internal static class NativeFrameStatusSnapshot
{
    internal static NativeFrameStatus[] Merge(IEnumerable<NativeFrameStatus> bindings,
        IEnumerable<NativeFrameStatus> failures, IEnumerable<NativeFrameStatus> skipped,
        Func<NativeFrameStatus, bool> isCurrent)
    {
        var rows = new Dictionary<(long Window,int Pid,long Started), NativeFrameStatus>();
        void Add(NativeFrameStatus row) {
            if(isCurrent(row)) rows.TryAdd((row.Window,row.Pid,row.Started),row);
        }
        foreach(var row in bindings) Add(row);
        var skip=skipped.ToArray();
        // A deliberate exclusion supersedes a past activation failure.
        foreach(var row in skip.Where(row=>row.Reason=="Excluded")) Add(row);
        foreach(var row in failures) Add(row);
        foreach(var row in skip.Where(row=>row.Reason!="Excluded")) Add(row);
        return rows.Values.Take(100).ToArray();
    }
}
