using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using static TerrainConstants;

var options = ToolOptions.Parse(args);
var generator = new IslandWorldGenerator(options);
var manifest = generator.Generate();

Directory.CreateDirectory(options.OutputDirectory);
var manifestPath = Path.Combine(options.OutputDirectory, "world.json");
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, jsonOptions));

Console.WriteLine($"Generated {manifest.Chunks.Count} chunks at {options.OutputDirectory}");
Console.WriteLine($"Manifest: {manifestPath}");
Console.WriteLine($"Harbors: {manifest.Harbors.Count}");
Console.WriteLine($"Rivers: {manifest.Rivers.Count}");
Console.WriteLine($"Lakes: {manifest.Lakes.Count}");
Console.WriteLine($"Biome regions: {manifest.BiomeRegions.Count}");

internal sealed record ToolOptions(
    string OutputDirectory,
    int ChunksX,
    int ChunksY,
    int SamplesPerChunk,
    int Seed,
    int ErosionIterations)
{
    public static ToolOptions Parse(string[] args)
    {
        var output = GetDefaultOutputDirectory();
        var chunksX = DefaultChunksX;
        var chunksY = DefaultChunksY;
        var samples = DefaultSamplesPerChunk;
        var seed = 12345;
        var erosionIterations = DefaultErosionIterations;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value after {arg}");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "--output":
                case "-o":
                    output = NextValue();
                    break;
                case "--chunks-x":
                    chunksX = ParsePositiveInt(arg, NextValue());
                    break;
                case "--chunks-y":
                    chunksY = ParsePositiveInt(arg, NextValue());
                    break;
                case "--samples":
                    samples = ParsePositiveInt(arg, NextValue());
                    break;
                case "--seed":
                    seed = int.Parse(NextValue(), CultureInfo.InvariantCulture);
                    break;
                case "--erosion-iterations":
                    erosionIterations = ParseNonNegativeInt(arg, NextValue());
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        if (samples < 2)
        {
            throw new ArgumentException("--samples must be at least 2");
        }

        return new ToolOptions(output, chunksX, chunksY, samples, seed, erosionIterations);
    }

    private static int ParsePositiveInt(string name, string value)
    {
        var parsed = int.Parse(value, CultureInfo.InvariantCulture);
        if (parsed <= 0)
        {
            throw new ArgumentException($"{name} must be positive");
        }

        return parsed;
    }

    private static int ParseNonNegativeInt(string name, string value)
    {
        var parsed = int.Parse(value, CultureInfo.InvariantCulture);
        if (parsed < 0)
        {
            throw new ArgumentException($"{name} must be non-negative");
        }

        return parsed;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("WorldTerrainTool");
        Console.WriteLine($"  --output <path>       Output directory. Default: {GetDefaultOutputDirectory()}");
        Console.WriteLine("  --chunks-x <count>    Chunk columns. Default: 5");
        Console.WriteLine("  --chunks-y <count>    Chunk rows. Default: 5");
        Console.WriteLine("  --samples <count>     Height samples per chunk edge. Default: 257");
        Console.WriteLine("  --seed <number>       Deterministic generation seed. Default: 12345");
        Console.WriteLine("  --erosion-iterations <count>");
        Console.WriteLine("                        Hydraulic erosion iterations. Default: 96");
    }

    private static string GetDefaultOutputDirectory()
    {
        var repoRoot = FindRepositoryRoot(AppContext.BaseDirectory)
            ?? FindRepositoryRoot(Environment.CurrentDirectory)
            ?? Environment.CurrentDirectory;
        return Path.GetFullPath(Path.Combine(repoRoot, "world_data", "prototype"));
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory != null)
        {
            var hasTerrainTool = Directory.Exists(Path.Combine(directory.FullName, "terrain_tool"));
            var hasGodotProject = File.Exists(Path.Combine(directory.FullName, "godot", "project.godot"));
            if (hasTerrainTool && hasGodotProject)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

internal sealed class IslandWorldGenerator
{
    private readonly ToolOptions _options;
    private readonly double _worldWidthMeters;
    private readonly double _worldHeightMeters;
    private readonly List<HarborSite> _harborSites;
    private readonly List<IslandSeed> _smallIslands;
    private readonly List<MountainPeak> _randomMountains;
    private readonly List<TectonicPlate> _tectonicPlates;
    private readonly List<VolcanoSite> _volcanoes;

    public IslandWorldGenerator(ToolOptions options)
    {
        _options = options;
        _worldWidthMeters = options.ChunksX * ChunkSizeMeters;
        _worldHeightMeters = options.ChunksY * ChunkSizeMeters;
        _tectonicPlates = BuildTectonicPlates(options.Seed);
        _smallIslands = BuildSmallIslands(options.Seed);
        _harborSites = BuildHarborSites(options.Seed);
        _randomMountains = BuildRandomMountains(options.Seed);
        _volcanoes = BuildVolcanoes(options.Seed);
    }

    public WorldManifest Generate()
    {
        var chunksDirectory = Path.Combine(_options.OutputDirectory, "chunks");
        if (Directory.Exists(chunksDirectory))
        {
            Directory.Delete(chunksDirectory, recursive: true);
        }

        Directory.CreateDirectory(chunksDirectory);

        var terrainMap = BuildTerrainMap();
        var chunkMetadata = new List<ChunkMetadata>(_options.ChunksX * _options.ChunksY);
        var biomeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var chunkY = 0; chunkY < _options.ChunksY; chunkY++)
        {
            for (var chunkX = 0; chunkX < _options.ChunksX; chunkX++)
            {
                var dominantBiome = GenerateChunk(chunkX, chunkY, chunksDirectory, terrainMap, biomeCounts);
                chunkMetadata.Add(new ChunkMetadata(
                    chunkX,
                    chunkY,
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.height",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.biome",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.flow",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.soil",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.uplift",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.fault",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.volcanic",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.glacier",
                    dominantBiome));
            }
        }

        return new WorldManifest(
            FormatVersion,
            new GeneratorInfo("WorldTerrainTool", _options.Seed, "tectonic_island_archipelago_v2"),
            new ScaleInfo(MetersPerGodotUnit, ChunkSizeMeters, _worldWidthMeters, _worldHeightMeters),
            new HeightInfo(MinHeightMeters, MaxHeightMeters, "uint16_little_endian", "height = min_height + sample / 65535.0 * (max_height - min_height)"),
            new ErosionInfo(_options.ErosionIterations, "float32_little_endian", "flow_accumulation stores normalized accumulated water flow per sample; soil_depth stores loose/deposited soil depth in meters per sample."),
            new GeologyInfo("float32_little_endian", "tectonic_uplift, fault, volcanic, and glacier files store normalized 0..1 derived geology masks per sample."),
            new BiomeInfo("uint8_palette_index", "One biome palette index per height sample, row-major order.", BuildBiomePalette()),
            new ChunkGridInfo(_options.ChunksX, _options.ChunksY, _options.SamplesPerChunk, _options.SamplesPerChunk),
            chunkMetadata,
            BuildHarbors(),
            BuildRivers(),
            BuildLakes(),
            BuildBiomeRegions(biomeCounts));
    }

    private string GenerateChunk(int chunkX, int chunkY, string chunksDirectory, TerrainMap terrainMap, Dictionary<string, int> biomeCounts)
    {
        var samples = _options.SamplesPerChunk;
        var biomeVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var heightPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.height");
        var biomePath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.biome");
        var flowPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.flow");
        var soilPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.soil");
        var upliftPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.uplift");
        var faultPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.fault");
        var volcanicPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.volcanic");
        var glacierPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.glacier");

        using var heightStream = File.Create(heightPath);
        using var heightWriter = new BinaryWriter(heightStream);
        using var biomeStream = File.Create(biomePath);
        using var biomeWriter = new BinaryWriter(biomeStream);
        using var flowStream = File.Create(flowPath);
        using var flowWriter = new BinaryWriter(flowStream);
        using var soilStream = File.Create(soilPath);
        using var soilWriter = new BinaryWriter(soilStream);
        using var upliftStream = File.Create(upliftPath);
        using var upliftWriter = new BinaryWriter(upliftStream);
        using var faultStream = File.Create(faultPath);
        using var faultWriter = new BinaryWriter(faultStream);
        using var volcanicStream = File.Create(volcanicPath);
        using var volcanicWriter = new BinaryWriter(volcanicStream);
        using var glacierStream = File.Create(glacierPath);
        using var glacierWriter = new BinaryWriter(glacierStream);

        for (var localY = 0; localY < samples; localY++)
        {
            for (var localX = 0; localX < samples; localX++)
            {
                var sampleX = chunkX * (samples - 1) + localX;
                var sampleY = chunkY * (samples - 1) + localY;
                var height = terrainMap.HeightMeters[sampleY, sampleX];
                var biome = ClassifyBiomeForCell(terrainMap, sampleX, sampleY);
                heightWriter.Write(ToSample(height));
                biomeWriter.Write(GetBiomeIndex(biome));
                flowWriter.Write((float)terrainMap.FlowAccumulation[sampleY, sampleX]);
                soilWriter.Write((float)terrainMap.SoilDepthMeters[sampleY, sampleX]);
                upliftWriter.Write((float)terrainMap.TectonicUplift[sampleY, sampleX]);
                faultWriter.Write((float)terrainMap.FaultMask[sampleY, sampleX]);
                volcanicWriter.Write((float)terrainMap.VolcanicActivity[sampleY, sampleX]);
                glacierWriter.Write((float)terrainMap.GlacierMask[sampleY, sampleX]);

                biomeVotes[biome] = biomeVotes.GetValueOrDefault(biome) + 1;
                biomeCounts[biome] = biomeCounts.GetValueOrDefault(biome) + 1;
            }
        }

        return biomeVotes.Count == 0
            ? "ocean"
            : biomeVotes.MaxBy(pair => pair.Value).Key;
    }

    private static byte GetBiomeIndex(string biome)
    {
        var index = Array.IndexOf(BiomeIds, biome);
        return (byte)(index < 0 ? 0 : index);
    }

    private static List<BiomePaletteEntry> BuildBiomePalette()
    {
        return BiomeIds
            .Select((id, index) => new BiomePaletteEntry(index, id))
            .ToList();
    }

    private TerrainMap BuildTerrainMap()
    {
        var samples = _options.SamplesPerChunk;
        var width = _options.ChunksX * (samples - 1) + 1;
        var height = _options.ChunksY * (samples - 1) + 1;
        var sampleCount = (long)width * height;
        if (sampleCount > MaxGlobalErosionSamples)
        {
            throw new InvalidOperationException($"Global erosion supports up to {MaxGlobalErosionSamples:N0} samples for this prototype; requested {sampleCount:N0}. Use fewer chunks/samples or implement tiled erosion with padding for larger worlds.");
        }

        var map = new TerrainMap(width, height, ChunkSizeMeters / (samples - 1));

        for (var y = 0; y < height; y++)
        {
            var worldY = y / (double)(height - 1) * _worldHeightMeters;
            for (var x = 0; x < width; x++)
            {
                var worldX = x / (double)(width - 1) * _worldWidthMeters;
                var sample = EvaluateBaseTerrain(worldX, worldY);
                map.HeightMeters[y, x] = sample.HeightMeters;
                map.BaseMoisture[y, x] = sample.Moisture;
                map.Temperature[y, x] = sample.Temperature;
                map.MountainMask[y, x] = sample.MountainMask;
                map.LandMask[y, x] = sample.LandMask;
                map.TectonicUplift[y, x] = sample.TectonicUplift;
                map.FaultMask[y, x] = sample.FaultMask;
                map.VolcanicActivity[y, x] = sample.VolcanicActivity;
                map.SoilDepthMeters[y, x] = sample.HeightMeters > 0.0
                    ? Lerp(0.45, 4.8, SmoothStep(0.08, 0.55, sample.LandMask))
                    : 0.0;
            }
        }

        ErosionSimulator.Run(map, _options.Seed, _options.ErosionIterations);
        GlacialCarvingSimulator.Run(map, _options.Seed);
        return map;
    }

    private string ClassifyBiomeForCell(TerrainMap map, int x, int y)
    {
        var height = map.HeightMeters[y, x];
        var flowWetness = SmoothStep(0.03, 0.72, map.FlowAccumulation[y, x]);
        var soilWetness = SmoothStep(0.6, 7.0, map.SoilDepthMeters[y, x]);
        var moisture = Clamp01(map.BaseMoisture[y, x] + flowWetness * 0.22 + soilWetness * 0.10);
        return ClassifyBiome(height, moisture, map.Temperature[y, x], map.MountainMask[y, x]);
    }

    private BaseTerrainSample EvaluateBaseTerrain(double worldX, double worldY)
    {
        var nx = worldX / _worldWidthMeters * 2.0 - 1.0;
        var ny = worldY / _worldHeightMeters * 2.0 - 1.0;
        var aspect = _worldWidthMeters / _worldHeightMeters;
        var islandX = nx / Math.Max(0.75, aspect);
        var islandY = ny * Math.Max(0.75, aspect);

        var warpX = Noise.Fractal(nx * 1.8 + 11.0, ny * 1.8 - 7.0, _options.Seed, 4, 0.5);
        var warpY = Noise.Fractal(nx * 1.8 - 19.0, ny * 1.8 + 23.0, _options.Seed + 37, 4, 0.5);
        var distance = Math.Sqrt(Math.Pow(islandX + warpX * 0.18, 2) + Math.Pow(islandY + warpY * 0.12, 2));
        var mainIsland = SmoothStep(0.98, 0.48, distance);

        var smallIslandMask = 0.0;
        foreach (var island in _smallIslands)
        {
            var dx = nx - island.X;
            var dy = ny - island.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            smallIslandMask = Math.Max(smallIslandMask, SmoothStep(island.Radius, island.Radius * 0.42, d));
        }

        var land = Clamp01(Math.Max(mainIsland, smallIslandMask));
        var coastNoise = Noise.Fractal(nx * 8.0, ny * 8.0, _options.Seed + 101, 5, 0.52);
        land = Clamp01(land + coastNoise * 0.08);
        var tectonics = EvaluateTectonics(nx, ny);

        var mountainCenterY = 0.08 * Math.Sin(nx * Math.PI * 1.5) - 0.08 * nx;
        var mountainDistance = Math.Abs(ny - mountainCenterY);
        var rangeMask = SmoothStep(0.30, 0.025, mountainDistance) * SmoothStep(0.08, 0.28, mainIsland);
        var ridgeNoise = Noise.Fractal(nx * 10.0, ny * 10.0, _options.Seed + 211, 5, 0.55);
        var ridge = Math.Pow(rangeMask, 1.8) * (0.75 + ridgeNoise * 0.35);

        var secondaryCenterY = -0.48 * nx + 0.22 + 0.07 * Math.Sin((nx + 0.25) * Math.PI * 2.2);
        var secondaryDistance = Math.Abs(ny - secondaryCenterY) / Math.Sqrt(1.0 + 0.48 * 0.48);
        var secondaryRangeMask = SmoothStep(0.24, 0.018, secondaryDistance) * SmoothStep(0.12, 0.34, mainIsland);
        var secondaryRidgeNoise = Noise.Fractal(nx * 12.0 + 3.0, ny * 12.0 - 5.0, _options.Seed + 233, 5, 0.56);
        var secondaryRidge = Math.Pow(secondaryRangeMask, 1.65) * (0.70 + secondaryRidgeNoise * 0.32);

        var randomPeakHeight = 0.0;
        var randomPeakMask = 0.0;
        foreach (var peak in _randomMountains)
        {
            var peakDistance = Distance(nx, ny, peak.X, peak.Y);
            var peakMask = SmoothStep(peak.Radius, peak.Radius * 0.18, peakDistance) * SmoothStep(0.10, 0.30, mainIsland);
            if (peakMask <= 0.0)
            {
                continue;
            }

            var peakNoise = Noise.Fractal(nx * peak.Ruggedness, ny * peak.Ruggedness, _options.Seed + peak.NoiseOffset, 4, 0.52);
            randomPeakHeight += Math.Pow(peakMask, 1.55) * peak.HeightMeters * (0.84 + peakNoise * 0.24);
            randomPeakMask = Math.Max(randomPeakMask, peakMask);
        }

        var tectonicRuggedness = Noise.Fractal(nx * 26.0 + 5.0, ny * 26.0 - 8.0, _options.Seed + 619, 4, 0.50);
        var tectonicUplift = tectonics.UpliftMask * (0.86 + tectonicRuggedness * 0.18);
        var riftDepression = tectonics.RiftMask * (0.72 + Math.Abs(tectonicRuggedness) * 0.28);
        var faultScarp = tectonics.TransformMask * Noise.Fractal(nx * 42.0, ny * 42.0, _options.Seed + 631, 2, 0.45);

        var lowlandNoise = Noise.Fractal(nx * 5.0, ny * 5.0, _options.Seed + 301, 5, 0.5);
        var detailNoise = Noise.Fractal(nx * 18.0, ny * 18.0, _options.Seed + 401, 4, 0.45);
        var coastalShelf = SmoothStep(0.08, 0.35, land);

        var height = -180.0
            + land * 320.0
            + coastalShelf * lowlandNoise * 260.0
            + coastalShelf * detailNoise * 90.0
            + ridge * 1_420.0
            + secondaryRidge * 980.0
            + randomPeakHeight
            + tectonicUplift * land * 720.0
            - riftDepression * land * 360.0
            + faultScarp * land * 220.0;

        var volcanicActivity = 0.0;
        foreach (var volcano in _volcanoes)
        {
            var volcanoDistance = Distance(nx, ny, volcano.X, volcano.Y);
            var apron = SmoothStep(volcano.Radius * 2.8, volcano.Radius * 0.58, volcanoDistance);
            if (apron <= 0.0)
            {
                continue;
            }

            var cone = SmoothStep(volcano.Radius * 1.15, volcano.Radius * 0.08, volcanoDistance);
            var crater = SmoothStep(volcano.Radius * 0.16, 0.0, volcanoDistance);
            var lavaNoise = Noise.Fractal(nx * 34.0 + volcano.NoiseOffset, ny * 34.0 - volcano.NoiseOffset, _options.Seed + 643, 3, 0.48);
            var skirt = Math.Pow(apron, 2.2);
            height += skirt * volcano.HeightMeters * 0.22;
            height += Math.Pow(cone, 1.35) * volcano.HeightMeters * (0.58 + lavaNoise * 0.10);
            height -= crater * volcano.HeightMeters * 0.22;
            volcanicActivity = Math.Max(volcanicActivity, Math.Max(cone, apron * 0.55));
        }

        foreach (var harbor in _harborSites)
        {
            var bayDistance = Distance(worldX, worldY, harbor.XMeters, harbor.YMeters);
            var bay = SmoothStep(harbor.RadiusMeters, 0.0, bayDistance);
            height -= bay * 130.0;

            var shelf = SmoothStep(harbor.RadiusMeters * 1.45, harbor.RadiusMeters * 0.3, bayDistance);
            if (shelf > 0.0 && height > 8.0 && height < 180.0)
            {
                height = Lerp(height, 24.0 + detailNoise * 8.0, shelf * 0.55);
            }
        }

        height = Lerp(-220.0, height, land);
        var combinedMountainMask = Math.Max(Math.Max(rangeMask, Math.Max(secondaryRangeMask, randomPeakMask)), Math.Max(tectonicUplift, volcanicActivity * 0.65));
        var mountainRainShadow = Math.Max(combinedMountainMask, tectonics.BoundaryMask * 0.5);
        var moisture = Clamp01(0.55 + Noise.Fractal(nx * 4.0 - 2.0, ny * 4.0 + 5.0, _options.Seed + 503, 4, 0.5) * 0.32 - mountainRainShadow * Math.Max(0.0, nx) * 0.35);
        var temperature = Clamp01(0.72 - Math.Abs(ny) * 0.25 - Math.Max(0.0, height) / 2_300.0);

        return new BaseTerrainSample(
            SoftLimitHeight(height),
            moisture,
            temperature,
            combinedMountainMask,
            land,
            tectonicUplift,
            Math.Max(tectonics.TransformMask, tectonics.BoundaryMask * 0.45),
            volcanicActivity);
    }

    private TectonicSample EvaluateTectonics(double nx, double ny)
    {
        var nearestIndex = 0;
        var secondIndex = 0;
        var nearestDistance = double.MaxValue;
        var secondDistance = double.MaxValue;

        for (var i = 0; i < _tectonicPlates.Count; i++)
        {
            var plate = _tectonicPlates[i];
            var dx = nx - plate.X;
            var dy = ny - plate.Y;
            var distance = dx * dx + dy * dy;
            if (distance < nearestDistance)
            {
                secondDistance = nearestDistance;
                secondIndex = nearestIndex;
                nearestDistance = distance;
                nearestIndex = i;
            }
            else if (distance < secondDistance)
            {
                secondDistance = distance;
                secondIndex = i;
            }
        }

        var first = _tectonicPlates[nearestIndex];
        var second = _tectonicPlates[secondIndex];
        var gap = Math.Sqrt(secondDistance) - Math.Sqrt(nearestDistance);
        var boundary = SmoothStep(0.14, 0.01, gap);
        if (boundary <= 0.0)
        {
            return new TectonicSample(0.0, 0.0, 0.0, 0.0, first.Id);
        }

        var normalX = second.X - first.X;
        var normalY = second.Y - first.Y;
        var normalLength = Math.Sqrt(normalX * normalX + normalY * normalY);
        if (normalLength <= 0.000001)
        {
            return new TectonicSample(0.0, 0.0, 0.0, 0.0, first.Id);
        }

        normalX /= normalLength;
        normalY /= normalLength;
        var relativeX = first.VelocityX - second.VelocityX;
        var relativeY = first.VelocityY - second.VelocityY;
        var convergence = (relativeX * normalX + relativeY * normalY) * 0.5 + 0.5;
        var shear = Math.Abs(relativeX * -normalY + relativeY * normalX);
        var crustContrast = Math.Abs(first.ContinentalBias - second.ContinentalBias);
        var uplift = boundary * SmoothStep(0.56, 0.92, convergence) * Lerp(0.78, 1.20, crustContrast);
        var rift = boundary * SmoothStep(0.44, 0.12, convergence);
        var transform = boundary * SmoothStep(0.35, 0.95, shear) * (1.0 - Math.Max(uplift, rift) * 0.45);

        return new TectonicSample(boundary, Clamp01(uplift), Clamp01(rift), Clamp01(transform), first.Id);
    }

    private List<HarborMetadata> BuildHarbors()
    {
        return _harborSites.Select((site, index) => new HarborMetadata(
            $"harbor_{index + 1:00}",
            site.Name,
            Math.Round(site.XMeters, 2),
            Math.Round(site.YMeters, 2),
            Math.Round(site.RadiusMeters, 2),
            site.Shelter)).ToList();
    }

    private List<RiverMetadata> BuildRivers()
    {
        return new List<RiverMetadata>();
    }

    private List<LakeMetadata> BuildLakes()
    {
        return new List<LakeMetadata>();
    }

    private static List<BiomeRegion> BuildBiomeRegions(Dictionary<string, int> biomeCounts)
    {
        var total = Math.Max(1, biomeCounts.Values.Sum());
        return biomeCounts
            .OrderByDescending(pair => pair.Value)
            .Select(pair => new BiomeRegion(pair.Key, Math.Round(pair.Value / (double)total, 4)))
            .ToList();
    }

    private List<HarborSite> BuildHarborSites(int seed)
    {
        var random = new Random(seed + 7001);
        var names = new[] { "Northhook", "Greywater", "Eastmere", "Southport", "Westhaven", "Deepwater" };
        var angles = new[] { -2.55, -1.76, -0.42, 0.54, 1.48, 2.38 };
        var sites = new List<HarborSite>();

        for (var i = 0; i < angles.Length; i++)
        {
            var angle = angles[i] + (random.NextDouble() - 0.5) * 0.16;
            var radius = 0.63 + random.NextDouble() * 0.08;
            var nx = Math.Cos(angle) * radius;
            var ny = Math.Sin(angle) * radius * 0.82;
            sites.Add(new HarborSite(
                names[i],
                (nx + 1.0) * 0.5 * _worldWidthMeters,
                (ny + 1.0) * 0.5 * _worldHeightMeters,
                2_400.0 + random.NextDouble() * 1_600.0,
                Math.Round(0.72 + random.NextDouble() * 0.22, 2)));
        }

        return sites;
    }

    private List<MountainPeak> BuildRandomMountains(int seed)
    {
        var random = new Random(seed + 12011);
        var peaks = new List<MountainPeak>();

        for (var i = 0; i < 18; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2.0;
            var distance = 0.12 + random.NextDouble() * 0.58;
            var x = Math.Cos(angle) * distance;
            var y = Math.Sin(angle) * distance * 0.78;

            peaks.Add(new MountainPeak(
                x,
                y,
                0.045 + random.NextDouble() * 0.075,
                240.0 + random.NextDouble() * 650.0,
                14.0 + random.NextDouble() * 12.0,
                random.Next(10_000, 90_000)));
        }

        return peaks;
    }

    private List<TectonicPlate> BuildTectonicPlates(int seed)
    {
        var random = new Random(seed + 17017);
        var plates = new List<TectonicPlate>();
        var plateCount = 11;

        for (var i = 0; i < plateCount; i++)
        {
            var movementAngle = random.NextDouble() * Math.PI * 2.0;
            var speed = 0.45 + random.NextDouble() * 0.95;
            var centerAngle = random.NextDouble() * Math.PI * 2.0;
            var centerRadius = Math.Sqrt(random.NextDouble()) * 1.25;
            plates.Add(new TectonicPlate(
                i,
                Math.Cos(centerAngle) * centerRadius,
                Math.Sin(centerAngle) * centerRadius,
                Math.Cos(movementAngle) * speed,
                Math.Sin(movementAngle) * speed,
                random.NextDouble()));
        }

        return plates;
    }

    private List<VolcanoSite> BuildVolcanoes(int seed)
    {
        var random = new Random(seed + 19019);
        var candidates = new List<(double X, double Y, double Potential)>();

        for (var i = 0; i < 520; i++)
        {
            var x = random.NextDouble() * 2.0 - 1.0;
            var y = random.NextDouble() * 2.0 - 1.0;
            var tectonics = EvaluateTectonics(x, y);
            var islandBias = SmoothStep(1.16, 0.32, Math.Sqrt(x * x + y * y));
            var potential = Math.Max(tectonics.UpliftMask * 0.9, tectonics.RiftMask * 0.55) * islandBias;
            if (potential > 0.32)
            {
                candidates.Add((x, y, potential));
            }
        }

        var volcanoes = candidates
            .OrderByDescending(candidate => candidate.Potential + random.NextDouble() * 0.12)
            .Take(14)
            .Select((candidate, index) => new VolcanoSite(
                candidate.X,
                candidate.Y,
                0.035 + random.NextDouble() * 0.075,
                240.0 + candidate.Potential * 720.0 + random.NextDouble() * 300.0,
                random.Next(1_000, 90_000) + index * 97))
            .ToList();

        for (var i = 0; i < 3; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2.0;
            var radius = random.NextDouble() * 0.58;
            volcanoes.Add(new VolcanoSite(
                Math.Cos(angle) * radius,
                Math.Sin(angle) * radius * 0.8,
                0.045 + random.NextDouble() * 0.055,
                320.0 + random.NextDouble() * 480.0,
                random.Next(1_000, 90_000)));
        }

        return volcanoes;
    }

    private static List<IslandSeed> BuildSmallIslands(int seed)
    {
        var random = new Random(seed + 9001);
        var islands = new List<IslandSeed>();

        for (var i = 0; i < 18; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2.0;
            var distance = 0.72 + random.NextDouble() * 0.42;
            islands.Add(new IslandSeed(
                Math.Cos(angle) * distance,
                Math.Sin(angle) * distance,
                0.035 + random.NextDouble() * 0.09));
        }

        return islands;
    }

    private static ushort ToSample(double heightMeters)
    {
        var normalized = (heightMeters - MinHeightMeters) / (MaxHeightMeters - MinHeightMeters);
        return (ushort)Math.Round(Clamp01(normalized) * ushort.MaxValue);
    }

    private static double SoftLimitHeight(double heightMeters)
    {
        if (heightMeters <= HeightSoftLimitMeters)
        {
            return Clamp(heightMeters, MinHeightMeters, MaxHeightMeters);
        }

        var excess = heightMeters - HeightSoftLimitMeters;
        var compressed = HeightSoftLimitMeters + SoftCeilingRangeMeters * (1.0 - Math.Exp(-excess / SoftCeilingRangeMeters));
        return Clamp(compressed, MinHeightMeters, MaxHeightMeters);
    }

    private static string ClassifyBiome(double height, double moisture, double temperature, double rangeMask)
    {
        if (height <= 0.0)
        {
            return "ocean";
        }

        if (height < 18.0)
        {
            return "coast";
        }

        if (height > 1_180.0)
        {
            return "alpine";
        }

        if (rangeMask > 0.45 && height > 520.0)
        {
            return moisture > 0.55 ? "mountain_forest" : "dry_mountains";
        }

        if (temperature < 0.35)
        {
            return moisture > 0.5 ? "highland_forest" : "cold_steppe";
        }

        if (moisture > 0.72)
        {
            return "temperate_rainforest";
        }

        if (moisture > 0.48)
        {
            return "woodland";
        }

        if (moisture > 0.28)
        {
            return "grassland";
        }

        return "arid_scrub";
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * Clamp01(t);
    private static double Clamp01(double value) => Clamp(value, 0.0, 1.0);
    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
}

internal static class Noise
{
    public static double Fractal(double x, double y, int seed, int octaves, double persistence)
    {
        var total = 0.0;
        var amplitude = 1.0;
        var frequency = 1.0;
        var max = 0.0;

        for (var octave = 0; octave < octaves; octave++)
        {
            total += ValueNoise(x * frequency, y * frequency, seed + octave * 17) * amplitude;
            max += amplitude;
            amplitude *= persistence;
            frequency *= 2.0;
        }

        return max == 0.0 ? 0.0 : total / max;
    }

    private static double ValueNoise(double x, double y, int seed)
    {
        var x0 = FastFloor(x);
        var y0 = FastFloor(y);
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        var sx = Fade(x - x0);
        var sy = Fade(y - y0);

        var n00 = HashToUnit(x0, y0, seed);
        var n10 = HashToUnit(x1, y0, seed);
        var n01 = HashToUnit(x0, y1, seed);
        var n11 = HashToUnit(x1, y1, seed);

        var ix0 = Lerp(n00, n10, sx);
        var ix1 = Lerp(n01, n11, sx);
        return Lerp(ix0, ix1, sy) * 2.0 - 1.0;
    }

    private static int FastFloor(double value) => value >= 0.0 ? (int)value : (int)value - 1;
    private static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double HashToUnit(int x, int y, int seed)
    {
        unchecked
        {
            var hash = seed;
            hash ^= x * 374761393;
            hash = (hash << 13) ^ hash;
            hash ^= y * 668265263;
            hash *= 1274126177;
            return (hash & 0x7fffffff) / (double)int.MaxValue;
        }
    }
}

internal static class ErosionSimulator
{
    private static readonly int[] NeighborX = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] NeighborY = { -1, -1, -1, 0, 0, 1, 1, 1 };
    private static readonly double[] NeighborDistance = { Math.Sqrt(2.0), 1.0, Math.Sqrt(2.0), 1.0, 1.0, Math.Sqrt(2.0), 1.0, Math.Sqrt(2.0) };

    public static void Run(TerrainMap map, int seed, int iterations)
    {
        if (iterations <= 0)
        {
            return;
        }

        var water = new double[map.Height, map.Width];
        var sediment = new double[map.Height, map.Width];
        var nextWater = new double[map.Height, map.Width];
        var nextSediment = new double[map.Height, map.Width];

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            AddRainfall(map, water, seed, iteration);
            Array.Copy(water, nextWater, water.Length);
            Array.Copy(sediment, nextSediment, sediment.Length);

            for (var y = 0; y < map.Height; y++)
            {
                for (var x = 0; x < map.Width; x++)
                {
                    MoveWater(map, water, sediment, nextWater, nextSediment, x, y);
                }
            }

            (water, nextWater) = (nextWater, water);
            (sediment, nextSediment) = (nextSediment, sediment);
            ErodeAndDeposit(map, water, sediment);
            Evaporate(water);
        }

        NormalizeFlow(map);
    }

    private static void AddRainfall(TerrainMap map, double[,] water, int seed, int iteration)
    {
        for (var y = 0; y < map.Height; y++)
        {
            var ny = y / (double)Math.Max(1, map.Height - 1);
            for (var x = 0; x < map.Width; x++)
            {
                if (map.HeightMeters[y, x] <= 0.0 || map.LandMask[y, x] < 0.08)
                {
                    continue;
                }

                var nx = x / (double)Math.Max(1, map.Width - 1);
                var rainfallNoise = Noise.Fractal(nx * 9.0 + iteration * 0.017, ny * 9.0 - iteration * 0.013, seed + 31003, 3, 0.55);
                var rainfall = ErosionConstants.RainfallMeters * map.LandMask[y, x] * (0.78 + rainfallNoise * 0.22);
                water[y, x] += Math.Max(0.0, rainfall);
            }
        }
    }

    private static void MoveWater(TerrainMap map, double[,] water, double[,] sediment, double[,] nextWater, double[,] nextSediment, int x, int y)
    {
        var currentWater = water[y, x];
        if (currentWater <= 0.000001)
        {
            return;
        }

        var currentSurface = map.HeightMeters[y, x] + currentWater;
        Span<double> weights = stackalloc double[8];
        var totalWeight = 0.0;

        for (var i = 0; i < 8; i++)
        {
            var nx = x + NeighborX[i];
            var ny = y + NeighborY[i];
            if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height)
            {
                continue;
            }

            var neighborSurface = map.HeightMeters[ny, nx] + water[ny, nx];
            var drop = currentSurface - neighborSurface;
            if (drop <= 0.0)
            {
                continue;
            }

            var weight = drop / NeighborDistance[i];
            weights[i] = weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0.0)
        {
            return;
        }

        var outWater = Math.Min(currentWater, currentWater * ErosionConstants.FlowRate);
        var outSediment = sediment[y, x] * outWater / Math.Max(currentWater, 0.000001);
        nextWater[y, x] -= outWater;
        nextSediment[y, x] -= outSediment;
        map.FlowAccumulation[y, x] += outWater;

        for (var i = 0; i < 8; i++)
        {
            if (weights[i] <= 0.0)
            {
                continue;
            }

            var nx = x + NeighborX[i];
            var ny = y + NeighborY[i];
            var share = weights[i] / totalWeight;
            nextWater[ny, nx] += outWater * share;
            nextSediment[ny, nx] += outSediment * share;
        }
    }

    private static void ErodeAndDeposit(TerrainMap map, double[,] water, double[,] sediment)
    {
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var waterDepth = water[y, x];
                if (waterDepth <= 0.000001 || map.HeightMeters[y, x] <= 0.0)
                {
                    sediment[y, x] *= 0.96;
                    continue;
                }

                var slope = CalculateSlope(map, x, y);
                var capacity = Math.Max(ErosionConstants.MinimumSedimentCapacity, waterDepth * slope * ErosionConstants.SedimentCapacityFactor);
                if (sediment[y, x] > capacity)
                {
                    var deposit = (sediment[y, x] - capacity) * ErosionConstants.DepositionRate;
                    sediment[y, x] -= deposit;
                    map.HeightMeters[y, x] = Math.Min(MaxHeightMeters, map.HeightMeters[y, x] + deposit);
                    map.SoilDepthMeters[y, x] = Math.Min(ErosionConstants.MaximumSoilDepthMeters, map.SoilDepthMeters[y, x] + deposit);
                    continue;
                }

                var requestedErosion = (capacity - sediment[y, x]) * ErosionConstants.ErosionRate;
                if (requestedErosion <= 0.0)
                {
                    continue;
                }

                var soilErosion = Math.Min(map.SoilDepthMeters[y, x], requestedErosion);
                var rockErosion = Math.Max(0.0, requestedErosion - soilErosion) * ErosionConstants.RockErosionMultiplier;
                var totalErosion = soilErosion + rockErosion;
                map.SoilDepthMeters[y, x] -= soilErosion;
                map.HeightMeters[y, x] = Math.Max(MinHeightMeters, map.HeightMeters[y, x] - totalErosion);
                sediment[y, x] += totalErosion;
            }
        }
    }

    private static double CalculateSlope(TerrainMap map, int x, int y)
    {
        var height = map.HeightMeters[y, x];
        var steepest = 0.0;

        for (var i = 0; i < 8; i++)
        {
            var nx = x + NeighborX[i];
            var ny = y + NeighborY[i];
            if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height)
            {
                continue;
            }

            var drop = height - map.HeightMeters[ny, nx];
            if (drop <= 0.0)
            {
                continue;
            }

            steepest = Math.Max(steepest, drop / (map.CellSizeMeters * NeighborDistance[i]));
        }

        return steepest;
    }

    private static void Evaporate(double[,] water)
    {
        for (var y = 0; y < water.GetLength(0); y++)
        {
            for (var x = 0; x < water.GetLength(1); x++)
            {
                water[y, x] *= 1.0 - ErosionConstants.EvaporationRate;
            }
        }
    }

    private static void NormalizeFlow(TerrainMap map)
    {
        var maxFlow = 0.0;
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                maxFlow = Math.Max(maxFlow, map.FlowAccumulation[y, x]);
            }
        }

        if (maxFlow <= 0.0)
        {
            return;
        }

        var maxLog = Math.Log(1.0 + maxFlow);
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                map.FlowAccumulation[y, x] = Math.Log(1.0 + map.FlowAccumulation[y, x]) / maxLog;
            }
        }
    }
}

