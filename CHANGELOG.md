# Changelog

## 1.4.1 - 2026-05-25

### Fixed
- Fixed a UI freeze when opening or pasting large markdown documents with long pipe tables.
- Moved markdown preview rendering off the Avalonia UI thread so expensive documents do not block the app shell.
- Avoided preview line-number decoration inside large table internals, which could make table-heavy files sluggish.
- Replaced whole-document fenced-code preprocessing with a linear scanner to keep large documents predictable.
