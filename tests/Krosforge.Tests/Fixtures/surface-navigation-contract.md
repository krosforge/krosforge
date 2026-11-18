# Surface navigation contract — reference build 3.7.14.18

The supported surface is the connected set of 50 maps with natural border exits in world geometry bundle 587, starting at map 153878787. The versioned allowlist records each map's world coordinates. All 50 belong to world map 2. Indoor maps, underground/tutorial instances, interactive doors and other worlds are not admitted by this allowlist. The `outdoor` metadata bit alone is insufficient: this reference build also sets it on internal maps.

Sources are read from the user's installation, never distributed:

- `mapdata_assets_world_587.bundle`, SHA-256 `6806b94ebbfc904e0ba3f906942593819c49cf9db068ceeba1dd693d714f89ee`.
- `data_assets_mapsinformationdataroot.asset.bundle`, SHA-256 `39860ebf8f98f915a5865f3719cd17b739691da5cf57e2a8793365d9fa83b8da`.
- `GameAssembly.dll`, SHA-256 `af866f65f0d08eebe9008ffda54462e2814d994c81059fffd766f0d685503ab3`.

An independent Python reader of the embedded Unity schemas was used to enumerate neighbours, 560-cell geometry, exit flags and world coordinates. That inventory has 156 directed map connections and 2,481 source-cell/destination-map pairs. A separate traversal from the initial cell reaches every exit and 16,019 surface cells. These counts include diagonal exits; they do not include flags at corners whose step exits through a different side.

## Corresponding arrivals

This is a reconstructed local boundary policy, not a recovered official server script. On the staggered grid (14 columns, 40 rows), perform one step in an exit direction, then wrap only the crossed axis to the neighbouring map. The target must be walkable and advertise the reciprocal direction. Prefer the straight step when available; otherwise use a permitted adjacent diagonal, ordered counterclockwise then clockwise. Never search for an arbitrary nearby walkable cell. Missing correspondence fails catalog loading.

For example:

| Exit | Side | Direction | Arrival |
| --- | --- | --- | --- |
| 349 | right | east (0) | 336 |
| 335 | right | southeast (1) | 336 |
| 363 | right | east (0) | 350 |
| 448 | left | northwest (5) | 447 |
| 3 | top | north (6) | 535 |
| 17 | top | north (6) | 549 |
| 2 | top | northeast (7) | 548 |
| 546 | bottom | southeast (1) | 1 |

These vectors were derived separately from staggered row/column coordinates, including reverse steps. Cell 27's northeast step crosses the right edge, not the top edge. Cell 546's southwest step crosses the bottom edge, not the left edge.

The independent extraction's complete correspondence digest is `5f12eac74ef85f16cbd081eab43adabfd72a6253500ad128e85747f9ec91ef7b`: SHA-256 of UTF-8 lines `sourceMap,destinationMap,exitCell,arrivalCell,direction\n`, sorted numerically by source map, destination map and exit cell. The integration test compares the production catalog to this digest, rather than generating its expected pairs with `SurfaceTopology`.

## Exploration zones and height

`ClientCellData.GetLinkedRpZone` at RVA `0x682f30` reads the field at offset `0x20`, shifts it right by 4 and masks with 15. Thus packed values 16 and 17 both designate exploration zone 1; their low nibble describes a different, combat zone. `faw.ivr` at RVA `0x1a259e0` checks that the target is movable and compares the extracted exploration zones. `eza.bbnj` / `ezc.bbnq` select navigation data through `ezc.bbnt` using that exploration zone. These static anchors were checked in the pinned binary; they are distinct from native UI acceptance.

Movement validates every intermediate walkable cell against the same exploration zone. Height changes inside a declared zone are permitted; a zero/unknown zone does not authorize an altitude change. Different exploration zones remain disconnected, including the isolated zone in map 154010373.

## Verification scope

Synthetic tests cover straight and diagonal border steps in all four directions, reverse correspondence, corners, exclusions, incompatible neighbours and missing arrivals. Installed-reference tests check the complete correspondence digest, walkable connectivity and every directed map connection over TCP, including appearance position fields, persistence, reconnect and a second account remaining on the initial map. The real installation is required for those tests and is mounted read-only; public CI skips them explicitly. Native visual acceptance is recorded separately in the PR.
