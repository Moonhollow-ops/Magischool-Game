using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;
using Magischool.Sim.WorldGen;
using Xunit;

namespace Magischool.Sim.Tests;

public class GridAndMaterialTests
{
    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();

    [Fact]
    public void IndexAndCoordinatesRoundTrip()
    {
        var grid = new WorldGrid(7, 5, 3, Table);
        for (int i = 0; i < grid.Count; i++)
        {
            var (x, y, z) = grid.Coordinates(i);
            Assert.True(grid.InBounds(x, y, z));
            Assert.Equal(i, grid.Index(x, y, z));
        }
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(7, 0, 0)]
    [InlineData(0, 5, 0)]
    [InlineData(0, 0, 3)]
    public void OutOfBoundsIsRejected(int x, int y, int z)
    {
        Assert.False(new WorldGrid(7, 5, 3, Table).InBounds(x, y, z));
    }

    [Fact]
    public void AirIsIdZeroSoAnEmptyGridIsEmpty()
    {
        Assert.Equal(0, Table.Id(BuiltInMaterials.Air));
    }

    [Fact]
    public void WaterAndIcePhaseLinksAgree()
    {
        var water = Table[Table.Id(BuiltInMaterials.Water)];
        var ice = Table[Table.Id(BuiltInMaterials.Ice)];
        Assert.Equal(BuiltInMaterials.Ice, water.FreezesInto);
        Assert.Equal(BuiltInMaterials.Water, ice.MeltsInto);
        Assert.Equal(water.MeltingPoint, ice.MeltingPoint);
        // Water expands when it freezes; the burst-pipe chain depends on it.
        Assert.True(ice.Density < water.Density);
    }

    [Fact]
    public void TableRejectsBrokenLinksAndDuplicates()
    {
        var broken = BuiltInMaterials.All[1] with { MeltsInto = "magma" };
        Assert.Throws<ArgumentException>(() => new MaterialTable(new[] { BuiltInMaterials.All[0], broken }));
        Assert.Throws<ArgumentException>(() => new MaterialTable(new[] { BuiltInMaterials.All[0], BuiltInMaterials.All[0] }));
    }

    [Fact]
    public void CommandsOutsideTheGridAreIgnored()
    {
        var sim = TestRoomGenerator.Create("bounds", Table);
        ulong before = sim.StateHash();
        sim.Enqueue(new SetMaterial(-1, 0, 0, 1));
        sim.Enqueue(new SetTemperature(0, 0, 99, 1000f));
        sim.Step();

        var expected = TestRoomGenerator.Create("bounds", Table);
        expected.Step();
        Assert.Equal(expected.StateHash(), sim.StateHash());
        Assert.NotEqual(before, sim.StateHash()); // the tick itself advanced
    }

    [Fact]
    public void CommandsApplyAtTheStartOfTheNextTick()
    {
        var sim = TestRoomGenerator.Create("timing", Table);
        ushort iron = Table.Id(BuiltInMaterials.Iron);
        int i = sim.Grid.Index(0, 0, 3);

        sim.Enqueue(new SetMaterial(0, 0, 3, iron));
        Assert.NotEqual(iron, sim.Grid.Material[i]);
        sim.Step();
        Assert.Equal(iron, sim.Grid.Material[i]);
    }

    [Fact]
    public void TestRoomHasItsLandmarks()
    {
        var grid = TestRoomGenerator.Generate("landmarks", Table);
        int Count(string key)
        {
            ushort id = Table.Id(key);
            int n = 0;
            for (int i = 0; i < grid.Count; i++)
                if (grid.Material[i] == id) n++;
            return n;
        }

        Assert.True(Count(BuiltInMaterials.Lead) > 10);
        Assert.True(Count(BuiltInMaterials.Water) > 5);
        Assert.True(Count(BuiltInMaterials.Ice) > 0);
        Assert.True(Count(BuiltInMaterials.Wood) > 0);
        Assert.True(Count(BuiltInMaterials.Air) > grid.Count / 3);
    }
}
