using System.Text;
using System.Text.Json.Nodes;

namespace RulesCorpus.Tests.Support;

/// <summary>Edits a built corpus.json, optionally recomputing both digests so only the intended defect remains.</summary>
internal static class ManifestEdits
{
    public static JsonObject Read(TempCorpus corpus) => (JsonObject)JsonNode.Parse(corpus.ReadBytes("corpus.json"))!;

    public static byte[] Bytes(JsonObject manifest) => Encoding.UTF8.GetBytes(manifest.ToJsonString());

    public static void Write(TempCorpus corpus, JsonObject manifest) => corpus.WriteBytes("corpus.json", Bytes(manifest));

    /// <summary>Recomputes contentDigest and manifestDigest over the edited manifest.</summary>
    public static byte[] Reseal(byte[] manifest)
    {
        var errors = new List<CorpusError>();
        (CorpusManifest? m, _) = CorpusManifest.ReadSchema(manifest, errors);
        Assert.Empty(errors);
        return CorpusBuilder.Compose(m!.CorpusId, [.. m.Artifacts], [.. m.Derivations], [.. m.Baselines], [.. m.Segments]).ToUtf8Json();
    }

    public static CorpusException ParseFails(byte[] manifest) =>
        Assert.Throws<CorpusException>(() => CorpusManifest.Parse(manifest));

    public static void AssertError(CorpusException e, string path, string reasonFragment)
    {
        Assert.True(
            e.Errors.Any(x => x.Path == path && x.Reason.Contains(reasonFragment, StringComparison.Ordinal)),
            $"expected an error at {path} containing '{reasonFragment}'; got:\n{string.Join("\n", e.Errors)}");
    }
}
