using RulesCorpus.Adapters;

namespace RulesCorpus.Internal;

/// <summary>The written spellings of the format's enumerations, in one place.</summary>
internal static class Vocabulary
{
    public const string ManifestSchema = "rules-corpus/manifest/1";
    public const string BuildSchema = "rules-corpus/build/1";
    public const string ManifestFile = "corpus.json";
    public const string BuildFile = "corpus.build.json";

    public static string Write(ArtifactRole role) => role switch
    {
        ArtifactRole.Source => "source",
        ArtifactRole.Derived => "derived",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static ArtifactRole? ReadRole(string value) => value switch
    {
        "source" => ArtifactRole.Source,
        "derived" => ArtifactRole.Derived,
        _ => null,
    };

    public static string Write(DerivationReproducibility value) => value switch
    {
        DerivationReproducibility.Reproducible => "reproducible",
        DerivationReproducibility.External => "external",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static DerivationReproducibility? ReadReproducibility(string value) => value switch
    {
        "reproducible" => DerivationReproducibility.Reproducible,
        "external" => DerivationReproducibility.External,
        _ => null,
    };

    public static bool IsDefined(DerivationFidelity value) =>
        value is DerivationFidelity.Lossless or DerivationFidelity.LossyTraceable or DerivationFidelity.NonReversible;

    public static string Write(DerivationFidelity value) => value switch
    {
        DerivationFidelity.Lossless => "lossless",
        DerivationFidelity.LossyTraceable => "lossy-traceable",
        DerivationFidelity.NonReversible => "non-reversible",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static DerivationFidelity? ReadFidelity(string value) => value switch
    {
        "lossless" => DerivationFidelity.Lossless,
        "lossy-traceable" => DerivationFidelity.LossyTraceable,
        "non-reversible" => DerivationFidelity.NonReversible,
        _ => null,
    };
}
