namespace Ro3Emu.Server.Maps;

/// <summary>
/// The client's walkability grid of one map (<c>*_dynamicmapdata.bytes</c>): 1 m cells, world x,z =
/// cell index, each cell walkable or not with a height; a few maps carry extra height layers. Little-endian, uncompressed:
/// a 12 x int32 header, a block table (30x30-cell blocks), 4 bytes per cell (byte0 bit7 walkable, int16 height in cm)
/// and 8-byte extra layers {cell, u16 x, u16 z}.
/// </summary>
public sealed class NavGrid
{
    /// <summary>The client accepts a walkable layer within this height of the queried y (SceneSpaceDynamic._isStaticWalkable).</summary>
    public const float WalkHeightTolerance = 2f;

    private const int HeaderSize = 48;
    /// <summary>Weighted A*: the route is a little longer than the shortest one, found with far fewer expanded cells (monsters plan every few seconds).</summary>
    private const float HeuristicWeight = 2f;
    private readonly byte[] _data;
    private readonly int _blockSize;
    private readonly int _blocksX;

    public int Width { get; }
    public int Height { get; }

    private NavGrid(byte[] data)
    {
        _data = data;
        Width = BitConverter.ToInt32(data, 20);
        Height = BitConverter.ToInt32(data, 24);
        _blockSize = BitConverter.ToInt32(data, 28);
        _blocksX = (Width + _blockSize - 1) / _blockSize;
    }

    public static NavGrid Load(string path) => new(File.ReadAllBytes(path));

    /// <summary>Height layers of cell floor(x), floor(z): (walkable, height in m).</summary>
    public IEnumerable<(bool Walkable, float Height)> Layers(float x, float z)
    {
        int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
        if (cx < 0 || cz < 0 || cx >= Width || cz >= Height) yield break;
        int block = cz / _blockSize * _blocksX + cx / _blockSize;
        int entry = HeaderSize + block * 16;
        int normalPos = BitConverter.ToInt32(_data, entry);
        int otherPos = BitConverter.ToInt32(_data, entry + 8);
        int otherCount = BitConverter.ToInt32(_data, entry + 12);
        int cell = normalPos + (cz % _blockSize * _blockSize + cx % _blockSize) * 4;
        yield return ((_data[cell] & 0x80) != 0, BitConverter.ToInt16(_data, cell + 2) * 0.01f);
        for (int k = 0; k < otherCount; k++)
        {
            int p = otherPos + k * 8;
            if (BitConverter.ToUInt16(_data, p + 4) == cx && BitConverter.ToUInt16(_data, p + 6) == cz)
                yield return ((_data[p] & 0x80) != 0, BitConverter.ToInt16(_data, p + 2) * 0.01f);
        }
    }

    public bool IsWalkable(float x, float y, float z) =>
        Layers(x, z).Any(l => l.Walkable && MathF.Abs(l.Height - y) <= WalkHeightTolerance);

    /// <summary>Height of the walkable layer nearest to <paramref name="yHint"/>, or null when the cell is not walkable.</summary>
    public float? WalkHeight(float x, float z, float yHint)
    {
        float? best = null;
        foreach (var (walkable, h) in Layers(x, z))
            if (walkable && (best == null || MathF.Abs(h - yHint) < MathF.Abs(best.Value - yHint))) best = h;
        return best;
    }

