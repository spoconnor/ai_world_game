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
    int Seed)
{
    public static ToolOptions Parse(string[] args)
    {
        var output = Path.Combine("world_data", "prototype");
        var chunksX = DefaultChunksX;
        var chunksY = DefaultChunksY;
        var samples = DefaultSamplesPerChunk;
        var seed = 12345;

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

        return new ToolOptions(output, chunksX, chunksY, samples, seed);
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

    private static void PrintHelp()
    {
        Console.WriteLine("WorldTerrainTool");
        Console.WriteLine("  --output <path>       Output directory. Default: world_data/prototype");
        Console.WriteLine("  --chunks-x <count>    Chunk columns. Default: 5");
        Console.WriteLine("  --chunks-y <count>    Chunk rows. Default: 5");
        Console.WriteLine("  --samples <count>     Height samples per chunk edge. Default: 257");
        Console.WriteLine("  --seed <number>       Deterministic generation seed. Default: 12345");
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
    private readonly List<LakeBasin> _lakes;
    private readonly List<RiverPath> _rivers;

    public IslandWorldGenerator(ToolOptions options)
    {
        _options = options;
        _worldWidthMeters = options.ChunksX * ChunkSizeMeters;
        _worldHeightMeters = options.ChunksY * ChunkSizeMeters;
        _smallIslands = BuildSmallIslands(options.Seed);
        _harborSites = BuildHarborSites(options.Seed);
        _randomMountains = BuildRandomMountains(options.Seed);
        _lakes = new List<LakeBasin>();
        _rivers = BuildRiverPaths(options.Seed);
    }

    public WorldManifest Generate()
    {
        var chunksDirectory = Path.Combine(_options.OutputDirectory, "chunks");
        if (Directory.Exists(chunksDirectory))
        {
            Directory.Delete(chunksDirectory, recursive: true);
        }

        Directory.CreateDirectory(chunksDirectory);

        var chunkMetadata = new List<ChunkMetadata>(_options.ChunksX * _options.ChunksY);
        var biomeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var chunkY = 0; chunkY < _options.ChunksY; chunkY++)
        {
            for (var chunkX = 0; chunkX < _options.ChunksX; chunkX++)
            {
                var dominantBiome = GenerateChunk(chunkX, chunkY, chunksDirectory, biomeCounts);
                chunkMetadata.Add(new ChunkMetadata(
                    chunkX,
                    chunkY,
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.height",
                    $"chunks/chunk_x{chunkX:000}_y{chunkY:000}.biome",
                    dominantBiome));
            }
        }

        return new WorldManifest(
            FormatVersion,
            new GeneratorInfo("WorldTerrainTool", _options.Seed, "island_archipelago_v1"),
            new ScaleInfo(MetersPerGodotUnit, ChunkSizeMeters, _worldWidthMeters, _worldHeightMeters),
            new HeightInfo(MinHeightMeters, MaxHeightMeters, "uint16_little_endian", "height = min_height + sample / 65535.0 * (max_height - min_height)"),
            new BiomeInfo("uint8_palette_index", "One biome palette index per height sample, row-major order.", BuildBiomePalette()),
            new ChunkGridInfo(_options.ChunksX, _options.ChunksY, _options.SamplesPerChunk, _options.SamplesPerChunk),
            chunkMetadata,
            BuildHarbors(),
            BuildRivers(),
            BuildLakes(),
            BuildBiomeRegions(biomeCounts));
    }

    private string GenerateChunk(int chunkX, int chunkY, string chunksDirectory, Dictionary<string, int> biomeCounts)
    {
        var samples = _options.SamplesPerChunk;
        var biomeVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var heightPath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.height");
        var biomePath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.biome");

        using var heightStream = File.Create(heightPath);
        using var heightWriter = new BinaryWriter(heightStream);
        using var biomeStream = File.Create(biomePath);
        using var biomeWriter = new BinaryWriter(biomeStream);

        for (var localY = 0; localY < samples; localY++)
        {
            for (var localX = 0; localX < samples; localX++)
            {
                var worldX = (chunkX + localX / (double)(samples - 1)) * ChunkSizeMeters;
                var worldY = (chunkY + localY / (double)(samples - 1)) * ChunkSizeMeters;
                var terrain = EvaluateTerrain(worldX, worldY);
                heightWriter.Write(ToSample(terrain.HeightMeters));
                biomeWriter.Write(GetBiomeIndex(terrain.Biome));

                biomeVotes[terrain.Biome] = biomeVotes.GetValueOrDefault(terrain.Biome) + 1;
                biomeCounts[terrain.Biome] = biomeCounts.GetValueOrDefault(terrain.Biome) + 1;
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

    private TerrainSample EvaluateTerrain(double worldX, double worldY)
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

        var lowlandNoise = Noise.Fractal(nx * 5.0, ny * 5.0, _options.Seed + 301, 5, 0.5);
        var detailNoise = Noise.Fractal(nx * 18.0, ny * 18.0, _options.Seed + 401, 4, 0.45);
        var coastalShelf = SmoothStep(0.08, 0.35, land);

        var height = -180.0
            + land * 320.0
            + coastalShelf * lowlandNoise * 260.0
            + coastalShelf * detailNoise * 90.0
            + ridge * 2_850.0
            + secondaryRidge * 1_950.0
            + randomPeakHeight;

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

        var riverStrength = 0.0;
        foreach (var river in _rivers)
        {
            var riverSample = SampleRiver(river, worldX, worldY);
            var channel = SmoothStep(river.WidthMeters, 0.0, riverSample.DistanceMeters);
            var valley = SmoothStep(river.WidthMeters * 5.5, 0.0, riverSample.DistanceMeters);
            if (valley <= 0.0)
            {
                continue;
            }

            var bedHeight = Lerp(river.SourceHeightMeters, -8.0, Math.Pow(riverSample.T, 0.72));
            height -= valley * (24.0 + 120.0 * (1.0 - riverSample.T));
            if (height > bedHeight)
            {
                height = Lerp(height, bedHeight, channel * 0.86);
            }

            riverStrength = Math.Max(riverStrength, channel);
        }

        var lakeStrength = 0.0;
        foreach (var lake in _lakes)
        {
            var lakeDistance = Distance(worldX, worldY, lake.XMeters, lake.YMeters);
            var lakeCore = SmoothStep(lake.RadiusMeters, lake.RadiusMeters * 0.72, lakeDistance);
            var lakeShore = SmoothStep(lake.RadiusMeters * 1.35, lake.RadiusMeters * 0.9, lakeDistance);
            if (lakeShore <= 0.0)
            {
                continue;
            }

            if (height > lake.WaterHeightMeters)
            {
                height = Lerp(height, lake.WaterHeightMeters, lakeCore * 0.94);
            }

            height -= lakeShore * 12.0;
            lakeStrength = Math.Max(lakeStrength, lakeCore);
        }

        height = Lerp(-220.0, height, land);
        var mountainRainShadow = Math.Max(rangeMask, Math.Max(secondaryRangeMask * 0.75, randomPeakMask * 0.45));
        var moisture = Clamp01(0.55 + Noise.Fractal(nx * 4.0 - 2.0, ny * 4.0 + 5.0, _options.Seed + 503, 4, 0.5) * 0.32 - mountainRainShadow * Math.Max(0.0, nx) * 0.35 + riverStrength * 0.22 + lakeStrength * 0.28);
        var temperature = Clamp01(0.72 - Math.Abs(ny) * 0.25 - Math.Max(0.0, height) / 4_500.0);
        var combinedMountainMask = Math.Max(rangeMask, Math.Max(secondaryRangeMask, randomPeakMask));

        return new TerrainSample(Clamp(height, MinHeightMeters, MaxHeightMeters), ClassifyBiome(height, moisture, temperature, combinedMountainMask, riverStrength, lakeStrength));
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
        return _rivers.Select((river, index) => new RiverMetadata(
            $"river_{index + 1:00}",
            river.Name,
            Math.Round(river.WidthMeters, 2),
            river.Points
                .Select(point => new RiverPointMetadata(
                    Math.Round((point.X + 1.0) * 0.5 * _worldWidthMeters, 2),
                    Math.Round((point.Y + 1.0) * 0.5 * _worldHeightMeters, 2)))
                .ToList())).ToList();
    }

    private List<LakeMetadata> BuildLakes()
    {
        return _lakes.Select((lake, index) => new LakeMetadata(
            $"lake_{index + 1:00}",
            lake.Name,
            Math.Round(lake.XMeters, 2),
            Math.Round(lake.YMeters, 2),
            Math.Round(lake.RadiusMeters, 2),
            Math.Round(lake.WaterHeightMeters, 2))).ToList();
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
                420.0 + random.NextDouble() * 1_150.0,
                14.0 + random.NextDouble() * 12.0,
                random.Next(10_000, 90_000)));
        }

        return peaks;
    }

    private List<RiverPath> BuildRiverPaths(int seed)
    {
        var random = new Random(seed + 15091);
        var names = new[] { "Ashrun", "Brightwater", "Stonewash", "Lowmere", "Redbranch", "Hollowrun" };
        var rivers = new List<RiverPath>();
        var sources = new List<NormalizedPoint>();

        for (var i = 0; i < names.Length; i++)
        {
            var source = PickRiverSource(random, sources);
            sources.Add(source);
            var sourceHeight = BaseTerrainHeight(source.X, source.Y);
            var points = TraceRiverDownhill(source, random);

            rivers.Add(new RiverPath(
                names[i],
                115.0 + random.NextDouble() * 95.0,
                Math.Max(420.0, sourceHeight - 80.0),
                points));
        }

        return rivers;
    }

    private NormalizedPoint PickRiverSource(Random random, List<NormalizedPoint> existingSources)
    {
        var best = new NormalizedPoint(0.0, 0.0);
        var bestScore = double.MinValue;

        for (var i = 0; i < 160; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2.0;
            var radius = Math.Sqrt(random.NextDouble()) * 0.58;
            var candidate = new NormalizedPoint(
                Math.Cos(angle) * radius,
                Math.Sin(angle) * radius * 0.82);

            var height = BaseTerrainHeight(candidate.X, candidate.Y);
            if (height < 520.0)
            {
                continue;
            }

            var spacingPenalty = existingSources
                .Select(source => Math.Max(0.0, 0.28 - Distance(candidate.X, candidate.Y, source.X, source.Y)) * 1_800.0)
                .DefaultIfEmpty(0.0)
                .Sum();
            var score = height - spacingPenalty + random.NextDouble() * 120.0;
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return bestScore == double.MinValue
            ? new NormalizedPoint((random.NextDouble() - 0.5) * 0.55, (random.NextDouble() - 0.5) * 0.45)
            : best;
    }

    private List<NormalizedPoint> TraceRiverDownhill(NormalizedPoint source, Random random)
    {
        const int maxSteps = 220;
        const double step = 0.020;

        var points = new List<NormalizedPoint> { source };
        var current = source;
        var previousDirection = Normalize(current.X, current.Y);
        var lakeCount = 0;

        for (var i = 0; i < maxSteps; i++)
        {
            var currentHeight = BaseTerrainHeight(current.X, current.Y);
            if (currentHeight <= 12.0 || Math.Abs(current.X) > 0.94 || Math.Abs(current.Y) > 0.90)
            {
                break;
            }

            var best = current;
            var bestScore = double.MaxValue;
            var bestHeight = currentHeight;

            for (var directionIndex = 0; directionIndex < 16; directionIndex++)
            {
                var angle = directionIndex / 16.0 * Math.PI * 2.0;
                var direction = new NormalizedVector(Math.Cos(angle), Math.Sin(angle));
                var candidate = new NormalizedPoint(
                    Clamp(current.X + direction.X * step, -0.98, 0.98),
                    Clamp(current.Y + direction.Y * step, -0.94, 0.94));
                var candidateHeight = BaseTerrainHeight(candidate.X, candidate.Y);
                var downhill = currentHeight - candidateHeight;
                var outward = Math.Sqrt(candidate.X * candidate.X + candidate.Y * candidate.Y);
                var turnPenalty = 1.0 - (direction.X * previousDirection.X + direction.Y * previousDirection.Y);
                var meander = Noise.Fractal(candidate.X * 24.0, candidate.Y * 24.0, _options.Seed + 17003 + i, 2, 0.5);

                var score = candidateHeight
                    - Math.Max(0.0, downhill) * 0.55
                    - outward * 80.0
                    + Math.Max(0.0, -downhill - 24.0) * 18.0
                    + turnPenalty * 38.0
                    + meander * 16.0
                    + random.NextDouble() * 4.0;

                if (score < bestScore)
                {
                    best = candidate;
                    bestHeight = candidateHeight;
                    bestScore = score;
                }
            }

            if (Distance(current.X, current.Y, best.X, best.Y) <= 0.0001)
            {
                break;
            }

            if (lakeCount < 3 && currentHeight > 65.0 && bestHeight > currentHeight - 2.5)
            {
                var lake = BuildLakeAt(current, currentHeight, previousDirection, random);
                _lakes.Add(lake.Basin);
                lakeCount++;
                points.Add(lake.Center);
                points.Add(lake.Outlet);
                previousDirection = Normalize(lake.Outlet.X - current.X, lake.Outlet.Y - current.Y);
                current = lake.Outlet;
                continue;
            }

            previousDirection = Normalize(best.X - current.X, best.Y - current.Y);
            current = best;

            if (i % 3 == 0 || BaseTerrainHeight(current.X, current.Y) <= 18.0)
            {
                points.Add(current);
            }
        }

        if (points[^1] != current)
        {
            points.Add(current);
        }

        return SimplifyRiverPoints(points);
    }

    private RiverLakeCrossing BuildLakeAt(NormalizedPoint center, double basinHeight, NormalizedVector flowDirection, Random random)
    {
        var radiusNormalized = 0.026 + random.NextDouble() * 0.024;
        var radiusMeters = radiusNormalized * Math.Min(_worldWidthMeters, _worldHeightMeters) * 0.5;
        var waterHeight = Math.Max(18.0, basinHeight + 4.0 + random.NextDouble() * 8.0);
        var outlet = center;
        var outletScore = double.MaxValue;

        for (var directionIndex = 0; directionIndex < 48; directionIndex++)
        {
            var angle = directionIndex / 48.0 * Math.PI * 2.0;
            var direction = new NormalizedVector(Math.Cos(angle), Math.Sin(angle));
            var candidate = new NormalizedPoint(
                Clamp(center.X + direction.X * radiusNormalized * 1.28, -0.98, 0.98),
                Clamp(center.Y + direction.Y * radiusNormalized * 1.28, -0.94, 0.94));
            var candidateHeight = BaseTerrainHeight(candidate.X, candidate.Y);
            var sameSideBonus = direction.X * flowDirection.X + direction.Y * flowDirection.Y;
            var outward = Math.Sqrt(candidate.X * candidate.X + candidate.Y * candidate.Y);
            var score = candidateHeight - sameSideBonus * 90.0 - outward * 35.0 + random.NextDouble() * 4.0;

            if (score < outletScore)
            {
                outlet = candidate;
                outletScore = score;
            }
        }

        var worldX = (center.X + 1.0) * 0.5 * _worldWidthMeters;
        var worldY = (center.Y + 1.0) * 0.5 * _worldHeightMeters;
        var basin = new LakeBasin(
            $"Lake {_lakes.Count + 1:00}",
            worldX,
            worldY,
            radiusMeters,
            waterHeight);

        return new RiverLakeCrossing(center, outlet, basin);
    }

    private static List<NormalizedPoint> SimplifyRiverPoints(List<NormalizedPoint> points)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        var simplified = new List<NormalizedPoint> { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var previous = simplified[^1];
            var current = points[i];
            var next = points[i + 1];
            var ab = Normalize(current.X - previous.X, current.Y - previous.Y);
            var bc = Normalize(next.X - current.X, next.Y - current.Y);
            var dot = ab.X * bc.X + ab.Y * bc.Y;

            if (Distance(previous.X, previous.Y, current.X, current.Y) > 0.055 || dot < 0.985)
            {
                simplified.Add(current);
            }
        }

        simplified.Add(points[^1]);
        return simplified;
    }

    private double BaseTerrainHeight(double nx, double ny)
    {
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
        }

        var lowlandNoise = Noise.Fractal(nx * 5.0, ny * 5.0, _options.Seed + 301, 5, 0.5);
        var detailNoise = Noise.Fractal(nx * 18.0, ny * 18.0, _options.Seed + 401, 4, 0.45);
        var coastalShelf = SmoothStep(0.08, 0.35, land);
        var height = -180.0
            + land * 320.0
            + coastalShelf * lowlandNoise * 260.0
            + coastalShelf * detailNoise * 90.0
            + ridge * 2_850.0
            + secondaryRidge * 1_950.0
            + randomPeakHeight;

        var worldX = (nx + 1.0) * 0.5 * _worldWidthMeters;
        var worldY = (ny + 1.0) * 0.5 * _worldHeightMeters;
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

        return Lerp(-220.0, height, land);
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

    private static string ClassifyBiome(double height, double moisture, double temperature, double rangeMask, double riverStrength, double lakeStrength)
    {
        if (height <= 0.0)
        {
            return "ocean";
        }

        if (lakeStrength > 0.45)
        {
            return "lake";
        }

        if (riverStrength > 0.52 && height < 1_250.0)
        {
            return "river";
        }

        if (height < 18.0)
        {
            return "coast";
        }

        if (height > 2_150.0)
        {
            return "alpine";
        }

        if (rangeMask > 0.45 && height > 900.0)
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

    private RiverSample SampleRiver(RiverPath river, double worldX, double worldY)
    {
        var nx = worldX / _worldWidthMeters * 2.0 - 1.0;
        var ny = worldY / _worldHeightMeters * 2.0 - 1.0;
        var bestDistance = double.MaxValue;
        var bestT = 0.0;
        var totalLength = 0.0;

        for (var i = 0; i < river.Points.Count - 1; i++)
        {
            totalLength += NormalizedDistance(river.Points[i], river.Points[i + 1]);
        }

        var traversed = 0.0;
        for (var i = 0; i < river.Points.Count - 1; i++)
        {
            var a = river.Points[i];
            var b = river.Points[i + 1];
            var segmentLength = NormalizedDistance(a, b);
            var vx = b.X - a.X;
            var vy = b.Y - a.Y;
            var lengthSquared = vx * vx + vy * vy;
            var t = lengthSquared <= 0.0 ? 0.0 : Clamp01(((nx - a.X) * vx + (ny - a.Y) * vy) / lengthSquared);
            var px = a.X + vx * t;
            var py = a.Y + vy * t;
            var distanceMeters = Distance(
                nx * _worldWidthMeters * 0.5,
                ny * _worldHeightMeters * 0.5,
                px * _worldWidthMeters * 0.5,
                py * _worldHeightMeters * 0.5);

            if (distanceMeters < bestDistance)
            {
                bestDistance = distanceMeters;
                bestT = totalLength <= 0.0 ? 0.0 : (traversed + segmentLength * t) / totalLength;
            }

            traversed += segmentLength;
        }

        return new RiverSample(bestDistance, Clamp01(bestT));
    }

    private double NormalizedDistance(NormalizedPoint a, NormalizedPoint b)
    {
        return Distance(
            a.X * _worldWidthMeters * 0.5,
            a.Y * _worldHeightMeters * 0.5,
            b.X * _worldWidthMeters * 0.5,
            b.Y * _worldHeightMeters * 0.5);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * Clamp01(t);
    private static double Clamp01(double value) => Clamp(value, 0.0, 1.0);
    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));
    private static NormalizedVector Normalize(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length <= 0.000001
            ? new NormalizedVector(1.0, 0.0)
            : new NormalizedVector(x / length, y / length);
    }
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

internal sealed record TerrainSample(double HeightMeters, string Biome);
internal sealed record IslandSeed(double X, double Y, double Radius);
internal sealed record HarborSite(string Name, double XMeters, double YMeters, double RadiusMeters, double Shelter);
internal sealed record MountainPeak(double X, double Y, double Radius, double HeightMeters, double Ruggedness, int NoiseOffset);
internal sealed record NormalizedPoint(double X, double Y);
internal sealed record NormalizedVector(double X, double Y);
internal sealed record LakeBasin(string Name, double XMeters, double YMeters, double RadiusMeters, double WaterHeightMeters);
internal sealed record RiverLakeCrossing(NormalizedPoint Center, NormalizedPoint Outlet, LakeBasin Basin);
internal sealed record RiverPath(string Name, double WidthMeters, double SourceHeightMeters, List<NormalizedPoint> Points);
internal sealed record RiverSample(double DistanceMeters, double T);

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
    public const double ChunkSizeMeters = 10_000.0;
    public const double MetersPerGodotUnit = 10.0;
    public const double MinHeightMeters = -250.0;
    public const double MaxHeightMeters = 3_600.0;
    public const int FormatVersion = 1;
}

internal sealed record WorldManifest(
    int FormatVersion,
    GeneratorInfo Generator,
    ScaleInfo Scale,
    HeightInfo Height,
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
internal sealed record BiomeInfo(string SampleFormat, string DecodeRule, List<BiomePaletteEntry> Palette);
internal sealed record BiomePaletteEntry(int Index, string Id);
internal sealed record ChunkGridInfo(int ChunksX, int ChunksY, int SamplesX, int SamplesY);
internal sealed record ChunkMetadata(int X, int Y, string HeightFile, string BiomeFile, string DominantBiome);
internal sealed record HarborMetadata(string Id, string Name, double XMeters, double YMeters, double RadiusMeters, double Shelter);
internal sealed record RiverMetadata(string Id, string Name, double WidthMeters, List<RiverPointMetadata> Points);
internal sealed record RiverPointMetadata(double XMeters, double YMeters);
internal sealed record LakeMetadata(string Id, string Name, double XMeters, double YMeters, double RadiusMeters, double WaterHeightMeters);
internal sealed record BiomeRegion(string Id, double ApproximateCoverage);
