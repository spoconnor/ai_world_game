# World Landscape Viewer

Godot 4.7 project for loading generated world data from `../world_data`.

The main scene currently opens the voxel world editor prototype. It builds a streamable voxel view over the generated terrain manifest when available, falls back to procedural terrain when no generated world exists, and persists manual add/remove edits to:

```text
../world_data/prototype/voxel_edits.json
```

Default manifest path:

```text
../world_data/prototype/world.json
```

Generate data first once the .NET SDK is available:

```bash
dotnet run --project terrain_tool -- --chunks-x 5 --chunks-y 5 --seed 12345
```

Then open `godot/project.godot` in Godot 4.7 and run the main scene.

Controls:

- Left click: add/remove the hovered voxel.
- Left drag: rotate camera.
- Right drag: pan smoothly; the active voxel window scrolls across the global world as the camera reaches the window edge.
- Mouse wheel: zoom in and out.
- Arrow keys: smoothly scroll the active voxel window one voxel at a time.
- `Home`: return the active window to the world center.
- `X`: toggle remove mode.
- `S`: save edits.
- `R`: clear persisted voxel edits.
- `[` and `]`: cycle the voxel palette.

The editor palette includes terrain and biome materials plus roads, bridges, stone walls, wooden walls, and wooden roofs. The visible voxel window is intentionally bounded for performance; edits are saved with global voxel coordinates so changes can be made across the full generated world.
