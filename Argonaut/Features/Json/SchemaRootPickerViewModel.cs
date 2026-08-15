using System;
using System.Collections.Generic;
using System.ComponentModel;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// One row of the schema-type picker: either a section header or a selectable type. Headers ride
/// in the same list rather than in a separate control so the whole thing is one scrollable,
/// filterable surface with one selection.
/// </summary>
public sealed class SchemaRootPick
{
    private SchemaRootPick(string name, bool isHeader, string? description, string? detail, string? score, bool showSeparator, bool isRecommended)
    {
        Name = name;
        IsHeader = isHeader;
        Description = description;
        Detail = detail;
        Score = score;
        ShowSeparator = showSeparator;
        IsRecommended = isRecommended;
    }

    /// <summary>The schema type's name, or the header's caption.</summary>
    public string Name { get; }

    public bool IsHeader { get; }

    public bool IsSelectable => !IsHeader;

    /// <summary>The type's <c>description</c>, the payload that makes a list of opaque type names
    /// scannable. Null when the schema doesn't document it.</summary>
    public string? Description { get; }

    /// <summary>Property count, e.g. "5 fields" - the cheap way to tell a two-field envelope from
    /// the twenty-field payload it wraps.</summary>
    public string? Detail { get; }

    /// <summary>Match strength as a percentage, or null for an unscored entry.</summary>
    public string? Score { get; }

    public bool HasScore => Score is not null;

    /// <summary>Draws a rule above this row. Set on a section header that follows other content,
    /// so the shortlist reads as a distinct section rather than as the top of one long list.</summary>
    public bool ShowSeparator { get; }

    /// <summary>The one candidate that clearly fits this document, if there is one. Marked so a
    /// schema whose *own root* is the answer says so, rather than leaving the user to infer it
    /// from an absence.</summary>
    public bool IsRecommended { get; }

    public static SchemaRootPick Header(string caption, bool showSeparator = false)
        => new(caption, isHeader: true, null, null, null, showSeparator, isRecommended: false);

    public static SchemaRootPick Type(string name, string? description, int propertyCount, double? coverage, bool isRecommended = false)
        => new(
            name,
            isHeader: false,
            description,
            propertyCount == 1 ? "1 field" : $"{propertyCount} fields",
            coverage is { } value ? value.ToString("P0", System.Globalization.CultureInfo.CurrentCulture) : null,
            showSeparator: false,
            isRecommended);
}

/// <summary>
/// Drives the schema-type picker flyout: a filter box over a two-section list - the types that
/// plausibly match the open document, then every type the schema offers.
///
/// This exists because a flat list cannot answer the question the user actually has. Picking a
/// type from an OpenAPI document means choosing one of a hundred-odd opaque names, and neither
/// alphabetical order nor type-to-filter helps someone who doesn't yet know what their document
/// is. The answer lives in the document, so the list is ranked by
/// <see cref="JsonSchemaRootMatcher"/> against the document's own property names, and the likely
/// answers are lifted to the top.
///
/// Owned by <see cref="JsonToolbarViewModel"/> and shares its lifetime, so - like the toolbar
/// itself - it never unsubscribes.
/// </summary>
public sealed class SchemaRootPickerViewModel : ObservableObject
{
    /// <summary>
    /// Caption of the plausible-types section when no single candidate won - they are all merely
    /// possible, and the user has to choose. See <see cref="OtherMatchesHeader"/> for the case
    /// where one did win.
    /// </summary>
    public const string MatchesHeader = "Likely matches for this document";

    /// <summary>Caption of that section once a best match has been lifted out of it: what remains
    /// are the also-rans, and heading them "likely" alongside the winner reads as though a 78%
    /// and a 100% were equally good bets.</summary>
    public const string OtherMatchesHeader = "Other likely matches for this document";

    /// <summary>Caption of the section holding everything the schema offers.</summary>
    public const string AllTypesHeader = "All types";

    /// <summary>
    /// Shown in place of the matches section when the document gave nothing to go on. Worded as
    /// a prompt rather than a failure because a type is bound regardless - the binding is just a
    /// fallback rather than something recognised, and the user should know which it is.
    /// </summary>
    public const string NoMatchHeader = "No type matched this document — pick one";

    /// <summary>
    /// Label of the entry that binds the schema's own root - offered only when that root is
    /// usable, since it is a real schema like any other type in the list.
    ///
    /// There is deliberately no "no type" counterpart. Binding the wrong type cannot error by
    /// design (an unmatched key just gets no hint), so an inert placeholder would only be
    /// something to clear before the schema did anything; "No schema" in the schema list is
    /// already how you see nothing.
    /// </summary>
    public const string DocumentRootLabel = "Whole document";

