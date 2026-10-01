using System.Text.Json.Nodes;
using RulesCorpus.Adapters;
using RulesCorpus.Tests.Support;

namespace RulesCorpus.Tests;

public class DiffTests
{
    private static CorpusManifest Built(string notes = TempCorpus.NotesText, string origin = "a person")
    {
        using TempCorpus corpus = TempCorpus.Stored(notes, origin);
        return corpus.Build();
    }

    [Fact]
    public void Identical_manifests_have_no_differences()
    {
        ManifestDiff diff = ManifestDiff.Compare(Built(), Built());

        Assert.True(diff.ContentDigestEqual);
        Assert.True(diff.ManifestDigestEqual);
        Assert.Empty(diff.ArtifactsAdded);
        Assert.Empty(diff.ArtifactsRemoved);
        Assert.Empty(diff.ArtifactsChanged);
        Assert.Empty(diff.DerivationsAdded);
        Assert.Empty(diff.DerivationsRemoved);
        Assert.Empty(diff.DerivationsChanged);
        Assert.Empty(diff.BaselinesChanged);
        Assert.Empty(diff.SegmentsAdded);
        Assert.Empty(diff.SegmentsRemoved);
        Assert.Empty(diff.SegmentsChanged);
    }

    [Fact]
    public void A_metadata_change_shows_the_changed_artifact_with_content_digest_equal()
    {
        ManifestDiff diff = ManifestDiff.Compare(Built(origin: "a person"), Built(origin: "another person"));

        Assert.True(diff.ContentDigestEqual);
        Assert.False(diff.ManifestDigestEqual);
        ManifestChange<ManifestArtifact> change = Assert.Single(diff.ArtifactsChanged);
        Assert.Equal("a person", change.Before.Acquisition!.Origin);
        Assert.Equal("another person", change.After.Acquisition!.Origin);
        Assert.Equal(change.Before.Digest, change.After.Digest);
        Assert.Empty(diff.BaselinesChanged);
        Assert.Empty(diff.SegmentsChanged);
    }

    [Fact]
    public void A_content_change_shows_changed_digests_baselines_and_segments_by_id()
    {
        ManifestDiff diff = ManifestDiff.Compare(
            Built("one\ntwo\nthree\n"),
            Built("one\nTWO\n"));

        Assert.False(diff.ContentDigestEqual);
        Assert.Equal(["notes", "notes-canonical"], diff.ArtifactsChanged.Select(c => c.After.Id));
        Assert.All(diff.ArtifactsChanged, c => Assert.NotEqual(c.Before.Digest, c.After.Digest));
        Assert.Equal("notes", Assert.Single(diff.BaselinesChanged).After.SourceId);
        Assert.Equal("l2", Assert.Single(diff.SegmentsChanged).After.Id);
        Assert.Equal("l3", Assert.Single(diff.SegmentsRemoved).Id);
        Assert.Empty(diff.SegmentsAdded);
    }

    [Fact]
    public void Added_segments_are_listed_in_the_second_manifests_order()
    {
        ManifestDiff diff = ManifestDiff.Compare(Built("one\n"), Built("one\ntwo\nthree\n"));

        Assert.Equal(["l2", "l3"], diff.SegmentsAdded.Select(s => s.Id));
        Assert.Empty(diff.SegmentsRemoved);
    }

    private static CorpusManifest WithDerivationEdit(Action<JsonObject> edit)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        edit((JsonObject)m["derivations"]![0]!);
        return CorpusManifest.Parse(ManifestEdits.Reseal(ManifestEdits.Bytes(m)));
    }

    [Fact]
    public void A_fidelity_and_losses_change_shows_the_changed_derivation()
    {
        ManifestDiff diff = ManifestDiff.Compare(Built(), WithDerivationEdit(d =>
        {
            d["fidelity"] = "lossy-traceable";
            d["losses"] = new JsonArray("line endings");
        }));

        Assert.False(diff.ManifestDigestEqual);
        ManifestChange<ManifestDerivation> change = Assert.Single(diff.DerivationsChanged);
        Assert.Equal(DerivationFidelity.Lossless, change.Before.Fidelity);
        Assert.Empty(change.Before.Losses);
        Assert.Equal(DerivationFidelity.LossyTraceable, change.After.Fidelity);
        Assert.Equal(["line endings"], change.After.Losses);
        Assert.Empty(diff.DerivationsAdded);
        Assert.Empty(diff.DerivationsRemoved);
        Assert.Empty(diff.ArtifactsChanged);
        Assert.Empty(diff.SegmentsChanged);
    }

    [Fact]
    public void A_parameter_change_shows_the_changed_derivation()
    {
        ManifestDiff diff = ManifestDiff.Compare(Built(), WithDerivationEdit(d => d["parameters"] = new JsonObject { ["prefix"] = "x" }));

        ManifestChange<ManifestDerivation> change = Assert.Single(diff.DerivationsChanged);
        Assert.Empty(change.Before.Parameters);
        Assert.Equal("x", change.After.Parameters["prefix"]);
    }

    [Fact]
    public void A_renamed_derivation_is_one_removed_and_one_added()
    {
        CorpusManifest after;
        using (TempCorpus corpus = TempCorpus.Stored())
        {
            corpus.Build();
            JsonObject m = ManifestEdits.Read(corpus);
            m["derivations"]![0]!["id"] = "renamed";
            m["artifacts"]![1]!["derivedBy"] = "renamed";
            after = CorpusManifest.Parse(ManifestEdits.Reseal(ManifestEdits.Bytes(m)));
        }

        ManifestDiff diff = ManifestDiff.Compare(Built(), after);

        Assert.Equal("renamed", Assert.Single(diff.DerivationsAdded).Id);
        Assert.Equal("notes-lines", Assert.Single(diff.DerivationsRemoved).Id);
        Assert.Empty(diff.DerivationsChanged);
    }
}
