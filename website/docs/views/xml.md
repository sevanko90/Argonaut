# XML

XML of any size opens as a collapsible tree, indexed in the background so even a multi-gigabyte
file opens at once.

- **The path bar** shows the XPath of the selected node, spelled with the document's own
  namespace prefixes. **Go to** (<kbd>Ctrl</kbd>+<kbd>L</kbd> / <kbd>Cmd</kbd>+<kbd>L</kbd>) takes
  an XPath and jumps there.
- **Copy** a text node's value, or its XPath.
- **Search** the whole document, with optional indent guides to follow the nesting.
- Whitespace between elements is hidden, so the tree shows the content rather than the
  indentation.

The file is read as UTF-8. A file that declares a different encoding opens with a note saying so.
