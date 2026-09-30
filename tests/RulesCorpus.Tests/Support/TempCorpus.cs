using System.Text;
using System.Text.Json.Nodes;
using RulesCorpus.Adapters;

namespace RulesCorpus.Tests.Support;

/// <summary>A corpus directory under the temp directory, deleted on dispose.</summary>
internal sealed class TempCorpus : IDisposable
{
    public const string NotesText = "Alpha rule.\nBeta rule.\n\nGamma rule, with é.\n";

    public TempCorpus()
    {
        Root = Path.Combine(Path.GetTempPath(), "rules-corpus-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>
    /// A corpus whose every artifact is stored: one acquired source, one adapter derivation, one
    /// baseline. Verifies ok with nothing left unexamined.
    /// </summary>
    public static TempCorpus Stored(string notes = NotesText, string origin = "a person")
    {
        var corpus = new TempCorpus();
        corpus.WriteText("sources/notes.txt", notes);
        corpus.WriteBuild(StoredDefinition(origin));
        return corpus;
    }

    public static JsonObject StoredDefinition(string origin = "a person") => new()
    {
        ["schema"] = "rules-corpus/build/1",
        ["corpusId"] = "example",
        ["sources"] = new JsonArray(new JsonObject
        {
            ["id"] = "notes",
            ["path"] = "sources/notes.txt",
            ["mediaType"] = "text/plain",
            ["origin"] = origin,
            ["retrieved"] = "2026-01-02",
        }),
        ["derivations"] = new JsonArray(new JsonObject
        {
            ["id"] = "notes-lines",
            ["adapter"] = "lines",
            ["input"] = "notes",
            ["output"] = new JsonObject { ["id"] = "notes-canonical", ["path"] = "canonical/notes.txt" },
            ["parameters"] = new JsonObject(),
        }),
        ["external"] = new JsonArray(),
        ["baselines"] = new JsonArray(new JsonObject
        {
            ["sourceId"] = "notes",
            ["artifact"] = "notes",
            ["hashDerivation"] = "notes-bytes",
            ["asOf"] = "2026-01-01",
        }),
    };

    /// <summary>
    /// The format's own example shape: an unstored original, a stored text declared as its
    /// external derivation, and an adapter derivation over the text.
    /// </summary>
    public static TempCorpus WithExternal()
    {
        var corpus = new TempCorpus();
        corpus.WriteText("sources/rulebook.txt", NotesText);
        corpus.WriteBuild(new JsonObject
        {
            ["schema"] = "rules-corpus/build/1",
            ["corpusId"] = "example",
            ["sources"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "rulebook-pdf",
                    ["mediaType"] = "application/pdf",
                    ["stored"] = false,
                    ["bytes"] = 6031375,
                    ["digest"] = "sha256:" + new string('a', 64),
                    ["origin"] = "https://example.org/rulebook.pdf",
                    ["retrieved"] = "2026-09-14",
                },
                new JsonObject { ["id"] = "rulebook-text", ["path"] = "sources/rulebook.txt", ["mediaType"] = "text/plain" }),
            ["derivations"] = new JsonArray(new JsonObject
            {
                ["id"] = "rulebook-segmented",
                ["adapter"] = "lines",
                ["input"] = "rulebook-text",
                ["output"] = new JsonObject { ["id"] = "rulebook-canonical", ["path"] = "canonical/rulebook.txt" },
                ["parameters"] = new JsonObject { ["prefix"] = "r" },
            }),
            ["external"] = new JsonArray(new JsonObject
            {
                ["id"] = "rulebook-extraction",
                ["inputs"] = new JsonArray("rulebook-pdf"),
                ["output"] = "rulebook-text",
                ["tool"] = new JsonObject { ["id"] = "pdftotext", ["version"] = "24.02.0" },
                ["fidelity"] = "lossy-traceable",
                ["losses"] = new JsonArray("layout", "fonts", "images"),
            }),
            ["baselines"] = new JsonArray(new JsonObject
            {
                ["sourceId"] = "rulebook",
                ["artifact"] = "rulebook-text",
                ["hashDerivation"] = "rulebook-pdftotext-24.02.0-page-marked",
            }),
        });
        return corpus;
    }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void WriteText(string relative, string text) => WriteBytes(relative, Encoding.UTF8.GetBytes(text));

    public void WriteBytes(string relative, byte[] bytes)
    {
        string full = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public byte[] ReadBytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public bool Exists(string relative) => File.Exists(PathOf(relative));

    public void WriteBuild(JsonObject definition) => WriteText("corpus.build.json", definition.ToJsonString());

    public JsonObject ReadBuild() => (JsonObject)JsonNode.Parse(ReadBytes("corpus.build.json"))!;

    public void EditBuild(Action<JsonObject> edit)
    {
        JsonObject definition = ReadBuild();
        edit(definition);
        WriteBuild(definition);
    }

    public CorpusManifest Build(params ICorpusAdapter[] adapters) =>
        CorpusBuilder.Build(Root, adapters.Length == 0 ? [new LinesAdapter()] : adapters);

    public CorpusManifest Build(CorpusLimits limits, params ICorpusAdapter[] adapters) =>
        CorpusBuilder.Build(Root, adapters, limits);

    /// <summary>Every file under the corpus, by relative path, for byte-for-byte comparison.</summary>
    public SortedDictionary<string, byte[]> Snapshot()
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            files.Add(Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes(file));
        }

        return files;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