internal static class GlacialCarvingSimulator
{
    private static readonly int[] NeighborX = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] NeighborY = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public static void Run(TerrainMap map, int seed)
    {
        var stride = Math.Max(3, Math.Min(map.Width, map.Height) / 96);
        var random = new Random(seed + 7603);
        for (var y = stride; y < map.Height - stride; y += stride)
        {
            for (var x = stride; x < map.Width - stride; x += stride)
            {
                var jitterX = random.Next(-stride / 2, stride / 2 + 1);
                var jitterY = random.Next(-stride / 2, stride / 2 + 1);
                var sampleX = Math.Min(map.Width - stride - 1, Math.Max(stride, x + jitterX));
                var sampleY = Math.Min(map.Height - stride - 1, Math.Max(stride, y + jitterY));
                var accumulation = GetAccumulationPotential(map, sampleX, sampleY, seed);
                if (accumulation <= 0.0)
                {
                    continue;
                }

                CarveGlacierPath(map, sampleX, sampleY, accumulation);
            }
        }

        SmoothGlaciatedCells(map);
    }

    private static double GetAccumulationPotential(TerrainMap map, int x, int y, int seed)
    {
        var height = map.HeightMeters[y, x];
        if (height < 1_150.0 || map.LandMask[y, x] < 0.35)
        {
            return 0.0;
        }

        var volcanicQuiet = 1.0 - SmoothStep(0.05, 0.28, map.VolcanicActivity[y, x]);
        if (volcanicQuiet <= 0.0)
        {
            return 0.0;
        }

        var cold = SmoothStep(0.50, 0.20, map.Temperature[y, x]);
        var elevation = SmoothStep(1_150.0, 2_450.0, height);
        var moisture = SmoothStep(0.32, 0.74, map.BaseMoisture[y, x]);
        var nx = x / (double)Math.Max(1, map.Width - 1);
        var ny = y / (double)Math.Max(1, map.Height - 1);
        var snowNoise = Noise.Fractal(nx * 13.0, ny * 13.0, seed + 7301, 3, 0.52) * 0.18 + 0.9;
        return Clamp01(cold * elevation * (0.42 + moisture * 0.58) * snowNoise * volcanicQuiet);
    }

    private static void CarveGlacierPath(TerrainMap map, int startX, int startY, double strength)
    {
        var x = startX;
        var y = startY;
        var currentStrength = strength;
        var maxSteps = Math.Max(18, Math.Min(map.Width, map.Height) / 4);

        for (var step = 0; step < maxSteps; step++)
        {
            if (x <= 2 || y <= 2 || x >= map.Width - 3 || y >= map.Height - 3)
            {
                break;
            }

            var height = map.HeightMeters[y, x];
            if (height <= 12.0 || currentStrength <= 0.045)
            {
                break;
            }

            var volcanicQuiet = 1.0 - SmoothStep(0.05, 0.28, map.VolcanicActivity[y, x]);
            if (volcanicQuiet <= 0.0)
            {
                break;
            }

            var radius = 1 + (int)Math.Round(currentStrength * 3.0 + SmoothStep(2_600.0, 1_000.0, height));
            var depth = currentStrength * volcanicQuiet * SmoothStep(300.0, 2_600.0, height) * 5.8;
            CarveCrossSection(map, x, y, radius, depth);
            map.GlacierMask[y, x] = Math.Max(map.GlacierMask[y, x], currentStrength);

            if (!TryFindDownhillNeighbor(map, x, y, out var nextX, out var nextY))
            {
                break;
            }

            x = nextX;
            y = nextY;
            currentStrength *= 0.972;
        }
    }

    private static void CarveCrossSection(TerrainMap map, int centerX, int centerY, int radius, double depth)
    {
        var centerHeight = map.HeightMeters[centerY, centerX];
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
                {
                    continue;
                }

                var dx = x - centerX;
                var dy = y - centerY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > radius)
                {
                    continue;
                }

                var normalized = distance / Math.Max(1.0, radius);
                var uShape = 1.0 - normalized * normalized;
                var volcanicQuiet = 1.0 - SmoothStep(0.05, 0.28, map.VolcanicActivity[y, x]);
                if (volcanicQuiet <= 0.0)
                {
                    continue;
                }

                var target = centerHeight - depth * uShape;
                map.HeightMeters[y, x] = Math.Min(map.HeightMeters[y, x], Math.Max(0.0, target));
                map.SoilDepthMeters[y, x] *= 1.0 - uShape * volcanicQuiet * 0.42;
                map.GlacierMask[y, x] = Math.Max(map.GlacierMask[y, x], uShape * volcanicQuiet);
            }
        }
    }

    private static bool TryFindDownhillNeighbor(TerrainMap map, int x, int y, out int nextX, out int nextY)
    {
        nextX = x;
        nextY = y;
        var bestDrop = 0.0;
        var height = map.HeightMeters[y, x];

        for (var i = 0; i < NeighborX.Length; i++)
        {
            var nx = x + NeighborX[i];
            var ny = y + NeighborY[i];
            if (nx < 0 || ny < 0 || nx >= map.Width || ny >= map.Height)
            {
                continue;
            }

            var drop = height - map.HeightMeters[ny, nx];
            if (drop > bestDrop)
            {
                bestDrop = drop;
                nextX = nx;
                nextY = ny;
            }
        }

        return bestDrop > 0.05;
    }

    private static void SmoothGlaciatedCells(TerrainMap map)
    {
        var smoothed = new double[map.Height, map.Width];
        Array.Copy(map.HeightMeters, smoothed, map.HeightMeters.Length);

        for (var y = 1; y < map.Height - 1; y++)
        {
            for (var x = 1; x < map.Width - 1; x++)
            {
                var glacier = map.GlacierMask[y, x];
                if (glacier <= 0.0)
                {
                    continue;
                }

                var average = 0.0;
                for (var oy = -1; oy <= 1; oy++)
                {
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        average += map.HeightMeters[y + oy, x + ox];
                    }
                }

                average /= 9.0;
                smoothed[y, x] = Lerp(map.HeightMeters[y, x], average, glacier * 0.34);
            }
        }

        Array.Copy(smoothed, map.HeightMeters, map.HeightMeters.Length);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * Clamp01(t);
    private static double Clamp01(double value) => Math.Min(1.0, Math.Max(0.0, value));
}

