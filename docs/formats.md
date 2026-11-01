# File format notes

Parsing lives in [wow-alchemy](https://github.com/davidrios/wow-alchemy) (see `decisions/0002-rust-parsers.md`). These notes cover what this project needs to know when using each format with the 3.3.5a client.

## MPQ

`ClientArchives` sets the load order, and later archives override earlier ones:

1. `Data/`: `common`, `common-2`, `expansion`, `lichking`
2. `Data/<locale>/`: `locale`, `speech`, `expansion-locale`, `lichking-locale`, `expansion-speech`, `lichking-speech`
3. `Data/`: `patch`, then `patch-*` in name order
4. `Data/<locale>/`: `patch-<locale>`, then `patch-<locale>-*` in name order

Gotchas:

- `base-<locale>.MPQ` and `backup-<locale>.MPQ` are left out on purpose. They aren't game data archives in this order.
- Paths ignore case and treat `/` and `\` alike, but listed names keep whatever case the archive has.
- Opening every archive takes a few seconds on this client, so open one source and share it.
- Reads on one source run one at a time, behind a lock in the FFI. That's fine for M1, but check it against M2's streaming load.

## BLP

`BlpDecoder` decodes one mipmap level to RGBA8. Texture streaming may later want to upload DXT data directly, which needs a new FFI call.

## DBC

- Fields are 32 bits. `DbcFile` reads them untyped, and the record classes give them meaning.
- `Map` and `AreaTable` have 66 and 36 fields on build 12340, checked against the client by tests.
- Localized strings take 16 slots plus a flags field. A client only fills in its own slot, and enGB uses the enUS slot, so typed records need the source's locale.
- String offsets can point into the middle of a string, so the FFI reads the string block directly rather than using `WdbFile::get_string_by_offset`, which only finds whole strings.
