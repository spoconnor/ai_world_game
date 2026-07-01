# World Terrain Tool

External C# content pipeline tool for generating chunked world terrain data for the Godot runtime.

The first generator creates a large island-style world with:

- a main island landmass
- smaller surrounding islands
- a large mountain range crossing the centre
- sheltered harbor metadata
- broad biome classification per chunk

Example:

```bash
dotnet run --project terrain_tool -- --output world_data/prototype --chunks-x 5 --chunks-y 5 --seed 12345
```

For the target full map:

```bash
dotnet run --project terrain_tool -- --output world_data/full --chunks-x 100 --chunks-y 100 --seed 12345
```

Output format:

- `world.json`: readable manifest and metadata
- `chunks/chunk_x000_y000.height`: raw little-endian unsigned 16-bit height samples