internal static class ErosionConstants
{
    public const double RainfallMeters = 0.035;
    public const double FlowRate = 0.58;
    public const double EvaporationRate = 0.045;
    public const double SedimentCapacityFactor = 4.2;
    public const double MinimumSedimentCapacity = 0.0008;
    public const double ErosionRate = 0.055;
    public const double DepositionRate = 0.14;
    public const double RockErosionMultiplier = 0.08;
    public const double MaximumSoilDepthMeters = 14.0;
}

internal sealed class TerrainMap
{
    public TerrainMap(int width, int height, double cellSizeMeters)
    {
        Width = width;
        Height = height;
        CellSizeMeters = cellSizeMeters;
        HeightMeters = new double[height, width];
        BaseMoisture = new double[height, width];
        Temperature = new double[height, width];
        MountainMask = new double[height, width];
        LandMask = new double[height, width];
        TectonicUplift = new double[height, width];
        FaultMask = new double[height, width];
        VolcanicActivity = new double[height, width];
        GlacierMask = new double[height, width];
        FlowAccumulation = new double[height, width];
        SoilDepthMeters = new double[height, width];
    }

    public int Width { get; }
    public int Height { get; }
    public double CellSizeMeters { get; }
    public double[,] HeightMeters { get; }
    public double[,] BaseMoisture { get; }
    public double[,] Temperature { get; }
    public double[,] MountainMask { get; }
    public double[,] LandMask { get; }
    public double[,] TectonicUplift { get; }
    public double[,] FaultMask { get; }
    public double[,] VolcanicActivity { get; }
    public double[,] GlacierMask { get; }
    public double[,] FlowAccumulation { get; }
    public double[,] SoilDepthMeters { get; }
}

