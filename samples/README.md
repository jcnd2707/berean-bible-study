# Sample library

A small, redistributable subset of a real Bible study library, built by
[tools/BuildSamples](tools/BuildSamples) so the app works out of the box without you sourcing your
own modules first. It covers the chapters the eval harness uses (Genesis 1–2, Isaiah 7, Daniel 8,
John 1 and 3, Romans 8 and 14, Galatians 3, Philippians 2, Colossians 2, Hebrews 9, Revelation 13
and 20), plus the dictionary entries, Strong's numbers and cross-references those chapters touch.

Point `BereanResources:RootPath` at this folder to try the app without your own library (see the
[root README's Configuration section](../README.md#configuration)).

## Demo perspective

`clarke` (Adam Clarke's Commentary) is already labelled `Wesleyan` in `ModuleProfiles`
(`BereanResource.Api/appsettings.json`), so it's ready to be a demo perspective — it's a demo, not
a claim that Clarke exhaustively represents Wesleyan thought. Wire it up by adding this to
`Berean.Agent.Api/appsettings.json` (see [Perspectives](../README.md#perspectives)):

```json
"Perspectives": [
  { "Id": "wesleyan", "Tradition": "Wesleyan", "Label": "Wesleyan", "CitationPrefix": "WES", "TopK": 4 }
]
```

## Sources and licenses

| File(s) | Source | License | Notes |
|---|---|---|---|
| `Bibles/BSB.db` | [Berean Bible](https://berean.bible) | CC0 | Public domain dedication |
| `Bibles/ASV.db` | American Standard Version (1901) | Public Domain | |
| `Bibles/YLT.db` | Young's Literal Translation (1898) | Public Domain | |
| `Bibles/SpaRV.db` | Reina-Valera (1909) | Public Domain | Spanish |
| `Bibles/akjvstrong.bbl` | American King James Version + Strong's numbers | Public domain translation; Strong's numbers added by RevSteve, released under the translator's public-domain dedication | Powers the Strong's number demo |
| `Commentaries/barnes.cmt` | Albert Barnes' Notes on the Bible (1798–1870) | Public Domain | |
| `Commentaries/clarke.cmt` | Adam Clarke's Commentary (1760s–1832) | Public Domain | Labelled `Wesleyan` (see Demo perspective) |
| `Commentaries/henry.cmt` | Matthew Henry's Commentary (1662–1714) | Public Domain | |
| `Commentaries/jfb.cmt` | Jamieson-Fausset-Brown Commentary (19th c.) | Public Domain | |
| `Dictionaries/eastons.dct` | Easton's Bible Dictionary (1897) | Public Domain | |
| `Dictionaries/smiths.dct` | Smith's Bible Dictionary (1863) | Public Domain | |
| `Dictionaries/webster.dct` | Webster's 1828 Dictionary | Public Domain | |
| `Dictionaries/strong.dct` | Strong's Dictionary / Thayer's / BDB references | Public-domain texts; formatting by Timothy S. Morton (Bible Analyzer) and the MySword team | |
| `Dictionaries/bdb.dct` | Brown-Driver-Briggs Hebrew Lexicon | Public-domain text; formatting by Timothy S. Morton (Bible Analyzer) and the MySword team | |
| `cross_references/cross_references_0.db` | [OpenBible.info Cross References](https://www.openbible.info/labs/cross-references/) | CC BY 4.0 | Attribution: openbible.info |

All module files above are in the public domain or otherwise redistributable per their own
license; formatting/compilation credit for the MySword-format files (`.dct`, `.cmt`, `.bbl`) goes
to the MySword project and the named compilers. The code in the rest of this repository is
[MIT licensed](../LICENSE) — see [License & content](../README.md#license--content).

## Rebuilding this folder

```bash
dotnet run --project samples/tools/BuildSamples -- --source "<your bible-docs root>" --output samples
```

Copies the needed files from your library to a scratch temp directory first (never reads or
writes your live library in place beyond that copy), then extracts the chapters and cross-linked
entries listed above into this folder.
