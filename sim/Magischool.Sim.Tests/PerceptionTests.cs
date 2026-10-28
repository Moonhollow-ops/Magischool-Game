using System;
using System.Linq;
using Magischool.Sim.Bodies;
using Magischool.Sim.Core;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Magic;
using Magischool.Sim.Materials;
using Magischool.Sim.Perception;
using Magischool.Sim.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Magischool.Sim.Tests;

public class PerceptionTests
{
    private readonly ITestOutputHelper _out;
    public PerceptionTests(ITestOutputHelper output) => _out = output;

    private static readonly MaterialTable Table = BuiltInMaterials.CreateTable();
    private static readonly ushort Air = Table.Id(BuiltInMaterials.Air);
    private static readonly ushort Stone = Table.Id(BuiltInMaterials.Stone);
    private static readonly ushort Wood = Table.Id(BuiltInMaterials.Wood);
    private static readonly ushort Iron = Table.Id(BuiltInMaterials.Iron);
    private static readonly int SmokeGas = Table.GasIndexOf(Table.Id(BuiltInMaterials.Smoke));
    private const double Room = BuiltInMaterials.RoomTemperatureK;

    /// <summary>Still air over a stone floor, in an ordinary ether field.</summary>
    private static WorldGrid Ground(int width, int height, int depth)
    {
        var grid = new WorldGrid(width, height, depth, Table);
        grid.Fill(Air, Room);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grid.Set(x, y, 0, Stone, Room);
        Array.Fill(grid.EtherAmbient, 1e6);
        return grid;
    }

    /// <summary>A closed stone room, inside walls from (x0, y0) to (x1, y1), floor at z 1 and a roof over z = top.</summary>
    private static void StoneRoom(WorldGrid grid, int x0, int y0, int x1, int y1, int top)
    {
        for (int z = 1; z <= top + 1; z++)
        for (int y = y0 - 1; y <= y1 + 1; y++)
        for (int x = x0 - 1; x <= x1 + 1; x++)
        {
            bool inside = x >= x0 && x <= x1 && y >= y0 && y <= y1 && z <= top;
            if (!inside) grid.Set(x, y, z, Stone, Room);
        }
    }

    private static Simulation Start(WorldGrid grid, double skyLux = LightField.OvercastDayLux)
    {
        Atmosphere.Balance(grid, Air, Room);
        EtherSystem.Balance(grid);
        var sim = new Simulation("perception-test", grid, Table, StandardSystems.Create(openSky: true));
        sim.SkyIlluminanceLux = skyLux;
        return sim;
    }

    private static Body Spawn(Simulation sim, int x, int y, int z, int faceX = 0, int faceY = 1)
    {
        sim.Enqueue(new SpawnBody(x, y, z, BodyPlans.Human.Key));
        sim.Step();
        var body = sim.Bodies.Last();
        body.FacingX = faceX;
        body.FacingY = faceY;
        // What anyone glimpsed while the scene was being set up doesn't count.
        foreach (var b in sim.Bodies) b.Percepts.Clear();
        return body;
    }

    private static bool Saw(Body body, PerceptKind kind, long subject) =>
        body.Percepts.Any(p => p.Kind == kind && p.Subject == subject);

    private static Percept? Heard(Body body, SoundKind kind) =>
        body.Percepts.Where(p => p.Kind == PerceptKind.Sound && p.Detail == (byte)kind).Select(p => (Percept?)p).LastOrDefault();

    // ---------------------------------------------------------------- light

    [Fact]
    public void HotMatterGlowsAsABlackBodyDoes()
    {
        // A tungsten filament turns about 15 lm of each radiated watt into light, the sun about 93.
        Assert.InRange(Photometry.Efficacy(2800), 13.5, 16.5);
        Assert.InRange(Photometry.Efficacy(5800), 85, 100);
        // The Draper point: iron at 798 K gives a glow a dark-adapted eye can just see...
        double draper = LightField.SurfaceEmissivity * Photometry.LuminousExitance(798) / Math.PI;
        Assert.InRange(draper, 1e-3, 3e-2);
        Assert.True(PerceptionSystem.ThresholdLuminance(PerceptionSystem.DarkestAdaptation, 1e-2) < draper);
        // ...and nobody notices in daylight.
        Assert.True(PerceptionSystem.ThresholdLuminance(1000, 1e-2) > draper);
        Assert.Equal(0.0, Photometry.LuminousExitance(Room));
    }

