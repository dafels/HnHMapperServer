namespace HnHMapperServer.Services.Alignment;

/// <summary>
/// Small, pure numeric helpers shared by the public-map alignment solver and the
/// tenant-import aligner. Keeping a single implementation guarantees both paths agree
/// on how a median offset is computed.
/// </summary>
public static class AlignmentMath
{
    /// <summary>
    /// Integer median. For an even count returns the (truncating) average of the two
    /// central values — identical semantics to the original
    /// <c>PublicMapTenantImportService.Median</c> so behaviour is unchanged after the lift.
    /// Does not mutate the caller's list.
    /// </summary>
    public static int Median(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("Cannot take the median of an empty sequence.", nameof(values));

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
