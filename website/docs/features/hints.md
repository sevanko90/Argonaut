# Value hints

Plenty of values in data files are unreadable as written. Argonaut reads them for you and says what
they are, right beside the value:

| Value | The hint |
| --- | --- |
| **Dates** - epoch numbers in milliseconds, seconds or Keepa minutes, and ISO 8601 strings | The date, and how long ago or how far off it is. Argonaut works out which epoch unit the document uses, and you can change it. |
| **Colours** - `#1E90FF`, `rgb(...)` | A swatch, and the colour in the other notation. |
| **JWTs** | The algorithm, and when the token expires or how long ago it did. |
| **Links** - web and mail addresses | Open with a click, or <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+click on the value. |
| **Cron schedules** | The schedule in words, and when it next runs. |
| **JSON inside a string** | Shown as a tree. |
| **Base64** | Named for what it decodes to: text, a PNG, GIF, JPEG or WebP image (with its size), PDF, ZIP or gzip. |

A hint ending in a chevron opens up: a JWT shows its header and claims, embedded JSON shows as a
tree, a Base64 picture shows the picture - and the rest can be opened as a document of their own.

Hints go by what a value **looks like**, never by the name of its property, so they work on any
file. In the [text view](../views/text.md) they're stricter outside the values the syntax colouring
finds - a number in a log is just a number - and show as an underline that reveals the hint when
you hover it.