    [Fact]
    public void TheSkyLightsOpenGroundAndARoofShadesIt()
    {
        var grid = Ground(30, 30, 8);
        StoneRoom(grid, 4, 4, 9, 9, 4);
        var sim = Start(grid);

        double open = LightField.Skylight(sim, 20.5, 20.5, 1.5);
        double closed = LightField.Skylight(sim, 6.5, 6.5, 1.5);
        _out.WriteLine($"open {open:F0} lux, closed room {closed:F0} lux");
        Assert.InRange(open, 9_000, 10_000);
        Assert.Equal(0.0, closed);

        // Open a doorway: daylight comes in, dimmer the further from it.
        for (int z = 1; z <= 3; z++) grid.Set(10, 6, z, Air, Room);
        double byDoor = LightField.Skylight(sim, 9.5, 6.5, 1.5);
        double back = LightField.Skylight(sim, 4.5, 6.5, 1.5);
        _out.WriteLine($"by the door {byDoor:F0} lux, back of the room {back:F0} lux");
        Assert.True(byDoor > 100 && byDoor < 0.5 * open);
        Assert.True(back < byDoor);
    }

    [Fact]
    public void AFireLightsTheDarkByTheInverseSquareAndSmokeDimsIt()
    {
        var grid = Ground(40, 12, 6);
        int log = grid.Index(5, 5, 1);
        grid.Set(5, 5, 1, Wood, 600);
        grid.Flame[log] = 1 << 2; // its +x face is alight
        var sim = Start(grid, skyLux: 0.0);

        var lights = LightField.AllSources(sim);
        var flame = Assert.Single(lights);
        Assert.Equal(LightKind.Flame, flame.Kind);
        // 45 kW of burning oak, 30% of it radiated by soot at 1400 K.
        _out.WriteLine($"flame {flame.Candela:F1} cd, luminance {flame.Luminance:F0} cd/m², area {flame.AreaM2:F3} m²");
        double at2 = LightField.Illuminance(sim, lights, flame.X + 4, flame.Y, flame.Z);
        double at4 = LightField.Illuminance(sim, lights, flame.X + 8, flame.Y, flame.Z);
        _out.WriteLine($"2 m: {at2:F2} lux, 4 m: {at4:F2} lux");
        Assert.InRange(at2, 3, 30);
        Assert.Equal(4.0, at2 / at4, 1e-6);

        // A gram of soot in each tile between: 0.07 g/L, and the light all but goes.
        for (int x = 8; x <= 12; x++) grid.GasMass[SmokeGas][grid.Index(x, 5, 1)] = 1e-3;
        double smoky = LightField.Illuminance(sim, lights, flame.X + 8, flame.Y, flame.Z);
        _out.WriteLine($"through smoke: {smoky:E2} lux");
        Assert.True(smoky < 1e-3 * at4);
    }

    [Fact]
    public void AWallCastsAShadow()
    {
        var grid = Ground(20, 12, 6);
        grid.Set(5, 5, 1, Iron, 1100);
        for (int z = 1; z <= 3; z++) grid.Set(8, 5, z, Stone, Room);
        var sim = Start(grid, skyLux: 0.0);
        var lights = LightField.AllSources(sim);
        Assert.NotEmpty(lights);
        Assert.All(lights, l => Assert.Equal(LightKind.Glow, l.Kind));
        double lit = LightField.Illuminance(sim, lights, 5.5, 8.5, 1.5);
        double shaded = LightField.Illuminance(sim, lights, 11.5, 5.5, 1.5);
        _out.WriteLine($"beside {lit:E3} lux, behind the wall {shaded:E3} lux");
        Assert.True(lit > 0.1);
        Assert.Equal(0.0, shaded);
    }

    // ---------------------------------------------------------------- sight

    [Fact]
    public void StudentsSeeEachOtherByDayButNotInAClosedDarkRoom()
    {
        var grid = Ground(40, 20, 8);
        var sim = Start(grid);
        var a = Spawn(sim, 5, 10, 1, faceX: 1, faceY: 0);
        var b = Spawn(sim, 25, 10, 1, faceX: 1, faceY: 0);
        sim.Step();
        Assert.True(Saw(a, PerceptKind.Body, b.Id), "a, facing b 10 m off by day, saw nothing");
        // b faces away from a.
        Assert.False(Saw(b, PerceptKind.Body, a.Id));

        var dark = Ground(20, 20, 8);
        StoneRoom(dark, 2, 2, 15, 15, 5);
        var night = Start(dark);
        var c = Spawn(night, 4, 8, 1, faceX: 1, faceY: 0);
        var d = Spawn(night, 12, 8, 1);
        night.Step(10);
        Assert.False(Saw(c, PerceptKind.Body, d.Id), "saw in a closed room with no light at all");

        // A fire lit in the room: now they can be seen by its light.
        int log = dark.Index(10, 10, 1);
        dark.Set(10, 10, 1, Wood, 650);
        night.Activity.WakeAll();
        night.Step(10);
        Assert.NotEqual(0, dark.Flame[log]);
        Assert.True(Saw(c, PerceptKind.Body, d.Id), "didn't see by firelight");
        Assert.Contains(c.Percepts, p => p.Kind == PerceptKind.Fire);
    }

