# Semantic JSON diff

Ordinary diff tools compare lines. Argonaut compares **data**: it understands that JSON structure
carries meaning, and shows what actually changed in it.

So moving a property from the top of an object to the bottom isn't a difference - the object it
describes is the same. Reformatting a file isn't a difference either. What you see is values added,
removed and changed, and properties that moved, with where they went or came from.

## Opening a diff

With a JSON file open, choose **Compare with another JSON file…** from the toolbar. Or give two
files on the [command line](../guide/command-line.md):

```
Argonaut before.json after.json
```

## Reading it

The two documents are shown side by side as trees, each row coloured by what the comparison found,
and the containers on the way to a change are marked so you can follow it down.

- **Changes only** hides the rows that didn't change.
- **Previous** and **next difference** step through the changes.
- Optional indent guides show the nesting.

Both sides follow the shape of the first document, so the second side's layout may not match its
file line for line. To find exactly where something is in the second file, use its JSONPath in the
path bar.

[Search](../guide/search.md) covers both files; next and previous move between the rows that
match.