    /// <summary>How many scored candidates the matches section shows at most. Beyond a handful it
    /// stops being a shortlist and becomes the full list again.</summary>
    private const int MaxMatchesShown = 5;

    private readonly JsonSchemaSettings schemaSettings;

    /// <summary>Raised when the user has actually chosen a type, so the owning flyout can close.
    /// A callback rather than an event: the owner supplies it at construction and outlives this,
    /// so there is nothing to unsubscribe.</summary>
    private readonly Action? closeRequested;

    private IReadOnlyList<SchemaRootPick> picks = Array.Empty<SchemaRootPick>();
    private string filter = string.Empty;

    public SchemaRootPickerViewModel(JsonSchemaSettings schemaSettings, Action? closeRequested = null)
    {
        this.schemaSettings = schemaSettings;
        this.closeRequested = closeRequested;
        schemaSettings.PropertyChanged += OnSchemaSettingsPropertyChanged;
        Rebuild();
    }

    /// <summary>The filtered, sectioned list bound to the flyout's ListBox.</summary>
    public IReadOnlyList<SchemaRootPick> Picks
    {
        get => picks;
        private set => SetField(ref picks, value);
    }

    /// <summary>Whether the picker applies to the bound schema at all - false for an ordinary
    /// single-schema file, which hides the button entirely.</summary>
    public bool IsApplicable => schemaSettings.RootOptions.Count > 0;

    /// <summary>
    /// What the picker button reads when closed. A root is always bound once a multi-root schema
    /// is selected (see JsonSchemaSettings' defaulting), so the fallback is only ever reached in
    /// the instant before that lands.
    /// </summary>
    public string ButtonText
        => schemaSettings.SelectedRootName
            ?? (schemaSettings.DocumentRootIsUsable ? DocumentRootLabel : "Schema type\u2026");

    /// <summary>
    /// Explains what the matches were computed against, so a match on an array's elements can't
    /// be misread as a match on the document itself. Null when there's nothing to say.
    /// </summary>
    public string? MatchContextText
        => schemaSettings.MatchesDescribeArrayElements && schemaSettings.RootMatches.Count > 0
            ? "Matched against the first element of the array."
            : null;

    public bool HasMatchContext => MatchContextText is not null;

    /// <summary>Bound two-way to the flyout's filter box.</summary>
    public string Filter
    {
        get => filter;
        set
        {
            if (SetField(ref filter, value ?? string.Empty))
                Rebuild();
        }
    }

    /// <summary>
    /// Bound two-way to the list's SelectedItem. Nulls and header rows are ignored: the ListBox
    /// nulls its selection as the filtered list is swapped, and acting on that would unbind the
    /// type the user just chose.
    ///
    /// Acting on the pick is deferred a dispatcher turn: binding a root rebuilds
    /// <see cref="Picks"/> and closes the flyout, both of which pull the list out from under the
    /// selection commit this setter is running inside. See <see cref="UiDeferral"/>.
    /// </summary>
    public SchemaRootPick? SelectedPick
    {
        get => null;
        set
        {
            if (value is null || value.IsHeader)
                return;

            // The "whole document" row binds the schema's own root, which is the null name.
            string? rootName = value.Name == DocumentRootLabel ? null : value.Name;

            UiDeferral.AfterCurrentInput(() =>
            {
                schemaSettings.SelectRoot(rootName);
                OnPropertyChanged(nameof(SelectedPick));

                // Choosing a type is the terminal action of the flyout - nothing follows it.
                closeRequested?.Invoke();
            });
        }
    }