    [Fact]
    public void AWallHidesAStudentAndSmokeDoesToo()
    {
        var grid = Ground(30, 20, 8);
        var sim = Start(grid);
        for (int z = 1; z <= 5; z++)
        for (int y = 7; y <= 13; y++)
            grid.Set(10, y, z, Stone, Room);
        var a = Spawn(sim, 5, 10, 1, faceX: 1, faceY: 0);
        var b = Spawn(sim, 15, 10, 1);
        sim.Step();
        Assert.False(Saw(a, PerceptKind.Body, b.Id), "saw through a stone wall");

        // Wall gone, and smoke between them instead: 0.3 g of soot in every tile of the gap.
        for (int z = 1; z <= 5; z++)
        for (int y = 7; y <= 13; y++)
            grid.Set(10, y, z, Air, Room);
        sim.Activity.WakeAll();
        sim.Step();
        Assert.True(Saw(a, PerceptKind.Body, b.Id), "didn't see across clear air");
        a.Percepts.Clear();
        for (int x = 6; x <= 14; x++)
        for (int z = 1; z <= 5; z++)
        for (int y = 7; y <= 13; y++)
            grid.GasMass[SmokeGas][grid.Index(x, y, z)] = 3e-4;
        sim.Step();
        Assert.False(Saw(a, PerceptKind.Body, b.Id), "saw through thick smoke");
    }

    [Fact]
    public void AnEyeComingInFromTheLightIsBlindUntilItAdapts()
    {
        var grid = Ground(30, 20, 8);
        StoneRoom(grid, 12, 2, 26, 16, 5);
        // A doorway in the room's west wall, and a smouldering log deep inside.
        for (int z = 1; z <= 4; z++) grid.Set(11, 9, z, Air, Room);
        grid.Set(24, 4, 1, Wood, 650);
        var sim = Start(grid, skyLux: LightField.OvercastDayLux);
        var student = Spawn(sim, 5, 9, 1, faceX: 1, faceY: 0);
        sim.Step(20);
        double outdoors = student.Adaptation;
        // Into the dark room, at once (as if stepping in), and look about.
        student.X = 20; student.Y = 9;
        sim.Step(1);
        double justIn = student.Adaptation;
        sim.Step(10 * 60 * Simulation.TicksPerSecond);
        double later = student.Adaptation;
        _out.WriteLine($"adapted to {outdoors:E2} cd/m² outside, {justIn:E2} on coming in, {later:E2} ten minutes later");
        Assert.True(outdoors > 100);
        Assert.True(justIn > 0.5 * outdoors);
        Assert.True(later < 1e-2 * outdoors);
    }

    [Fact]
    public void AWitnessSeesAFireballFlyAndHearsItStrike()
    {
        var grid = Ground(60, 20, 8);
        for (int z = 1; z <= 5; z++)
        for (int y = 0; y < 20; y++)
            grid.Set(55, y, z, Stone, Room);
        var sim = Start(grid, skyLux: LightField.StarlightLux);
        var witness = Spawn(sim, 30, 4, 1);
        sim.Enqueue(new CastFireball(10, 10, 2, 1, 0, 0, 1_000_000));
        sim.Step(30);

        Assert.Empty(sim.Projectiles);
        Assert.True(witness.Percepts.Any(p => p.Kind == PerceptKind.Bolt), "the bolt flew past unseen");
        var blast = Heard(witness, SoundKind.Blast);
        Assert.NotNull(blast);
        _out.WriteLine($"the strike was {blast!.Value.Level:F0} dB(A) at the witness");
        Assert.InRange(blast.Value.Level, 100, 160);
    }

    // ---------------------------------------------------------------- hearing

