using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>NavGrid.FindPath on a synthetic 30 x 30 grid with a wall that has one opening (no game files needed).</summary>
public class NavPathTests : IDisposable
{
    private const int Size = 30;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-nav-" + Guid.NewGuid().ToString("N"));
    public NavPathTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    /// <summary>One 30 x 30 block, every cell walkable at height 0 except those <paramref name="blocked"/> says.</summary>
    private NavGrid Grid(Func<int, int, bool> blocked)
    {
        var bytes = new byte[48 + 16 + Size * Size * 4];
        BitConverter.GetBytes(Size).CopyTo(bytes, 20);      // width
        BitConverter.GetBytes(Size).CopyTo(bytes, 24);      // height
        BitConverter.GetBytes(Size).CopyTo(bytes, 28);      // block size
        BitConverter.GetBytes(48 + 16).CopyTo(bytes, 48);   // the block's cells follow the one-entry table
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                bytes[48 + 16 + (z * Size + x) * 4] = blocked(x, z) ? (byte)0 : (byte)0x80;
        string path = Path.Combine(_dir, "g" + Guid.NewGuid().ToString("N") + ".bytes");
        File.WriteAllBytes(path, bytes);
        return NavGrid.Load(path);
    }

    private static bool Wall(int x, int z) => x == 15 && z < 25;     // a wall across the grid with an opening at z 25..29

    [Fact]
    public void ARouteGoesAroundAWallThroughItsOpening()
    {
        var grid = Grid(Wall);
        Assert.False(grid.CanWalk(5.5f, 0, 5.5f, 25.5f, 5.5f));       // the straight line is blocked
        var route = grid.FindPath(5.5f, 0, 5.5f, 25.5f, 5.5f);
        Assert.NotNull(route);
        Assert.Equal((25.5f, 5.5f), route![^1]);                      // it ends at the goal itself
        Assert.Contains(route, p => p.Z >= 25f);                       // and passes through the opening
        float x = 5.5f, z = 5.5f;
        foreach (var p in route)                                       // every leg is one walkable straight segment
        {
            Assert.True(grid.CanWalk(x, 0, z, p.X, p.Z), $"leg ({x},{z}) -> ({p.X},{p.Z})");
            (x, z) = p;
        }
    }

    [Fact]
    public void AClearLineNeedsOneLegAndTheSameCellNone()
    {
        var grid = Grid(Wall);
        Assert.Equal([(5.5f, 20.5f)], grid.FindPath(5.5f, 0, 5.5f, 5.5f, 20.5f));
        Assert.Empty(grid.FindPath(5.2f, 0, 5.2f, 5.8f, 5.8f)!);
    }

    [Fact]
    public void WithoutAnOpeningTheRouteStopsAtTheWall()
    {
        var closed = Grid((x, z) => x == 15);                         // the wall closes the grid
        var route = closed.FindPath(5.5f, 0, 5.5f, 25.5f, 5.5f, maxNodes: 2000);
        Assert.NotNull(route);
        Assert.All(route!, p => Assert.True(p.X < 15f));              // nearest it can get, never through the wall
        Assert.True(route![^1].X > 13f);
    }

    [Fact]
    public void TheSearchIsBoundedByMaxNodes()
    {
        var grid = Grid(Wall);
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) grid.FindPath(5.5f, 0, 5.5f, 25.5f, 5.5f, 800);
        Assert.True(started.Elapsed.TotalMilliseconds < 2000, $"200 searches took {started.Elapsed.TotalMilliseconds:0} ms");
    }
}
