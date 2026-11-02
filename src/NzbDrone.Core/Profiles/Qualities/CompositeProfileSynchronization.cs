namespace NzbDrone.Core.Profiles.Qualities
{
    internal static class CompositeProfileSynchronization
    {
        // Configuration and assignment must not invalidate one another's checks.
        internal static object SyncRoot { get; } = new object();
    }
}
