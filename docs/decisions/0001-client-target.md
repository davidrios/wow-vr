# 0001: Client target is 3.3.5a with AzerothCore

**Status:** accepted (2026-11-01)

## Decision

Read game data from the original Wrath of the Lich King 3.3.5a client (build 12340), and take spawn data from the AzerothCore world database.

## Consequences

- `MpqAssetSource` is the only `IAssetSource` implementation. CASC is out of scope.
- Parsers target the 3.3.5a formats: monolithic ADT with `MH2O`, M2 with external `.skin` files, WMO, BLP2, DBC.
- AzerothCore targets the same client, so its display IDs need no remapping against the client's DBCs.
- The world covers Outland and Northrend as well as Eastern Kingdoms and Kalimdor.
- Blizzard no longer distributes this client. Players supply their own copy, and the project never provides or links to one.
