# Pathstone Documentation

This directory contains the configuration for generating API documentation
using [docfx](https://dotnet.github.io/docfx/).

## Building the docs

```bash
# Install docfx as a global tool (one-time)
dotnet tool install -g docfx

# Generate + serve locally
docfx docfx.json --serve

# Or just generate (output in _site/)
docfx docfx.json
```

## Structure

- `docfx.json` — docfx configuration (at repo root)
- `README.md` — the main landing page (docfx picks this up as index.html)
- `api/` — auto-generated API reference (from XML doc comments)
- `CHANGELOG.md` — included as a content page

## Hosting on GitHub Pages

1. Run `docfx docfx.json` to generate `_site/`.
2. Copy `_site/` contents to a `gh-pages` branch.
3. Enable GitHub Pages in repo settings → Pages → Source: `gh-pages` branch.