    [Fact]
    public void FootstepsCarryInTheOpenButNotThroughAStoneWall()
    {
        var grid = Ground(40, 20, 8);
        var sim = Start(grid);
        var walker = Spawn(sim, 10, 10, 1);
        var near = Spawn(sim, 18, 10, 1);
        var behind = Spawn(sim, 10, 3, 1);
        for (int z = 1; z <= 7; z++)
        for (int x = 0; x < 40; x++)
        for (int y = 5; y <= 6; y++)
            grid.Set(x, y, z, Stone, Room);
        sim.Activity.WakeAll();
        sim.Enqueue(new MoveBody(walker.Id, 1, 0));
        sim.Step();
        var step = Heard(near, SoundKind.Footstep);
        Assert.NotNull(step);
        _out.WriteLine($"a step 3.5 m off: {step!.Value.Level:F1} dB(A), {step.Value.Strength:F1} dB over the threshold");
        Assert.InRange(step.Value.Level, 30, 60);
        Assert.Null(Heard(behind, SoundKind.Footstep));
        Assert.Null(Heard(walker, SoundKind.Footstep));
    }

    [Fact]
    public void SoundBendsRoundTheEndOfAWallButLosesMuch()
    {
        // A stone wall in the open, as tall as the world, running from the edge to y = 25. One
        // listener has a clear line to the bang; the other, nearer, is behind the wall and hears
        // it only round its end, 12 m by that way against 5 m straight through the stone.
        var grid = Ground(40, 40, 6);
        for (int z = 1; z < 6; z++)
        for (int y = 0; y <= 25; y++)
            grid.Set(20, y, z, Stone, Room);
        var sim = Listening(grid, new SoundEvery(SoundKind.Blast, 15.5, 15.5, 2.5, 10.0));
        sim.Enqueue(new SpawnBody(15, 27, 1, BodyPlans.Human.Key));
        sim.Enqueue(new SpawnBody(25, 15, 1, BodyPlans.Human.Key));
        sim.Step();
        var inLine = Heard(sim.Bodies[0], SoundKind.Blast);
        var behind = Heard(sim.Bodies[1], SoundKind.Blast);
        Assert.NotNull(inLine);
        Assert.NotNull(behind);
        _out.WriteLine($"in line 6 m off {inLine!.Value.Level:F1} dB(A), behind the wall 5 m off {behind!.Value.Level:F1} dB(A)");
        Assert.True(behind.Value.Level < inLine.Value.Level - 15);
        // Not through the stone: that would be some 70 dB down.
        Assert.True(behind.Value.Level > inLine.Value.Level - 45);
    }

    [Fact]
    public void ABangRingsLouderInAStoneRoomThanInTheOpen()
    {
        double Level(bool room)
        {
            var grid = Ground(40, 40, 8);
            if (room) StoneRoom(grid, 10, 10, 20, 20, 5);
            var sim = Listening(grid, new SoundEvery(SoundKind.Blast, 12.5, 15.5, 2.5, 1.0));
            sim.Enqueue(new SpawnBody(18, 15, 1, BodyPlans.Human.Key));
            sim.Step();
            var heard = Heard(sim.Bodies[0], SoundKind.Blast);
            Assert.NotNull(heard);
            return heard!.Value.Level;
        }
        double open = Level(false), room = Level(true);
        _out.WriteLine($"3 m from a bang: {open:F1} dB(A) in the open, {room:F1} dB(A) in a stone room");
        Assert.True(room > open + 6);
    }

    [Fact]
    public void ASleeperWakesToABangButNotToAFootstep()
    {
        var grid = Ground(40, 20, 8);
        var sim = Start(grid, skyLux: 0.0);
        var sleeper = Spawn(sim, 10, 10, 1);
        var walker = Spawn(sim, 14, 10, 1);
        sleeper.SleepPressure = 0.8;
        sim.Enqueue(new SetAsleep(sleeper.Id, true));
        sim.Step();
        Assert.True(sleeper.Asleep);
        sim.Enqueue(new MoveBody(walker.Id, 1, 0));
        sim.Step(5);
        Assert.True(sleeper.Asleep, "woken by a footstep");
        Assert.Null(Heard(sleeper, SoundKind.Footstep));

        sim.Enqueue(new CastFireball(30, 15, 2, 1, 0, 0, 200_000));
        sim.Step(20);
        Assert.False(sleeper.Asleep, "slept through a fireball striking 15 m away");
        Assert.NotNull(Heard(sleeper, SoundKind.Blast));
    }

