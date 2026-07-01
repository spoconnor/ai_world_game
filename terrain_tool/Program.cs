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
        Console.WriteLine("  --samples <count>     Height samples per chunk edge. Default: 129");
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

    public IslandWorldGenerator(ToolOptions options)
    {
        _options = options;
        _worldWidthMeters = options.ChunksX * ChunkSizeMeters;
        _worldHeightMeters = options.ChunksY * ChunkSizeMeters;
        _smallIslands = BuildSmallIslands(options.Seed);
        _harborSites = BuildHarborSites(options.Seed);
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
                    dominantBiome));
            }
        }

        return new WorldManifest(
            FormatVersion,
            new GeneratorInfo("WorldTerrainTool", _options.Seed, "island_archipelago_v1"),
            new ScaleInfo(MetersPerGodotUnit, ChunkSizeMeters, _worldWidthMeters, _worldHeightMeters),
            new HeightInfo(MinHeightMeters, MaxHeightMeters, "uint16_little_endian", "height = min_height + sample / 65535.0 * (max_height - min_height)"),
            new ChunkGridInfo(_options.ChunksX, _options.ChunksY, _options.SamplesPerChunk, _options.SamplesPerChunk),
            chunkMetadata,
            BuildHarbors(),
            BuildBiomeRegions(biomeCounts));
    }

    private string GenerateChunk(int chunkX, int chunkY, string chunksDirectory, Dictionary<string, int> biomeCounts)
    {
        var samples = _options.SamplesPerChunk;
        var biomeVotes = new Dictionary<string, int>(StringComparer.Ordinal);
        var filePath = Path.Combine(chunksDirectory, $"chunk_x{chunkX:000}_y{chunkY:000}.height");

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream);

        for (var localY = 0; localY < samples; localY++)
        {
            for (var localX = 0; localX < samples; localX++)
            {
                var worldX = (chunkX + localX / (double)(samples - 1)) * ChunkSizeMeters;
                var worldY = (chunkY + localY / (double)(samples - 1)) * ChunkSizeMeters;
                var terrain = EvaluateTerrain(worldX, worldY);
                writer.Write(ToSample(terrain.HeightMeters));

                if (localX % 16 == 0 && localY % 16 == 0)
                {
                    biomeVotes[terrain.Biome] = biomeVotes.GetValueOrDefault(terrain.Biome) + 1;
                    biomeCounts[terrain.Biome] = biomeCounts.GetValueOrDefault(terrain.Biome) + 1;
                }
            }
        }

        return biomeVotes.Count == 0
            ? "ocean"
            : biomeVotes.MaxBy(pair => pair.Value).Key;
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

        var lowlandNoise = Noise.Fractal(nx * 5.0, ny * 5.0, _options.Seed + 301, 5, 0.5);
        var detailNoise = Noise.Fractal(nx * 18.0, ny * 18.0, _options.Seed + 401, 4, 0.45);
        var coastalShelf = SmoothStep(0.08, 0.35, land);

        var height = -180.0
            + land * 320.0
            + coastalShelf * lowlandNoise * 260.0
            + coastalShelf * detailNoise * 90.0
            + ridge * 2_850.0;

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
        var moisture = Clamp01(0.55 + Noise.Fractal(nx * 4.0 - 2.0, ny * 4.0 + 5.0, _options.Seed + 503, 4, 0.5) * 0.32 - rangeMask * Math.Max(0.0, nx) * 0.35);
        var temperature = Clamp01(0.72 - Math.Abs(ny) * 0.25 - Math.Max(0.0, height) / 4_500.0);

        return new TerrainSample(Clamp(height, MinHeightMeters, MaxHeightMeters), ClassifyBiome(height, moisture, temperature, rangeMask));
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

internal sealed record TerrainSample(double HeightMeters, string Biome);
internal sealed record IslandSeed(double X, double Y, double Radius);
internal sealed record HarborSite(string Name, double XMeters, double YMeters, double RadiusMeters, double Shelter);

internal static class TerrainConstants
{
    public const int DefaultChunksX = 5;
    public const int DefaultChunksY = 5;
    public const int DefaultSamplesPerChunk = 129;
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
    ChunkGridInfo ChunkGrid,
    List<ChunkMetadata> Chunks,
    List<HarborMetadata> Harbors,
    List<BiomeRegion> BiomeRegions);

internal sealed record GeneratorInfo(string Tool, int Seed, string Algorithm);
internal sealed record ScaleInfo(double MetersPerGodotUnit, double ChunkSizeMeters, double WorldWidthMeters, double WorldHeightMeters);
internal sealed record HeightInfo(double MinHeight, double MaxHeight, string SampleFormat, string DecodeRule);
internal sealed record ChunkGridInfo(int ChunksX, int ChunksY, int SamplesX, int SamplesY);
internal sealed record ChunkMetadata(int X, int Y, string HeightFile, string DominantBiome);
internal sealed record HarborMetadata(string Id, string Name, double XMeters, double YMeters, double RadiusMeters, double Shelter);
internal sealed record BiomeRegion(string Id, double ApproximateCoverage);
