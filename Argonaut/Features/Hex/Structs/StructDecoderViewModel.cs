using System;
using System.Collections.Generic;
using System.Linq;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex.Values;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Features.Hex.Structs;

/// <summary>Which language a definition is read as: told from the text, or chosen.</summary>
public enum StructLanguageChoice
{
    Detect,
    C,
    CSharp,
}

/// <summary>
/// The side panel's Structures and Struct tabs: the pasted definition and what it declares, the
/// struct picked from it, and the tree of that struct read from the document.
///
/// The struct follows the caret, as the Values tab does: it is read from the selection start (the
/// caret when nothing is selected), and a selection at least two structs long reads as an array of
/// them. Pinned, it stays where it is while the caret moves. A move changes only where the tree
/// reads from (<see cref="Moved"/>); the tree is made again (<see cref="Tree"/> changes) only when
/// its rows change - another struct, target, or array length.
///
/// Reads the hex document's source but never owns it: <see cref="Close"/>, before the document
/// releases it, tells a surface showing the tree to let go.
/// </summary>
public sealed class StructDecoderViewModel : ObservableObject
{
    private string definitionText = string.Empty;
    private StructLanguageChoice language;
    private StructDefinition? definition;
    private string? selectedType;
    private StructTarget target = StructTarget.ForThisMachine;
    private bool isPinned;
    private ByteOrder order;

    // Where the caret last put the mapping, and what it reads from.
    private IByteSource? bytes;
    private long caretStart;
    private long selectionLength;

    private LaidStruct? laid;
    private StructProblem? layoutProblem;
    private long start;
    private long count = 1;
    private StructTree? tree;

    // The field rows the dump marks: the one under the pointer, and - pinned - the one holding the
    // caret.
    private StructNode? hoveredField;
    private StructNode? caretField;

    // The bytes a row selected in the tree put in the dump's selection: the caret move that
    // follows is the tree's own, not one for the struct to follow.
    private ByteRange? ownSelection;

    public StructDecoderViewModel(ByteOrder order)
    {
        this.order = order;
    }

    /// <summary>The targets a C definition can be laid out for.</summary>
    public IReadOnlyList<StructTarget> Targets { get; } = [StructTarget.Lp64, StructTarget.Llp64];

    /// <summary>The language picker's choices, in <see cref="StructLanguageChoice"/>'s order.</summary>
    public IReadOnlyList<string> LanguageNames { get; } = ["Detect language", "C / C++", "C#"];

    /// <summary><see cref="Language"/> as the picker's index.</summary>
    public int SelectedLanguageIndex
    {
        get => (int)this.language;
        set
        {
            if (value >= 0 && value < LanguageNames.Count)
                Language = (StructLanguageChoice)value;
        }
    }

    /// <summary>The definition as pasted or typed; read again on every change.</summary>
    public string DefinitionText
    {
        get => this.definitionText;
        set
        {
            if (!SetField(ref this.definitionText, value ?? string.Empty))
                return;
            OnPropertyChanged(nameof(OffersSample));
            Parse();
        }
    }

    public StructLanguageChoice Language
    {
        get => this.language;
        set
        {
            if (!SetField(ref this.language, value))
                return;
            OnPropertyChanged(nameof(SelectedLanguageIndex));
            Parse();
        }
    }

    /// <summary>The language the definition was read as, for the Structures tab to say.</summary>
    public string LanguageText => this.definition is null
        ? string.Empty
        : (this.definition.Language == StructLanguage.CSharp ? "C#" : "C / C++") + (this.language == StructLanguageChoice.Detect ? " (detected)" : string.Empty);

    /// <summary>What could not be read, each with where: <c>3:14  Unions are …</c>.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>The structs the definition declares, by name.</summary>
    public IReadOnlyList<string> Types { get; private set; } = [];

    public bool HasDefinition => Types.Count > 0;

    public string? SelectedType
    {
        get => this.selectedType;
        set
        {
            if (SetField(ref this.selectedType, value))
                Lay();
        }
    }

    /// <summary>What C leaves to the platform. A C# definition is the same on every 64-bit
    /// target, so the choice only shows for C.</summary>
    public StructTarget Target
    {
        get => this.target;
        set
        {
            if (value is null || !SetField(ref this.target, value))
                return;
            OnPropertyChanged(nameof(TargetShortName));
            Lay();
        }
    }

    public bool ShowsTarget => this.definition?.Language == StructLanguage.C;

    /// <summary>The target as the selector's chip names it: <c>LP64</c>, <c>LLP64</c>.</summary>
    public string TargetShortName => this.target.Name.Split(' ')[0];

