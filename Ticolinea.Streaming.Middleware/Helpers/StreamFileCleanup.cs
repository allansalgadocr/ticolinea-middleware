namespace ticolinea.stream.service.Helpers;

// Files in STREAMS_FOLDER that the periodic cleanup must never delete.
// The placeholder slate is written once and never touched again, so any
// age-based sweep would otherwise remove it 20 minutes after creation.
public static class StreamFileCleanup
{
    public static bool IsProtected(string fileName) =>
        string.Equals(fileName, "placeholder_slate.ts", StringComparison.OrdinalIgnoreCase);
}
