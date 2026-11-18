# JSON Schema

Bind a JSON Schema to a document and Argonaut shows what the data *means*: each property's `title`
and `description` appear in a resizable gutter beside the tree, with the full text on hover.

This is documentation, not validation. Nothing is marked wrong; a property the schema doesn't
describe simply has no note.

## Choosing a schema

- **Argonaut matches one for you.** When a schema fits the document it's bound automatically, and
  the toolbar's schema list is ranked by how well each one fits. You can turn automatic matching
  off.
- **Or pick one** from the toolbar - a bundled schema, or one of your own. Your choice is remembered
  for that file.
- **Add your own** by dropping schema files into the schemas folder; **Open schema folder…** in the
  schema list takes you there.
- A file holding many schemas - `$defs`, or an OpenAPI document's components - offers a type
  picker, ranked by how well each type matches.

## What's understood

- Object properties, positional array slots (`prefixItems`) and labels for `enum` values.
- Local `$ref`, including recursive ones, `$defs` / `definitions`, and `allOf` / `oneOf` /
  `anyOf`.
- Not used: remote `$ref`, `patternProperties`, `if` / `then` / `else`.
