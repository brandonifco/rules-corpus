using RulesCorpus.Files;

namespace RulesCorpus;

/// <summary>
/// Packs a corpus directory into its canonical tar (decision 0006): the same corpus always
/// packs to the same bytes, so the archive's own digest identifies it.
/// </summary>
public static class CorpusPacker
{
    /// <summary>
    /// Verifies the corpus, then writes <c>corpus.build.json</c>, <c>corpus.json</c> and every
    /// stored artifact to <paramref name="destination"/> in ordinal path order, each re-read and
    /// checked against the digest verification saw, so what is packed is what was verified.
    /// </summary>
    /// <param name="corpusDirectory">The corpus directory.</param>
    /// <param name="destination">Where the archive is written. On an exception it may hold a partial archive and must be discarded.</param>
    /// <param name="options">Verification options, such as a rebuild with adapters.</param>
    /// <param name="allowNotVerified">
    /// Pack even when some checks are not verified, as for a corpus that declares an unstored
    /// artifact. Never packs a corpus with a failed check.
    /// </param>
    /// <returns>The verification report the pack was made under.</returns>
    /// <exception cref="CorpusException">Verification did not allow packing, or a file changed after it was verified.</exception>
    public static VerificationReport Pack(
        string corpusDirectory, Stream destination, VerificationOptions? options = null, bool allowNotVerified = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(corpusDirectory);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new VerificationOptions();
        var files = new DirectoryCorpusFiles(corpusDirectory);
        VerificationReport report = CorpusVerifier.Verify(files, options, out Dictionary<string, ContentDigest> verified);

        bool allowed = report.Outcome == VerificationOutcome.Ok
            || (report.Outcome == VerificationOutcome.NotVerified && allowNotVerified);
        if (!allowed)
        {
            List<CorpusError> reasons = report.Checks
                .Where(c => c.Outcome != VerificationOutcome.Ok)
                .Select(c => new CorpusError(c.Name, $"{c.Outcome}: {c.Detail}"))
                .ToList();
            throw new CorpusException(
                report.Outcome == VerificationOutcome.Failed
                    ? "The corpus failed verification and was not packed."
                    : "Some checks were not verified; the corpus was not packed (allow not-verified checks to pack it anyway).",
                reasons);
        }

        CorpusLimits limits = options.Limits ?? CorpusLimits.Default;
        long max = Math.Max(limits.MaxArtifactBytes, limits.MaxManifestBytes);
        foreach (string path in verified.Keys.Order(StringComparer.Ordinal))
        {
            if (!files.TryRead(path, max, out byte[] bytes, out string problem))
            {
                throw new CorpusException("A verified file could not be re-read for packing.", [new CorpusError(path, problem)]);
            }

            if (ContentDigest.Compute(bytes) != verified[path])
            {
                throw new CorpusException("A file changed after it was verified.", [new CorpusError(path, "its bytes no longer match the digest verification saw")]);
            }

            PaxTarWriter.WriteEntry(destination, path, bytes);
        }

        PaxTarWriter.WriteEnd(destination);
        return report;
    }
}
