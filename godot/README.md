# World Landscape Viewer

Godot 4.7 project for loading the generated landscape files from `../world_data`.

Default manifest path:

```text
../world_data/prototype/world.json
```

Generate data first from the repository root once the .NET SDK is available:

```bash
dotnet run --project terrain_tool -- --output world_data/prototype --chunks-x 5 --chunks-y 5 --seed 12345
```

Then open `godot/project.godot` in Godot 4.7 and run the main scene.

Controls:

- Left drag: rotate camera.
- Right drag, middle drag, or left+right drag: pan/scroll across the landscape.
- Mouse wheel: zoom in and out.