internal sealed record BaseTerrainSample(double HeightMeters, double Moisture, double Temperature, double MountainMask, double LandMask, double TectonicUplift, double FaultMask, double VolcanicActivity);
internal sealed record TectonicSample(double BoundaryMask, double UpliftMask, double RiftMask, double TransformMask, int PlateId);
internal sealed record IslandSeed(double X, double Y, double Radius);
internal sealed record HarborSite(string Name, double XMeters, double YMeters, double RadiusMeters, double Shelter);
internal sealed record MountainPeak(double X, double Y, double Radius, double HeightMeters, double Ruggedness, int NoiseOffset);
internal sealed record TectonicPlate(int Id, double X, double Y, double VelocityX, double VelocityY, double ContinentalBias);
internal sealed record VolcanoSite(double X, double Y, double Radius, double HeightMeters, int NoiseOffset);

internal static class TerrainConstants
{
    public static readonly string[] BiomeIds =
    {
        "ocean",
        "coast",
        "river",
        "lake",
        "grassland",
        "woodland",
        "temperate_rainforest",
        "mountain_forest",
        "dry_mountains",
        "highland_forest",
        "cold_steppe",
        "alpine",
        "arid_scrub"
    };

    public const int DefaultChunksX = 5;
    public const int DefaultChunksY = 5;
    public const int DefaultSamplesPerChunk = 257;
    public const int DefaultErosionIterations = 96;
    public const int MaxGlobalErosionSamples = 10_000_000;
    public const double ChunkSizeMeters = 10_000.0;
    public const double MetersPerGodotUnit = 1.0;
    public const double MinHeightMeters = -250.0;
    public const double MaxHeightMeters = 1_600.0;
    public const double HeightSoftLimitMeters = 1_360.0;
    public const double SoftCeilingRangeMeters = MaxHeightMeters - HeightSoftLimitMeters;
    public const int FormatVersion = 1;
}

