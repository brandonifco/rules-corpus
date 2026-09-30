using System.Text.RegularExpressions;

namespace RulesCorpus.Adapters.Text;

internal enum Segmentation
{
    Whole,
    Blocks,
    Headings,
}

/// <summary>
/// The derivation's parameters, parsed and cross-checked before any input byte is read. A
/// parameter that would not shape the output is refused along with unknown ones: the build
/// records every parameter as though it mattered, so one that did nothing would be a false
/// derivation record.
/// </summary>
internal sealed class TextOptions
{
    private const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly string KnownKeys =
        "bom, headingPattern, newlines, pageMarker, pagesContiguous, segmentation";

    private TextOptions()
    {
    }

    public bool StripBom { get; private set; }

    public bool PreserveNewlines { get; private set; }

    public Segmentation Segmentation { get; private set; }

    public Regex? HeadingPattern { get; private set; }

    public bool HeadingHasLocatorGroup { get; private set; }

    public Regex? PageMarker { get; private set; }

    public int PageMarkerGroup { get; private set; }

    public bool PagesContiguous { get; private set; }

    public static TextOptions Parse(IReadOnlyDictionary<string, string> parameters)
    {
        var options = new TextOptions();
        bool sawPagesContiguous = false;

        // Parameters arrive in ordinal key order, so the first refusal is the same on every run.
        foreach (var (key, value) in parameters)
        {
            switch (key)
            {
                case "bom":
                    options.StripBom = Choose(key, value, "reject", "strip");
                    break;
                case "newlines":
                    options.PreserveNewlines = Choose(key, value, "lf", "preserve");
                    break;
                case "pagesContiguous":
                    options.PagesContiguous = Choose(key, value, "false", "true");
                    sawPagesContiguous = true;
                    break;
                case "segmentation":
                    options.Segmentation = value switch
                    {
                        "whole" => Segmentation.Whole,
                        "blocks" => Segmentation.Blocks,
                        "headings" => Segmentation.Headings,
                        _ => throw Refusal.Of(
                            $"Parameter 'segmentation' is '{value}'; expected 'whole', 'blocks' or 'headings'."),
                    };
                    break;
                case "headingPattern":
                    options.HeadingPattern = Compile(key, value);
                    break;
                case "pageMarker":
                    options.PageMarker = Compile(key, value);
                    break;
                default:
                    throw Refusal.Of($"Unknown parameter '{key}'. The text adapter accepts {KnownKeys}.");
            }
        }

        if (options.HeadingPattern is { } heading)
        {
            if (options.Segmentation != Segmentation.Headings)
            {
                throw Refusal.Of(
                    "Parameter 'headingPattern' is only meaningful with segmentation 'headings'; "
                    + "here it would be recorded without shaping the output.");
            }

            string[] names = heading.GetGroupNames();
            if (!names.Contains("id", StringComparer.Ordinal))
            {
                throw Refusal.Of("Parameter 'headingPattern' has no named group 'id' to give each segment its id.");
            }

            options.HeadingHasLocatorGroup = names.Contains("locator", StringComparer.Ordinal);
        }
        else if (options.Segmentation == Segmentation.Headings)
        {
            throw Refusal.Of("Segmentation 'headings' requires parameter 'headingPattern'.");
        }

        if (options.PageMarker is { } marker)
        {
            // Group 0 is the whole match; exactly one more means exactly one capturing group,
            // named or not.
            int[] numbers = marker.GetGroupNumbers();
            if (numbers.Length != 2)
            {
                throw Refusal.Of(
                    $"Parameter 'pageMarker' has {numbers.Length - 1} capturing groups; it needs exactly one, "
                    + "holding the page number.");
            }

            options.PageMarkerGroup = numbers[1];
        }
        else if (sawPagesContiguous)
        {
            throw Refusal.Of("Parameter 'pagesContiguous' requires parameter 'pageMarker'.");
        }

        return options;
    }

    // Returns true for the second (non-default) value.
    private static bool Choose(string key, string value, string whenFalse, string whenTrue)
    {
        if (string.Equals(value, whenFalse, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(value, whenTrue, StringComparison.Ordinal))
        {
            return true;
        }

        throw Refusal.Of($"Parameter '{key}' is '{value}'; expected '{whenFalse}' or '{whenTrue}'.");
    }

    private static Regex Compile(string key, string pattern)
    {
        try
        {
            return new Regex(pattern, PatternOptions);
        }
        catch (NotSupportedException e)
        {
            // Backreferences, lookarounds, atomic and conditional groups: constructs that would
            // make matching time depend on the input in ways a linear-time matcher cannot bound.
            throw Refusal.Of(
                $"Parameter '{key}' uses a construct that linear-time (NonBacktracking) matching does not support: {e.Message}",
                e);
        }
        catch (ArgumentException e)
        {
            throw Refusal.Of($"Parameter '{key}' is not a valid regular expression: {e.Message}", e);
        }
    }
}