    /// <summary>
    /// A route around obstacles from a to b: A* over the 1 m cells (8 neighbours, no corner cutting, a step up or down of at
    /// most <see cref="WalkHeightTolerance"/>), then smoothed so that each waypoint is reached by one straight
    /// <see cref="CanWalk"/> segment. Returns the waypoints after a (the last one is b itself when it is reachable), an empty
    /// list when a and b share a cell, or null when nothing is found within <paramref name="maxNodes"/> expanded cells (when
    /// the cap is hit the route to the explored cell closest to b is returned instead, if it gets nearer to b).
    /// </summary>
    public List<(float X, float Z)>? FindPath(float ax, float ay, float az, float bx, float bz, int maxNodes = 800)
    {
        int sx = (int)MathF.Floor(ax), sz = (int)MathF.Floor(az), gx = (int)MathF.Floor(bx), gz = (int)MathF.Floor(bz);
        if (sx == gx && sz == gz) return new();
        if (gx < 0 || gz < 0 || gx >= Width || gz >= Height || sx < 0 || sz < 0 || sx >= Width || sz >= Height) return null;
        int Key(int x, int z) => z * Width + x;
        float H(int x, int z) { float dx = MathF.Abs(x - gx), dz = MathF.Abs(z - gz); return MathF.Max(dx, dz) + 0.414f * MathF.Min(dx, dz); }

        var g = new Dictionary<int, float> { [Key(sx, sz)] = 0 };
        var height = new Dictionary<int, float> { [Key(sx, sz)] = ay };
        var parent = new Dictionary<int, int>();
        var open = new PriorityQueue<(int X, int Z), float>();
        open.Enqueue((sx, sz), H(sx, sz));
        var closed = new HashSet<int>();
        int best = Key(sx, sz); float bestH = H(sx, sz);
        int goal = -1, expanded = 0;
        ReadOnlySpan<(int, int)> steps = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
        bool Open(int x, int z, float y, out float h)
        {
            h = 0;
            if (x < 0 || z < 0 || x >= Width || z >= Height) return false;
            if (WalkHeight(x + 0.5f, z + 0.5f, y) is not { } floor || MathF.Abs(floor - y) > WalkHeightTolerance) return false;
            h = floor;
            return true;
        }
        while (open.TryDequeue(out var cur, out _))
        {
            int ck = Key(cur.X, cur.Z);
            if (!closed.Add(ck)) continue;
            if (cur.X == gx && cur.Z == gz) { goal = ck; break; }
            if (++expanded > maxNodes) break;
            float cy = height[ck], cg = g[ck];
            foreach (var (dx, dz) in steps)
            {
                int nx = cur.X + dx, nz = cur.Z + dz, nk = Key(nx, nz);
                if (nx < 0 || nz < 0 || nx >= Width || nz >= Height || closed.Contains(nk)) continue;
                if (!Open(nx, nz, cy, out float ny)) continue;
                if (dx != 0 && dz != 0 && (!Open(cur.X + dx, cur.Z, cy, out _) || !Open(cur.X, cur.Z + dz, cy, out _))) continue;
                float ng = cg + (dx != 0 && dz != 0 ? 1.414f : 1f);
                if (g.TryGetValue(nk, out float old) && old <= ng) continue;
                g[nk] = ng; height[nk] = ny; parent[nk] = ck;
                float h = H(nx, nz);
                if (h < bestH) { bestH = h; best = nk; }
                open.Enqueue((nx, nz), ng + HeuristicWeight * h);
            }
        }
        int end = goal >= 0 ? goal : best;
        if (goal < 0 && (end == Key(sx, sz) || H(sx, sz) - bestH < 1f)) return null;   // not found and no nearer cell
        var cells = new List<int>();
        for (int k = end; ; k = parent[k]) { cells.Add(k); if (k == Key(sx, sz)) break; }
        cells.Reverse();                                                                // start .. end

        // String pulling: from the current point, the farthest following cell that one straight segment reaches.
        var route = new List<(float X, float Z)>();
        float px = ax, py = ay, pz = az;
        int i = 1;
        while (i < cells.Count)
        {
            int far = i;
            for (int j = Math.Min(cells.Count - 1, i + 24); j > i; j--)
            {
                float jx = cells[j] % Width + 0.5f, jz = cells[j] / Width + 0.5f;
                if (CanWalk(px, py, pz, jx, jz)) { far = j; break; }
            }
            bool last = far == cells.Count - 1 && goal >= 0;
            float wx = last ? bx : cells[far] % Width + 0.5f, wz = last ? bz : cells[far] / Width + 0.5f;
            if (last && !CanWalk(px, py, pz, wx, wz)) { wx = cells[far] % Width + 0.5f; wz = cells[far] / Width + 0.5f; }
            route.Add((wx, wz));
            py = height[cells[far]]; px = wx; pz = wz;
            i = far + 1;
        }
        return route;
    }

    /// <summary>
    /// Whether the straight 2D segment from a to b crosses only walkable cells (sampled every half metre, heights
    /// followed cell by cell like the client's direct-walk check).
    /// </summary>
    public bool CanWalk(float ax, float ay, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.5f));
        float y = ay;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps, x = ax + dx * t, z = az + dz * t;
            if (WalkHeight(x, z, y) is not { } h || MathF.Abs(h - y) > WalkHeightTolerance) return false;
            y = h;
        }
        return true;
    }
}
