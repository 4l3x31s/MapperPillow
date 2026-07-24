using System.ComponentModel;
using System.Threading;

namespace MapperPillow;

/// <summary>
/// Diagnostics hook used to observe whether a mapping ran through a generated
/// compile-time interceptor (which calls <see cref="MarkIntercepted"/>) or fell
/// back to reflection (which does not). Not intended for application use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MapperPillowTelemetry
{
    private static int _interceptedCount;

    /// <summary>Number of mappings served by a compile-time interceptor.</summary>
    public static int InterceptedCount => Volatile.Read(ref _interceptedCount);

    /// <summary>Called by generated interceptor code. Do not call directly.</summary>
    public static void MarkIntercepted() => Interlocked.Increment(ref _interceptedCount);

    /// <summary>Resets the counter (test helper).</summary>
    public static void Reset() => Interlocked.Exchange(ref _interceptedCount, 0);
}
