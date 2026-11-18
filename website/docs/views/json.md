# JSON

Argonaut began as a viewer for huge JSON files - hence the name - and JSON gets the most help.

A file of any size opens as a collapsible tree at once, while the structure is indexed in the
background.

- **Expand and collapse** nodes, or set how deep the tree opens from the toolbar. Optional indent
  guides show the nesting.
- **The path bar** shows the JSONPath of the selected node. Use **Go to**
  (<kbd>Ctrl</kbd>+<kbd>L</kbd> / <kbd>Cmd</kbd>+<kbd>L</kbd>) to type a JSONPath and jump
  straight there.
- **Copy** a property's value or its JSONPath from its menu.
- **Long values** are shortened in the tree, with a link to see the whole thing in the text view.
- **[Value hints](../features/hints.md)** explain dates, colours, tokens and encoded payloads
  beside the values.

## Arrays as tables

An array of objects offers **view as table**: one row per element and one column
per property, so you can scan and compare records instead of expanding them one by one. Resize
columns, pan across wide ones, and go back to the tree when you're done.

## Documentation from JSON Schema

Bind a JSON Schema and each property's `title` and `description` appear in a gutter beside the
data. Argonaut picks a matching schema for you where it can. See
[JSON Schema](../features/schema.md).

## Comparing two files

Open two JSON files together to see what changed in the data, not just which lines did. See
[Semantic JSON diff](../features/diff.md).

## Searching

Search runs against the file as written, so quote property names: search for `"name":value`
rather than `name:value`. See [Search](../guide/search.md).
