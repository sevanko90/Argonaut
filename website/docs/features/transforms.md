# Text transforms

The **Transform** menu in the [text view](../views/text.md) toolbar rewrites the whole document in
one step. Undo gives it back.

## Encode and decode

Encode or decode the document as a **JSON string**, **URL** encoding, **Base64** or **Base64 URL**.
Choose **Each line separately** to treat every line as its own value - a list of encoded IDs, say.

## Recipes

A recipe turns a list of lines into something else, and back again. Three come with Argonaut:

| Recipe | Turns the lines `apple` and `pear` into |
| --- | --- |
| SQL IN list | `('apple', 'pear')` - one value a line, quoted and escaped for SQL |
| JSON string array | `["apple", "pear"]` - one string a line, escaped for JSON |
| Comma-separated list | `apple, pear` |

**Reverse a recipe** takes the result apart again - paste a SQL `IN` list and get the bare values
back.

**Edit recipes…** opens the recipe editor, with a live preview of the current document. A recipe
says what each line becomes, what goes between items and before the first and after the last, how
values are escaped, and whether to trim spaces and skip blank lines. Change a bundled recipe and
it's saved as your own; delete yours and the bundled one comes back.

## Line endings

**Line endings** converts the whole document to LF or CRLF. The change is written when you save.