    private void OnSchemaSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null
            or nameof(JsonSchemaSettings.RootOptions)
            or nameof(JsonSchemaSettings.SelectedRootName)
            or nameof(JsonSchemaSettings.RootMatches)
            or nameof(JsonSchemaSettings.MatchesDescribeArrayElements)))
            return;

        OnPropertyChanged(nameof(IsApplicable));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(MatchContextText));
        OnPropertyChanged(nameof(HasMatchContext));
        Rebuild();
    }

    private void Rebuild()
    {
        var options = schemaSettings.RootOptions;
        if (options.Count == 0)
        {
            Picks = Array.Empty<SchemaRootPick>();
            return;
        }

        var schema = schemaSettings.Document;
        var matches = schemaSettings.RootMatches;
        var list = new List<SchemaRootPick>(options.Count + 3);

        // The schema's own root is scored like any named type (a schema that discriminates
        // internally is matched by its root and by nothing else), so the entry that binds it
        // carries that score rather than sitting above the list looking unevaluated.
        var documentRootMatch = FindMatch(matches, name: null);
        string? recommended = JsonSchemaRootMatcher.Best(matches) is { } best ? best.Name ?? DocumentRootLabel : null;

        // Offered only when it's a real schema, and never filtered away - it has to stay
        // reachable so a type bound by mistake can be undone without first clearing the filter.
        if (schemaSettings.DocumentRootIsUsable)
        {
            list.Add(SchemaRootPick.Type(
                DocumentRootLabel,
                "Label the file against the schema's own root.",
                propertyCount: schema?.GetPropertyCount(schema.DocumentRootId) ?? 0,
                coverage: documentRootMatch?.Coverage,
                isRecommended: recommended == DocumentRootLabel));
        }

        var shortlisted = new HashSet<string>(StringComparer.Ordinal);

        if (matches.Count > 0)
        {
            var bestMatch = JsonSchemaRootMatcher.Best(matches);

            // The winner is lifted out of the section below so that section can honestly be
            // headed "other": a runner-up scoring less than the best has no business sharing a
            // heading with it. (A winning *document root* is already pinned above, scored.)
            if (bestMatch is { Name: { } bestName } && Matches(bestName))
            {
                shortlisted.Add(bestName);
                list.Add(SchemaRootPick.Type(
                    bestName,
                    Describe(schema, bestMatch.Value.NodeId),
                    bestMatch.Value.SchemaKeys,
                    bestMatch.Value.Coverage,
                    isRecommended: true));
            }

            var section = new List<SchemaRootPick>(MaxMatchesShown);
            foreach (var match in matches)
            {
                if (section.Count >= MaxMatchesShown)
                    break;
                if (!JsonSchemaRootMatcher.IsPlausible(match))
                    break;

                // The document root is shown pinned above, and the best match just above that.
                if (match.Name is not { } name || !Matches(name) || !shortlisted.Add(name))
                    continue;

                section.Add(SchemaRootPick.Type(name, Describe(schema, match.NodeId), match.SchemaKeys, match.Coverage, isRecommended: false));
            }

            bool documentRootFits = documentRootMatch is { } root && JsonSchemaRootMatcher.IsPlausible(root);

            if (section.Count > 0)
            {
                // With no clear winner these are simply the candidates; with one they are the
                // also-rans, and saying so is what stops a 78% sitting under the same heading as
                // a 100% as though the two were equally likely.
                list.Add(SchemaRootPick.Header(recommended is null ? MatchesHeader : OtherMatchesHeader));
                list.AddRange(section);
            }
            else if (filter.Length == 0 && bestMatch is null && !documentRootFits)
            {
                // Scores exist but nothing cleared the bar - say so rather than silently showing
                // an unexplained alphabetical list. Never said when something *was* recognised,
                // whether that was the schema's own root or a named type.
                list.Add(SchemaRootPick.Header(NoMatchHeader));
            }
        }

        var rest = new List<SchemaRootPick>(options.Count);
        foreach (var option in options)
        {
            if (shortlisted.Contains(option.Name) || !Matches(option.Name))
                continue;

            rest.Add(SchemaRootPick.Type(option.Name, Describe(schema, option.NodeId), schema?.GetPropertyCount(option.NodeId) ?? 0, coverage: null));
        }

        if (rest.Count > 0)
        {
            // Ruled off from the shortlist above it - the two sections answer different
            // questions ("probably this" vs "everything there is") and shouldn't read as one list.
            list.Add(SchemaRootPick.Header($"{AllTypesHeader} ({options.Count})", showSeparator: true));
            list.AddRange(rest);
        }

        Picks = list;
    }

    private static SchemaRootMatch? FindMatch(IReadOnlyList<SchemaRootMatch> matches, string? name)
    {
        foreach (var match in matches)
        {
            if (string.Equals(match.Name, name, StringComparison.Ordinal))
                return match;
        }

        return null;
    }

    private static string? Describe(JsonSchemaDocument? schema, int nodeId)
    {
        string? description = schema?.GetDescription(nodeId);
        if (description is null)
            return null;

        // Descriptions run to paragraphs; the list needs a first line, not an essay.
        int cut = description.IndexOfAny(new[] { '\r', '\n' });
        if (cut >= 0)
            description = description[..cut];

        return description.Length > 120 ? description[..119] + "…" : description;
    }

    private bool Matches(string name)
        => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