internal sealed record WorldManifest(
    int FormatVersion,
    GeneratorInfo Generator,
    ScaleInfo Scale,
    HeightInfo Height,
    ErosionInfo Erosion,
    GeologyInfo Geology,
    BiomeInfo Biome,
    ChunkGridInfo ChunkGrid,
    List<ChunkMetadata> Chunks,
    List<HarborMetadata> Harbors,
    List<RiverMetadata> Rivers,
    List<LakeMetadata> Lakes,
    List<BiomeRegion> BiomeRegions);

internal sealed record GeneratorInfo(string Tool, int Seed, string Algorithm);
internal sealed record ScaleInfo(double MetersPerGodotUnit, double ChunkSizeMeters, double WorldWidthMeters, double WorldHeightMeters);
internal sealed record HeightInfo(double MinHeight, double MaxHeight, string SampleFormat, string DecodeRule);
internal sealed record ErosionInfo(int Iterations, string DerivedMapFormat, string DecodeRule);
internal sealed record GeologyInfo(string DerivedMapFormat, string DecodeRule);
internal sealed record BiomeInfo(string SampleFormat, string DecodeRule, List<BiomePaletteEntry> Palette);
internal sealed record BiomePaletteEntry(int Index, string Id);
internal sealed record ChunkGridInfo(int ChunksX, int ChunksY, int SamplesX, int SamplesY);
internal sealed record ChunkMetadata(int X, int Y, string HeightFile, string BiomeFile, string FlowFile, string SoilFile, string UpliftFile, string FaultFile, string VolcanicFile, string GlacierFile, string DominantBiome);
internal sealed record HarborMetadata(string Id, string Name, double XMeters, double YMeters, double RadiusMeters, double Shelter);
internal sealed record RiverMetadata(string Id, string Name, double WidthMeters, List<RiverPointMetadata> Points);
internal sealed record RiverPointMetadata(double XMeters, double YMeters);
internal sealed record LakeMetadata(string Id, string Name, double XMeters, double YMeters, double RadiusMeters, double WaterHeightMeters);
internal sealed record BiomeRegion(string Id, double ApproximateCoverage);
