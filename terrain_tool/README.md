# World Terrain Tool

External C# content pipeline tool for generating chunked world terrain data for the Godot runtime.

The first generator creates a large island-style world with:

- a main island landmass
- smaller surrounding islands
- deterministic tectonic plates with convergent, divergent, and transform boundary shaping
- a large mountain range crossing the centre
- a secondary mountain range and deterministic random mountain peaks
- volcanic cones, craters, lava aprons, and island-arc style volcanic uplift
- a hydraulic erosion pass with rainfall, downhill water flow, erosion, sediment transport, deposition, and soil depth
- a glacial carving pass that cuts cold highland glacier paths into broader U-shaped valleys
- sheltered harbor metadata
- broad biome classification per chunk

Example:

```bash
dotnet run --project terrain_tool -- --chunks-x 5 --chunks-y 5 --seed 12345
```

By default, output is written to the repository-level `world_data/prototype` directory used by the Godot world viewer.
The default resolution is `257 x 257` height samples per chunk. Use `--samples <count>` to override it.
The default erosion pass runs `96` iterations. Use `--erosion-iterations <count>` to tune it, or `--erosion-iterations 0` to export the uneroded base terrain.
The current erosion implementation runs on one global in-memory heightfield and is intended for prototype-sized worlds; larger worlds will need tiled erosion with padded borders.

For the target full map:

```bash
dotnet run --project terrain_tool -- --output world_data/full --chunks-x 100 --chunks-y 100 --seed 12345
```

Output format:

- `world.json`: readable manifest and metadata
- `chunks/chunk_x000_y000.height`: raw little-endian unsigned 16-bit height samples
- `chunks/chunk_x000_y000.biome`: raw unsigned 8-bit biome palette indices, one per height sample
- `chunks/chunk_x000_y000.flow`: raw little-endian 32-bit float normalized flow accumulation values
- `chunks/chunk_x000_y000.soil`: raw little-endian 32-bit float soil depth values in meters
- `chunks/chunk_x000_y000.uplift`: raw little-endian 32-bit float normalized tectonic uplift mask
- `chunks/chunk_x000_y000.fault`: raw little-endian 32-bit float normalized fault/plate-boundary mask
- `chunks/chunk_x000_y000.volcanic`: raw little-endian 32-bit float normalized volcanic activity mask
- `chunks/chunk_x000_y000.glacier`: raw little-endian 32-bit float normalized glacial carving mask

`world.json` also includes harbor metadata in world meters for later gameplay and rendering systems.
