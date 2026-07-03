# World Terrain Tool

External C# content pipeline tool for generating chunked world terrain data for the Godot runtime.

The first generator creates a large island-style world with:

- a main island landmass
- smaller surrounding islands
- a large mountain range crossing the centre
- a secondary mountain range and deterministic random mountain peaks
- terrain-following river channels that form lakes in basins, cross to the outlet side, and continue downstream
- sheltered harbor metadata
- broad biome classification per chunk

Example:

```bash
dotnet run --project terrain_tool -- --output world_data/prototype --chunks-x 5 --chunks-y 5 --seed 12345
```

The default resolution is `257 x 257` height samples per chunk. Use `--samples <count>` to override it.

For the target full map:

```bash
dotnet run --project terrain_tool -- --output world_data/full --chunks-x 100 --chunks-y 100 --seed 12345
```

Output format:

- `world.json`: readable manifest and metadata
- `chunks/chunk_x000_y000.height`: raw little-endian unsigned 16-bit height samples
- `chunks/chunk_x000_y000.biome`: raw unsigned 8-bit biome palette indices, one per height sample

`world.json` also includes harbor, river polyline, and lake basin metadata in world meters for later gameplay and rendering systems.