    /// <summary>Whether the struct stays where it is while the caret moves.</summary>
    public bool IsPinned
    {
        get => this.isPinned;
        set
        {
            if (!SetField(ref this.isPinned, value))
                return;
            if (value)
            {
                FindCaretField(this.caretStart);
                return;
            }

            this.caretField = null;
            OnPropertyChanged(nameof(HighlightedField));
            Map();
        }
    }

    /// <summary>The byte order the struct reads in - the Values tab's, set by the hex view.</summary>
    public ByteOrder Order
    {
        get => this.order;
        set
        {
            if (this.order == value)
                return;
            this.order = value;
            if (this.tree is not null)
            {
                this.tree.Order = value;
                Moved?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The tree of the mapped struct, or null when there is none to show.</summary>
    public StructTree? Tree
    {
        get => this.tree;
        private set
        {
            var previous = this.tree;
            if (!SetField(ref this.tree, value))
                return;
            previous?.Close();

            // Rows of the old tree name nothing in the new one.
            this.hoveredField = null;
            this.caretField = null;
            this.ownSelection = null;
            OnPropertyChanged(nameof(HighlightedField));
        }
    }

    /// <summary>Raised when the tree reads from elsewhere, or reads its bytes differently, but its
    /// rows are the same: a surface lays its rows out again and keeps its place.</summary>
    public event EventHandler? Moved;

    /// <summary>Raised when, pinned, the caret moves to another field: the tree selects
    /// <see cref="CaretField"/>.</summary>
    public event EventHandler? CaretFieldChanged;

    /// <summary>The bytes the struct is read from - one struct, or the whole array - for the dump
    /// to outline; null when nothing is mapped.</summary>
    public ByteRange? MappedRange => this.tree is { } shown ? new ByteRange(shown.Start, shown.Nodes.Size) : null;

    /// <summary>The field the dump marks inside the struct: the row under the pointer, else -
    /// pinned - the field holding the caret.</summary>
    public ByteRange? HighlightedField => (this.hoveredField ?? (this.isPinned ? this.caretField : null)) is { } node ? RangeOf(node) : null;

    /// <summary>Pinned, the deepest field holding the caret; null when the caret is outside the
    /// struct or nothing is pinned.</summary>
    public StructNode? CaretField => this.caretField;

    /// <summary>The row under the pointer in the tree, or null when it leaves.</summary>
    public void Hover(StructNode? node)
    {
        if (Nullable.Equals(node, this.hoveredField))
            return;
        this.hoveredField = node;
        OnPropertyChanged(nameof(HighlightedField));
    }

    /// <summary>
    /// A row selected in the tree: its bytes, for the dump to select. The caret move that follows
    /// is the tree's own, so the struct stays where it is rather than following it to the field.
    /// </summary>
    public ByteRange SelectField(in StructNode node)
    {
        var range = RangeOf(node);
        this.ownSelection = range;
        return range;
    }

    private ByteRange RangeOf(in StructNode node) => new((this.tree?.Start ?? 0) + node.Offset, Math.Max(1, node.Size));

    /// <summary>The tree's title: the struct mapped, or the array of them - <c>Sample</c>,
    /// <c>Sample[3]</c>.</summary>
    public string StructTitle => this.laid is not { } mapped ? string.Empty : this.count > 1 ? $"{mapped.Name}[{this.count}]" : mapped.Name;

    /// <summary>Its size, alignment and place: <c>268 bytes · align 4 · at 0x1F40</c>.</summary>
    public string StructDetail
    {
        get
        {
            if (this.laid is not { } mapped)
                return string.Empty;

            string size = $"{mapped.Size} byte{(mapped.Size == 1 ? string.Empty : "s")}";
            return $"{(this.count > 1 ? $"{this.count} × {size}" : size)} · align {mapped.Alignment} · at 0x{this.start:X}";
        }
    }

    public bool HasStruct => this.tree is not null;

    /// <summary>Why the picked struct cannot be shown, or what to do first; null when it shows.</summary>
    public string? Message => this.definition is null || Types.Count == 0
        ? "Paste a C, C++ or C# struct on the Structures tab to read the bytes at the caret as it."
        : this.layoutProblem is { } problem
            ? (problem.Position == default ? problem.Message : $"{problem.Position.Line}:{problem.Position.Column}  {problem.Message}")
            : null;

    public bool HasMessage => Message is not null;

    /// <summary>
    /// Where the caret is, and what it reads: the hex view calls this on every move. Unpinned, the
    /// struct follows; pinned, nothing moves.
    /// </summary>
    public void Follow(IByteSource? source, long caret, long selection)
    {
        this.bytes = source;
        bool own = this.ownSelection is { } chosen && chosen.Offset == caret && chosen.Length == selection && ReferenceEquals(source, this.tree?.Bytes);
        this.ownSelection = null;
        if (own)
            return;

        this.caretStart = caret;
        this.selectionLength = selection;
        if (!this.isPinned)
            Map();
        else
            FindCaretField(caret);
    }

    /// <summary>Pinned: the deepest field holding <paramref name="offset"/>, told to the tree when
    /// it changes.</summary>
    private void FindCaretField(long offset)
    {
        StructNode? found = null;
        if (this.tree is { } shown && offset >= shown.Start && offset < shown.Start + shown.Nodes.Size)
        {
            var nodes = shown.Nodes;
            var node = nodes.Root;
            while (nodes.ChildAtOffset(node, offset - shown.Start) is { } child)
                node = child;
            if (node.Kind != StructNodeKind.Root)
                found = node;
        }

        if (Nullable.Equals(found, this.caretField))
            return;
        this.caretField = found;
        OnPropertyChanged(nameof(HighlightedField));
        if (found is not null)
            CaretFieldChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The document grew: values past what had arrived may read now.</summary>
    public void Refresh()
    {
        if (this.tree is not null)
            Moved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Before the document releases its source: a surface showing the tree lets go.</summary>
    public void Close()
    {
        this.bytes = null;
        Tree = null;
    }

    private void NotifyStructShown()
    {
        OnPropertyChanged(nameof(MappedRange));
        OnPropertyChanged(nameof(StructTitle));
        OnPropertyChanged(nameof(StructDetail));
        OnPropertyChanged(nameof(HasStruct));
    }

    /// <summary>A new definition pasted in: tell its language from it again, whatever was chosen
    /// for the last one.</summary>
    public void DefinitionPasted() => Language = StructLanguageChoice.Detect;

    /// <summary>Whether the editor is empty, so the Structures tab offers the sample.</summary>
    public bool OffersSample => string.IsNullOrWhiteSpace(this.definitionText);

    /// <summary>Fills the editor with the sample in <paramref name="sampleLanguage"/>: a new
    /// definition, so its language is told from it, as a paste's is.</summary>
    public void InsertSample(StructLanguage sampleLanguage)
    {
        Language = StructLanguageChoice.Detect;
        DefinitionText = StructSamples.For(sampleLanguage);
    }

    private void Parse()
    {
        this.definition = string.IsNullOrWhiteSpace(this.definitionText)
            ? null
            : StructParser.Parse(this.definitionText, this.language switch
            {
                StructLanguageChoice.C => StructLanguage.C,
                StructLanguageChoice.CSharp => StructLanguage.CSharp,
                _ => null,
            });

        Problems = this.definition?.Problems.Select(p => $"{p.Position.Line}:{p.Position.Column}  {p.Message}").ToList() ?? [];
        Types = this.definition?.Declarations.Select(d => d.Name).Where(n => !n.StartsWith('<')).Distinct().ToList() ?? [];

        // Keep the struct picked while it is still declared; else the last declared - in a header
        // the outer struct usually comes after the ones it uses.
        if (this.selectedType is null || !Types.Contains(this.selectedType))
            this.selectedType = Types.Count > 0 ? Types[^1] : null;

        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(Types));
        OnPropertyChanged(nameof(HasDefinition));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(LanguageText));
        OnPropertyChanged(nameof(ShowsTarget));
        Lay();
    }

    private void Lay()
    {
        var result = this.definition is { } read && this.selectedType is { } name ? StructLayoutPass.Lay(read, name, this.target) : null;
        var previous = this.laid;
        this.laid = result?.Struct;
        this.layoutProblem = result?.Problem;
        if (!Equals(previous, this.laid))
            this.count = 0;
        Map();
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
    }

    /// <summary>Puts the struct where the caret says: a new tree when its rows change, else the
    /// same tree reading from the new start.</summary>
    private void Map()
    {
        if (this.laid is not { } mapped || this.bytes is not { } source)
        {
            Tree = null;
            NotifyStructShown();
            return;
        }

        long wanted = this.selectionLength >= 2 * mapped.Size ? this.selectionLength / mapped.Size : 1;
        this.start = this.caretStart;
        if (this.tree is { } shown && wanted == this.count && ReferenceEquals(shown.Nodes.Struct, mapped) && ReferenceEquals(shown.Bytes, source))
        {
            shown.Start = this.start;
            Moved?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            this.count = wanted;
            Tree = new StructTree(new StructNodes(mapped, wanted), source, this.start, this.order, this.target);
        }

        NotifyStructShown();
    }
}