    [Fact]
    public void OneEarHearsThreeDecibelsWorseAndNoneHearsNothing()
    {
        double Margin(int earsLost)
        {
            var grid = Ground(30, 20, 8);
            var sim = Listening(grid, new SoundEvery(SoundKind.Crack, 15.5, 10.5, 1.5, 1e-4));
            sim.Enqueue(new SpawnBody(5, 10, 1, BodyPlans.Human.Key));
            sim.Step();
            sim.Bodies[0].Percepts.Clear();
            var body = sim.Bodies[0];
            foreach (var name in new[] { "left ear", "right ear" }.Take(earsLost))
            {
                int p = body.Anatomy.IndexOf(name);
                for (int n = body.Anatomy.LayerStart[p]; n < body.Anatomy.LayerStart[p + 1]; n++) body.Burn[n] = 50.0;
            }
            sim.Step();
            var heard = Heard(body, SoundKind.Crack);
            return heard?.Strength ?? double.NegativeInfinity;
        }
        double two = Margin(0), one = Margin(1), none = Margin(2);
        _out.WriteLine($"margin with two ears {two:F2} dB, one {one:F2} dB, none {none}");
        // Over the background of a quiet room the loss counts for less than it does in silence.
        Assert.True(two > one && two - one <= 3.02);
        Assert.True(double.IsNegativeInfinity(none));
    }

    [Fact]
    public void AFireMasksAFootstep()
    {
        var grid = Ground(30, 20, 8);
        var sim = Start(grid, skyLux: 0.0);
        var listener = Spawn(sim, 10, 10, 1);
        var walker = Spawn(sim, 14, 10, 1);
        sim.Enqueue(new MoveBody(walker.Id, 1, 0));
        sim.Step(2);
        Assert.NotNull(Heard(listener, SoundKind.Footstep));

        listener.Percepts.Clear();
        for (int x = 9; x <= 11; x++) grid.Set(x, 12, 1, Wood, 650);
        sim.Activity.WakeAll();
        sim.Step(20);
        Assert.NotNull(Heard(listener, SoundKind.Fire));
        sim.Enqueue(new MoveBody(walker.Id, -1, 0));
        sim.Step(2);
        Assert.Null(Heard(listener, SoundKind.Footstep));
    }

    // ---------------------------------------------------------------- state

    [Fact]
    public void WhatWasNoticedIsSavedAndReplaysIdentically()
    {
        var grid = Ground(30, 20, 8);
        grid.Set(20, 15, 1, Wood, 650);
        var live = Start(grid, skyLux: LightField.FullMoonLux);
        var a = Spawn(live, 5, 5, 1, faceX: 1, faceY: 1);
        Spawn(live, 12, 8, 1);
        live.Enqueue(new CastFireball(2, 2, 2, 1, 1, 0, 500_000));
        live.Enqueue(new FaceBody(a.Id, 1, 0));
        live.Step(40);
        Assert.NotEmpty(a.Percepts);

        var restored = Snapshot.FromBytes(Snapshot.ToBytes(live), Table, StandardSystems.Create(openSky: true));
        Assert.Equal(live.StateHash(), restored.StateHash());
        Assert.Equal(a.Percepts.Count, restored.Bodies[0].Percepts.Count);

        live.Enqueue(new SetSkyLight(LightField.StarlightLux));
        restored.Enqueue(new SetSkyLight(LightField.StarlightLux));
        live.Step(100);
        restored.Step(100);
        Assert.Equal(live.StateHash(), restored.StateHash());
    }

    [Fact]
    public void PerceptionIsTheSameOnOneThreadAsOnMany()
    {
        ulong Run(int threads)
        {
            var grid = Ground(40, 30, 8);
            StoneRoom(grid, 20, 5, 35, 25, 4);
            for (int z = 1; z <= 3; z++) grid.Set(19, 15, z, Air, Room);
            grid.Set(30, 10, 1, Wood, 650);
            var sim = Start(grid, skyLux: 50);
            sim.Parallelism = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads };
            Spawn(sim, 5, 15, 1, faceX: 1, faceY: 0);
            Spawn(sim, 25, 15, 1, faceX: -1, faceY: 0);
            sim.Enqueue(new CastFireball(5, 20, 2, 1, 0, 0, 300_000));
            sim.Step(200);
            return sim.StateHash();
        }
        Assert.Equal(Run(1), Run(4));
    }

    /// <summary>Only a sound and the ears, for hearing tests without the rest of the physics.</summary>
    private static Simulation Listening(WorldGrid grid, ISimSystem sound)
    {
        Atmosphere.Balance(grid, Air, Room);
        return new Simulation("hearing", grid, Table, new[] { sound, new PerceptionSystem() }) { SkyIlluminanceLux = 0.0 };
    }

    /// <summary>A system that makes the same sound every tick.</summary>
    private sealed class SoundEvery : ISimSystem
    {
        private readonly SoundKind _kind;
        private readonly double _x, _y, _z, _joules;
        public SoundEvery(SoundKind kind, double x, double y, double z, double joules) => (_kind, _x, _y, _z, _joules) = (kind, x, y, z, joules);
        public string Name => "sound";
        public void Update(Simulation sim) => Acoustics.Emit(sim, _kind, _x, _y, _z, _joules, -1);
    }
}
