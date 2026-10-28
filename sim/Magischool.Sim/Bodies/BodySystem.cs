using System;
using Magischool.Sim.Core;
using Magischool.Sim.Fluids;
using Magischool.Sim.Materials;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Bodies;

/// <summary>
/// Layer 9: bodies. Each body in <see cref="Simulation.Bodies"/> is living tissue standing in a
/// column of tiles, and everything that happens to it is the physics of the layers below.
///
/// <para>
/// <b>Anatomy.</b> A body is a tree of parts (<see cref="Anatomy"/>): limbs down to the hands and
/// feet, a head with eyes and teeth, and organs inside the trunk and skull. Each part does its job
/// as well as it is whole (<see cref="Body.Health"/>): the heart pumps the blood, the lungs take
/// oxygen, the liver stores glycogen and with the kidneys clears poisons, the kidneys make urine,
/// the gut digests, the eyes see, the teeth chew, the legs walk and the spine carries the brain's
/// orders. A part torn off (<see cref="Sever"/>) leaves with everything attached through it, as a
/// body of its own.
/// </para>
/// <para>
/// <b>Heat.</b> Each part is concentric layers of tissue (skin, fat, muscle, bone; an organ its
/// own tissue), each with its own temperature. Heat conducts between layers, and blood carries it
/// between every layer and the core (the Pennes bioheat model). The skin trades heat with the
/// tile around it by convection and radiation through clothing, or by conduction with water or
/// rubble it is in; the soles of the feet with the floor; an organ with the part it sits in.
/// Whatever the body loses the tile gains.
/// </para>
/// <para>
/// <b>Regulation.</b> The brain reads the core and the skin and answers as Gagge's model does:
/// it opens the skin's blood vessels and sweats when warm, closes them and shivers when cold.
/// Sweat evaporates only as fast as the air around can take it (humid air takes less), and each
/// kilogram carries off its latent heat as steam into the tile.
/// </para>
/// <para>
/// <b>Metabolism.</b> Glycogen and fat burn with oxygen into carbon dioxide and water, with
/// their real chemistry, so a body uses up the oxygen of a sealed room and fills it with carbon
/// dioxide and moisture. Every breath takes the tile's gas as it is, keeps what oxygen the
/// blood can bind at that pressure (the haemoglobin curve), and gives it back warmed, moist and
/// richer in carbon dioxide. Food digests into glycogen and fat, water into the body; kidneys
/// make urine and the bladder empties onto the floor.
/// </para>
/// <para>
/// <b>Injury.</b> Tissue cooks as real tissue does (the Henriques damage integral: hours at
/// 44 °C, seconds at 60 °C), freezes with its latent heat (frostbite is the ice it made), and
/// boils dry past 100 °C. Falls and falling rubble put their kinetic energy into the parts they
/// hit and the organs inside them; past what each tissue absorbs they break it, and broken parts
/// bleed (onto the floor, or inside the body from an organ) until they clot. The brain lives on
/// its oxygen: drowned, smothered, starved of blood, with a stopped heart or a broken neck, it
/// has minutes. A heart stops when it is itself broken, the core is too hot or cold, too much
/// blood is lost, or poisons build up with the liver or kidneys failed; the body dies when its
/// brain does.
/// </para>
/// <para>
/// Everything is counted: a body's energy and mass (<see cref="Physiology"/>) plus the world's
/// never change, apart from what falls and climbs trade with gravity. Not yet: bodies don't take
/// up room in the tiles they stand in (air and water pass through them), and they hold no ether.
/// </para>
/// </summary>
public sealed class BodySystem : ISimSystem
{
    public string Name => "bodies";

    /// <summary>Gravitational potential energy bodies have given up by falling, less what they gained climbing, J.</summary>
    public double ReleasedPotentialJ { get; private set; }

    private const double Dt = Simulation.SecondsPerTick;
    private const double StefanBoltzmann = 5.670374419e-8;
    private const double SkinEmissivity = 0.95;

    /// <summary>Insulation of one clo of clothing, m²·K/W.</summary>
    public const double CloM2KPerW = 0.155;

    /// <summary>Convection from still air to a standing body, W/(m²·K), and from still water.</summary>
    public const double StillAirConvection = 3.1, StillWaterConvection = 300.0;

    /// <summary>Trauma at which a part is torn from the body: twenty-five times what breaks it.</summary>
    public const double SeverTrauma = 25.0;

    /// <summary>Blood a stump loses per kg of what was cut from it, kg/s, before it clots.</summary>
    public const double StumpBleedPerKg = 2e-3;

    /// <summary>Health below which a part can't do its job at all (a leg won't bear weight, a heart fibrillates).</summary>
    public const double FailedHealth = 0.3;

    /// <summary>Blood a fully broken part loses, kg/s, before it clots (over ten minutes).</summary>
    public const double BleedPerTrauma = 2e-3;
    public const double ClotSeconds = 600.0;

    /// <summary>Air breathed per watt of metabolism, kg/s: about 7 L/min at rest.</summary>
    public const double VentilationKgPerJ = 1.5e-6;

    /// <summary>How fast the blood gives its oxygen back to air poorer than it, s.</summary>
    public const double WashoutSeconds = 15.0;

    /// <summary>Seconds the brain survives with no oxygen at all.</summary>
    public const double BrainAnoxiaSeconds = 300.0;

    public void Update(Simulation sim)
    {
        if (sim.Bodies.Count == 0) return;
        var w = Physiology.For(sim.Materials);
        // Parts torn off this tick join the list at its end and start moving next tick.
        int count = sim.Bodies.Count;
        for (int b = 0; b < count; b++)
        {
            var body = sim.Bodies[b];
            var grid = sim.Grid;
            if (!grid.InBounds(body.X, body.Y, body.Z) || !grid.InBounds(body.X, body.Y, body.Z + body.HeightTiles - 1)) continue;
            Move(sim, w, body);
            Rubble(sim, w, body);
            TearOff(sim, w, body);
            if (body.Alive && !body.IsRemains) Live(sim, w, body);
            else { body.MetabolicW = 0.0; body.ShiverW = 0.0; body.SweatKgPerS = 0.0; body.OxygenSaturation = 0.0; }
            Exchange(sim, w, body);
            TissuePhase(sim, w, body);
            Vitals(body);
            sim.Activity.WakeAround(body.X, body.Y, body.Z);
            sim.Activity.WakeAround(body.X, body.Y, body.Z + body.HeightTiles - 1);
        }
    }

    // ---------------------------------------------------------------- where it stands

    private static int TileOf(WorldGrid grid, Body body, int part) => grid.Index(body.X, body.Y, body.Z + body.Anatomy.Parts[part].Dz);

    /// <summary>How much of a tile is solid, liquid and gas, by volume.</summary>
    private static (double Solid, double Liquid, double Gas) Shares(WorldGrid grid, int i)
    {
        double solid = 0.0, liquid = 0.0;
        if (grid.HasCondensed(i))
        {
            double fill = Math.Min(1.0, grid.FillFraction(i));
            if (grid.Materials[grid.Material[i]].Phase == Phase.Solid) solid = fill;
            else liquid = fill;
        }
        bool gas = grid.FreeVolume(i) >= FluidSystem.GasLockFraction * WorldGrid.TileVolumeM3 && grid.TotalGasMass(i) > WorldGrid.SeparateGasKg;
        return (solid, liquid, gas ? Math.Max(0.0, 1.0 - solid - liquid) : 0.0);
    }

    private static bool Blocked(WorldGrid grid, int x, int y, int z) =>
        !grid.InBounds(x, y, z) || Shares(grid, grid.Index(x, y, z)).Solid >= 0.5;

    /// <summary>Whether a body standing with its feet at (x, y, z) has nothing to fall into.</summary>
    private static bool Supported(WorldGrid grid, int x, int y, int z)
    {
        if (z == 0) return true;
        if (Shares(grid, grid.Index(x, y, z - 1)).Solid >= 0.5) return true;
        var feet = Shares(grid, grid.Index(x, y, z));
        if (feet.Solid >= 0.5) return true;
        // Afloat: a body sinks into deep water until its thighs are under.
        return feet.Liquid >= 0.5 && Shares(grid, grid.Index(x, y, z + 1)).Liquid >= 0.5;
    }

    /// <summary>A part a bolt flying into tile i would hit, the largest if several share it.</summary>
    public static bool TryPartAt(Simulation sim, int i, out Body hit, out int part)
    {
        var (x, y, z) = sim.Grid.Coordinates(i);
        hit = null!;
        part = -1;
        double best = 0.0;
        foreach (var body in sim.Bodies)
        {
            if (body.X != x || body.Y != y) continue;
            for (int p = 0; p < body.Anatomy.Parts.Length; p++)
            {
                var def = body.Anatomy.Parts[p];
                if (!def.Internal && body.Z + def.Dz == z && def.SkinAreaM2 > best)
                {
                    best = def.SkinAreaM2;
                    hit = body;
                    part = p;
                }
            }
        }
        return part >= 0;
    }

    /// <summary>A flash of heat landing on the outer layer of one part (a fireball's strike).</summary>
    public static void HeatSkin(Simulation sim, Body body, int part, double joules)
    {
        Physiology.Heat(body, body.Anatomy.LayerStart[part], joules);
        sim.Activity.WakeAround(body.X, body.Y, body.Z);
    }

    // ---------------------------------------------------------------- moving and falling

    private void Move(Simulation sim, Physiology.World w, Body body)
    {
        var grid = sim.Grid;
        int h = body.HeightTiles;
        double kg = Physiology.Kg(body);
        int dx = Math.Clamp(body.StepX, -1, 1), dy = Math.Clamp(body.StepY, -1, 1);
        body.StepX = body.StepY = 0;

        if ((dx != 0 || dy != 0) && body.Conscious && CanWalk(body) && body.KineticJ == 0.0
            && Supported(grid, body.X, body.Y, body.Z) && !Blocked(grid, body.X, body.Y, body.Z))
        {
            int nx = body.X + dx, ny = body.Y + dy;
            double stepM = WorldGrid.TileSizeM * Math.Sqrt(dx * dx + dy * dy);
            if (Clear(grid, nx, ny, body.Z, h))
            {
                body.X = nx; body.Y = ny;
                body.PendingWorkJ += WalkJPerKgM * kg * stepM;
            }
            else if (Clear(grid, nx, ny, body.Z + 1, h) && !Blocked(grid, body.X, body.Y, body.Z + h))
            {
                // A step up: the muscles lift the body at a quarter efficiency, the rest is heat.
                double lift = kg * FluidSystem.Gravity * WorldGrid.TileSizeM;
                body.X = nx; body.Y = ny; body.Z++;
                body.PendingWorkJ += WalkJPerKgM * kg * stepM + lift / MuscleEfficiency;
                TakeHeatFromMuscle(body, lift);
                ReleasedPotentialJ -= lift;
            }
        }

        if (!Supported(grid, body.X, body.Y, body.Z))
        {
            body.FallSpeed += FluidSystem.Gravity * Dt;
            body.FallTravel += body.FallSpeed * Dt;
            while (body.FallTravel >= WorldGrid.TileSizeM && !Supported(grid, body.X, body.Y, body.Z))
            {
                double released = kg * FluidSystem.Gravity * WorldGrid.TileSizeM;
                body.Z--;
                body.FallTravel -= WorldGrid.TileSizeM;
                body.KineticJ += released;
                ReleasedPotentialJ += released;
            }
            if (!Supported(grid, body.X, body.Y, body.Z)) return;
        }
        if (body.KineticJ > 0.0) Land(sim, body);
        body.FallSpeed = 0.0;
        body.FallTravel = 0.0;
    }

    /// <summary>Metabolic cost of walking, J per kg per metre, and muscle's efficiency at lifting.</summary>
    public const double WalkJPerKgM = 2.0, MuscleEfficiency = 0.25;

    private static bool Clear(WorldGrid grid, int x, int y, int z, int h)
    {
        if (!grid.InBounds(x, y, z) || !grid.InBounds(x, y, z + h - 1)) return false;
        for (int k = 0; k < h; k++)
            if (Blocked(grid, x, y, z + k)) return false;
        return true;
    }

    /// <summary>
    /// Whether its legs will carry it: the spine whole, and at least one foot sound with every part
    /// between it and the trunk (shin, thigh) sound too.
    /// </summary>
    public static bool CanWalk(Body body)
    {
        if (!body.SpineIntact) return false;
        var anatomy = body.Anatomy;
        for (int p = 0; p < anatomy.Parts.Length; p++)
        {
            if (anatomy.Parts[p].Function != PartFunction.Foot) continue;
            bool sound = true;
            for (int q = p; q >= 0 && anatomy.ParentOf[q] >= 0; q = anatomy.ParentOf[q])
                if (body.Health(q) < FailedHealth) { sound = false; break; }
            if (sound) return true;
        }
        return false;
    }

    private static void TakeHeatFromMuscle(Body body, double joules)
    {
        double capacity = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
            if (body.Anatomy.NodeTissue[n] == Tissue.Muscle) capacity += Physiology.Capacity(body, n);
        if (!(capacity > 0.0)) { Physiology.Heat(body, 0, -joules); return; }
        for (int n = 0; n < body.Mass.Length; n++)
            if (body.Anatomy.NodeTissue[n] == Tissue.Muscle) Physiology.Heat(body, n, -joules * Physiology.Capacity(body, n) / capacity);
    }

    /// <summary>The body hits the ground (or water) and its kinetic energy goes into whatever it lands on with.</summary>
    private static void Land(Simulation sim, Body body)
    {
        var grid = sim.Grid;
        double ke = body.KineticJ;
        body.KineticJ = 0.0;
        int feet = grid.Index(body.X, body.Y, body.Z);
        if (Shares(grid, feet).Liquid >= 0.5)
        {
            // Water takes most of a fall into it.
            double water = 0.85 * ke;
            EnergyAudit.AddJoules(grid, feet, water, 0.0);
            ke -= water;
        }
        // Shared among the outer parts as each takes the fall; the organs inside take theirs from them.
        var parts = body.Anatomy.Parts;
        double shares = 0.0;
        for (int p = 0; p < parts.Length; p++)
            if (!parts[p].Internal) shares += parts[p].ImpactShare;
        if (shares > 0.0)
        {
            for (int p = 0; p < parts.Length; p++)
                if (!parts[p].Internal) Impact(body, p, ke * parts[p].ImpactShare / shares);
        }
        else Physiology.Heat(body, 0, ke);
        sim.Activity.WakeAround(body.X, body.Y, body.Z);
    }

    /// <summary>
    /// A blow to one outer part: its energy spreads by mass through the part and the organs in it,
    /// becomes heat, and past what each tissue absorbs, injury (<see cref="TissueDef.ImpactToleranceJPerKg"/>).
    /// </summary>
    public static void Impact(Body body, int part, double joules)
    {
        if (!(joules > 0.0)) return;
        var anatomy = body.Anatomy;
        double kg = 0.0;
        foreach (int q in anatomy.WithOrgans(part)) kg += PartKg(body, q);
        if (!(kg > 0.0)) return;
        foreach (int q in anatomy.WithOrgans(part))
        {
            int first = anatomy.LayerStart[q], end = anatomy.LayerStart[q + 1];
            double partKg = 0.0, capacity = 0.0, tolerance = 0.0;
            for (int n = first; n < end; n++)
            {
                partKg += body.Mass[n];
                capacity += Physiology.Capacity(body, n);
                tolerance += body.Mass[n] * BodyPlans.Of(anatomy.NodeTissue[n]).ImpactToleranceJPerKg;
            }
            if (!(partKg > 0.0)) continue;
            double j = joules * partKg / kg;
            for (int n = first; n < end; n++) Physiology.Heat(body, n, j * Physiology.Capacity(body, n) / capacity);
            double before = body.Trauma[q];
            body.Trauma[q] += j / tolerance;
            // Bruising below a third; past it, torn vessels bleed.
            double torn = Math.Max(0.0, Math.Min(body.Trauma[q], 1.0) - Math.Max(before, 0.3));
            body.Bleed[q] += BleedPerTrauma * torn;
        }
    }

    /// <summary>Loose rubble falling into a tile the body is in strikes it, and its kinetic energy goes into the parts there.</summary>
    private static void Rubble(Simulation sim, Physiology.World w, Body body)
    {
        var grid = sim.Grid;
        var parts = body.Anatomy.Parts;
        for (int dz = body.HeightTiles - 1; dz >= 0; dz--)
        {
            int i = grid.Index(body.X, body.Y, body.Z + dz);
            if (!grid.HasCondensed(i) || grid.FallSpeed[i] <= 0.0 || (grid.Structure[i] & WorldGrid.LooseBit) == 0) continue;
            if (grid.Materials[grid.Material[i]].Phase != Phase.Solid) continue;
            double v = grid.FallSpeed[i];
            double ke = 0.5 * grid.Mass[i] * v * v;
            // The falling has already turned its potential energy into heat in the rubble; the blow takes it back out.
            ke = Math.Min(ke, Math.Max(0.0, grid.CondensedHeatCapacity(i) * (grid.Temperature[i] - 1.0)));
            grid.FallSpeed[i] = 0.0;
            grid.FallTravel[i] = 0.0;
            if (!(ke > 0.0)) continue;
            EnergyAudit.AddJoules(grid, i, -ke, 0.0);
            double kg = 0.0;
            for (int p = 0; p < parts.Length; p++)
                if (!parts[p].Internal && parts[p].Dz == dz) kg += BlockKg(body, p);
            if (!(kg > 0.0)) { EnergyAudit.AddJoules(grid, i, ke, 0.0); continue; }
            for (int p = 0; p < parts.Length; p++)
                if (!parts[p].Internal && parts[p].Dz == dz) Impact(body, p, ke * BlockKg(body, p) / kg);
        }
    }

    /// <summary>Mass of a part's own layers, kg.</summary>
    public static double PartKg(Body body, int part)
    {
        double kg = 0.0;
        for (int n = body.Anatomy.LayerStart[part]; n < body.Anatomy.LayerStart[part + 1]; n++) kg += body.Mass[n];
        return kg;
    }

    /// <summary>Mass of a part with the organs in it, kg.</summary>
    private static double BlockKg(Body body, int part)
    {
        double kg = 0.0;
        foreach (int q in body.Anatomy.WithOrgans(part)) kg += PartKg(body, q);
        return kg;
    }

    // ---------------------------------------------------------------- tearing apart

    private static void TearOff(Simulation sim, Physiology.World w, Body body)
    {
        // One at a time: each cut renumbers the parts.
        for (bool cut = true; cut;)
        {
            cut = false;
            var parts = body.Anatomy.Parts;
            for (int p = 0; p < parts.Length; p++)
            {
                if (parts[p].Internal || body.Anatomy.ParentOf[p] < 0 || body.Trauma[p] < SeverTrauma) continue;
                Sever(sim, body, p);
                cut = true;
                break;
            }
        }
    }

    /// <summary>
    /// Cuts a part from the body, with everything attached through it: it becomes a body of its
    /// own (remains, unless it took the brain), standing in the same column, and the stump bleeds.
    /// Every gram and joule goes with the tissue it is in; the fat's chemical energy is shared by
    /// fat. The piece with the root part keeps the body's id and its blood and stores. Returns
    /// the new body.
    /// </summary>
    public static Body Sever(Simulation sim, Body body, int part)
    {
        var anatomy = body.Anatomy;
        if (part < 0 || part >= anatomy.Parts.Length || anatomy.ParentOf[part] < 0)
            throw new ArgumentException("Only a part attached to another can be cut off.", nameof(part));
        var w = Physiology.For(sim.Materials);
        bool Off(int q) => anatomy.IsWithin(q, part);
        int stump = anatomy.ParentOf[part];
        while (anatomy.Parts[stump].Internal) stump = anatomy.ParentOf[stump];
        int lowOff = int.MaxValue, lowOn = int.MaxValue;
        double offKg = 0.0;
        for (int q = 0; q < anatomy.Parts.Length; q++)
        {
            if (Off(q)) { lowOff = Math.Min(lowOff, anatomy.Parts[q].Dz); offKg += PartKg(body, q); }
            else lowOn = Math.Min(lowOn, anatomy.Parts[q].Dz);
        }

        var offAnatomy = anatomy.Subset(Off, out var offMap);
        var onAnatomy = anatomy.Subset(q => !Off(q), out var onMap);

        var piece = new Body(sim.NextBodyId++, body.Plan, offAnatomy, body.X, body.Y, body.Z + lowOff);
        for (int q = 0; q < anatomy.Parts.Length; q++)
        {
            if (offMap[q] < 0) continue;
            int from = anatomy.LayerStart[q], to = offAnatomy.LayerStart[offMap[q]];
            for (int k = 0; k < anatomy.LayerStart[q + 1] - from; k++)
            {
                int a = from + k, b = to + k;
                piece.Mass[b] = body.Mass[a];
                piece.WaterKg[b] = body.WaterKg[a];
                piece.FrozenKg[b] = body.FrozenKg[a];
                piece.Temperature[b] = body.Temperature[a];
                piece.Burn[b] = body.Burn[a];
                piece.Frostbite[b] = body.Frostbite[a];
            }
            piece.Trauma[offMap[q]] = body.Trauma[q];
        }
        double lipid = Physiology.LipidKg(body), offLipid = Physiology.LipidKg(piece);
        double fatJ = lipid > 0.0 ? body.FatJ * offLipid / lipid : 0.0;
        piece.FatJ = fatJ;
        body.FatJ -= fatJ;
        // No blood goes with it: it drains from the stump.
        double heat = 0.0, kg = 0.0;
        for (int n = 0; n < piece.Mass.Length; n++) { heat += piece.Mass[n] * piece.Temperature[n]; kg += piece.Mass[n]; }
        piece.BloodTemperature = kg > 0.0 ? heat / kg : body.BloodTemperature;
        piece.HeartStopped = true;
        piece.Alive = !piece.IsRemains;
        piece.ClothingClo = body.ClothingClo;
        piece.SleepPressure = body.SleepPressure;
        piece.BrainHypoxia = body.BrainHypoxia;
        piece.KineticJ = 0.0;

        int[] Invert(int[] map, int length)
        {
            var inverse = new int[length];
            for (int q = 0; q < map.Length; q++)
                if (map[q] >= 0) inverse[map[q]] = q;
            return inverse;
        }
        var partMap = Invert(onMap, onAnatomy.Parts.Length);
        var nodeMap = new int[onAnatomy.NodeCount];
        for (int q = 0; q < onAnatomy.Parts.Length; q++)
            for (int k = onAnatomy.LayerStart[q]; k < onAnatomy.LayerStart[q + 1]; k++)
                nodeMap[k] = anatomy.LayerStart[partMap[q]] + (k - onAnatomy.LayerStart[q]);
        body.Resize(onAnatomy, nodeMap, partMap);
        body.Z += lowOn;
        body.Bleed[onMap[stump]] += StumpBleedPerKg * offKg;
        sim.Bodies.Add(piece);
        sim.Activity.WakeAround(body.X, body.Y, body.Z);
        return piece;
    }

    /// <summary>
    /// Changes the tissue of one layer of a part (a mutation, or a transmutation), keeping its mass,
    /// water and energy: its temperature moves to whatever holds the same energy in the new tissue.
    /// </summary>
    public static void ChangeTissue(Simulation sim, Body body, int part, int layer, Tissue tissue)
    {
        var w = Physiology.For(sim.Materials);
        var anatomy = body.Anatomy;
        var def = anatomy.Parts[part];
        if (layer < 0 || layer >= def.Layers.Length) throw new ArgumentOutOfRangeException(nameof(layer));
        int n = anatomy.LayerStart[part] + layer;
        double e = Physiology.NodeJoules(w, body, n);
        var layers = ( (Tissue Tissue, double Kg)[])def.Layers.Clone();
        layers[layer] = (tissue, layers[layer].Kg);
        var parts = (PartDef[])anatomy.Parts.Clone();
        parts[part] = def with { Layers = layers };
        var map = new int[anatomy.NodeCount];
        for (int k = 0; k < map.Length; k++) map[k] = k;
        var partMap = new int[parts.Length];
        for (int k = 0; k < partMap.Length; k++) partMap[k] = k;
        body.Resize(new Anatomy(parts), map, partMap);
        Physiology.AddMass(w, body, n, 0.0, e - Physiology.NodeJoules(w, body, n));
    }

    /// <summary>
    /// Attaches a new part, made of <paramref name="tissueKg"/> of fresh tissue as <paramref name="part"/>
    /// describes, to the body. The caller supplies the matter's energy, <paramref name="joules"/>;
    /// the part is made at the temperature that energy gives it. Returns its index.
    /// </summary>
    public static int Attach(Simulation sim, Body body, PartDef part, double joules)
    {
        var w = Physiology.For(sim.Materials);
        var anatomy = body.Anatomy;
        var grown = anatomy.With(part);
        var nodeMap = new int[grown.NodeCount];
        for (int k = 0; k < nodeMap.Length; k++) nodeMap[k] = k < anatomy.NodeCount ? k : -1;
        var partMap = new int[grown.Parts.Length];
        for (int k = 0; k < partMap.Length; k++) partMap[k] = k < anatomy.Parts.Length ? k : -1;
        body.Resize(grown, nodeMap, partMap);
        int p = grown.Parts.Length - 1;
        int first = grown.LayerStart[p], end = grown.LayerStart[p + 1];
        double kg = 0.0;
        for (int n = first; n < end; n++) kg += grown.InitialMass[n];
        for (int n = first; n < end; n++)
        {
            var tissue = BodyPlans.Of(grown.NodeTissue[n]);
            double m = grown.InitialMass[n];
            body.Temperature[n] = Physiology.ReferenceK;
            body.Mass[n] = 0.0;
            Physiology.AddMass(w, body, n, m, joules * m / kg);
            body.WaterKg[n] = m * tissue.WaterFraction;
        }
        double lipid = 0.0;
        for (int n = first; n < end; n++)
            if (grown.NodeTissue[n] == Tissue.Fat) lipid += body.Mass[n] - body.WaterKg[n];
        // Fresh fat's chemical energy is part of the energy supplied.
        if (lipid > 0.0)
        {
            double chem = lipid * Physiology.FatJPerKg;
            body.FatJ += chem;
            for (int n = first; n < end; n++) Physiology.Heat(body, n, -chem * Physiology.Capacity(body, n) / FreshCapacity(body, first, end));
        }
        return p;
    }

    /// <summary>Energy a fresh part made as <paramref name="part"/> describes holds at a temperature, J (with its fat's chemical energy).</summary>
    public static double FreshPartJoules(Physiology.World w, PartDef part, double temperatureK)
    {
        double j = 0.0;
        foreach (var (tissue, kg) in part.Layers)
        {
            var def = BodyPlans.Of(tissue);
            j += kg * (def.SpecificHeat * temperatureK + w.Offset(tissue));
            if (tissue == Tissue.Fat) j += kg * (1.0 - def.WaterFraction) * Physiology.FatJPerKg;
        }
        return j;
    }

    private static double FreshCapacity(Body body, int first, int end)
    {
        double c = 0.0;
        for (int n = first; n < end; n++) c += Physiology.Capacity(body, n);
        return c;
    }

    // ---------------------------------------------------------------- living

    private void Live(Simulation sim, Physiology.World w, Body body)
    {
        var anatomy = body.Anatomy;
        double core = body.BloodTemperature;
        double skin = 0.0, area = 0.0;
        for (int p = 0; p < anatomy.Parts.Length; p++)
        {
            if (!HasSkin(anatomy, p)) continue;
            skin += anatomy.Parts[p].SkinAreaM2 * body.Temperature[anatomy.LayerStart[p]];
            area += anatomy.Parts[p].SkinAreaM2;
        }
        skin = area > 0.0 ? skin / area : core;
        body.MeanSkinTemperature = skin;

        // The thermostat (Gagge 1971): set points of 37 °C at the core and 34 °C at the skin.
        double warmCore = Math.Max(0.0, core - 310.15), coldCore = Math.Max(0.0, 310.15 - core);
        double warmSkin = Math.Max(0.0, skin - 307.15), coldSkin = Math.Max(0.0, 307.15 - skin);
        bool regulating = !body.HeartStopped && body.BrainFunction > 0.5 && core > 303.15;
        body.ShiverW = regulating && core > 304.15 ? Math.Min(MaxShiverW, 19.4 * coldSkin * coldCore * area) : 0.0;
        // Sweating answers the mean body temperature, nine parts core to one part skin.
        double warmBody = Math.Max(0.0, 0.9 * core + 0.1 * skin - (0.9 * 310.15 + 0.1 * 307.15));
        body.SweatKgPerS = regulating ? Math.Min(MaxSweatKgPerS, 170e-3 / 3600.0 * area * warmBody * Math.Exp(warmSkin / 10.7)) : 0.0;
        _skinFlow = regulating ? Math.Clamp((6.3 + 100.0 * warmCore) / (1.0 + 0.5 * coldSkin) / 6.3, 0.15, 14.0) : 1.0;

        Metabolise(w, body);
        Breathe(sim, w, body);
        Digest(w, body);
        Kidneys(sim, w, body);
        Bleeding(sim, w, body);
        Sweat(sim, w, body);
        Sleep(body);
        Toxicity(body);
    }

    /// <summary>Whether a part meets the world through skin (and so sweats, and counts toward the skin's temperature).</summary>
    private static bool HasSkin(Anatomy anatomy, int p) =>
        !anatomy.Parts[p].Internal && anatomy.NodeTissue[anatomy.LayerStart[p]] == Tissue.Skin;

    /// <summary>Time for poisons to stop the heart with neither liver nor kidneys working, and for working ones to clear them, s.</summary>
    public const double ToxinSeconds = 3.0 * 86400.0, ClearSeconds = 86400.0;

    /// <summary>Liver and kidneys clear the blood together: with either below half its work, poisons build up.</summary>
    private static void Toxicity(Body body)
    {
        double clearing = Math.Min(body.FunctionLevel(PartFunction.Liver), body.FunctionLevel(PartFunction.Kidney));
        if (body.HeartStopped) clearing = 0.0;
        if (clearing < 0.5) body.Toxins += (1.0 - clearing / 0.5) * Dt / ToxinSeconds;
        else body.Toxins = Math.Max(0.0, body.Toxins - Dt / ClearSeconds);
    }

    public const double MaxShiverW = 350.0;
    /// <summary>About 1.5 L of sweat an hour.</summary>
    public const double MaxSweatKgPerS = 4.2e-4;

    // How far the skin's vessels are open this tick, relative to rest. Set per body, before its heat moves.
    private double _skinFlow = 1.0;

    private void Metabolise(Physiology.World w, Body body)
    {
        var plan = body.Plan;
        var anatomy = body.Anatomy;
        // A body missing parts has less tissue to keep alive; one grown more, more.
        double size = anatomy.InitialKg / (plan.InitialKg - plan.BloodKg);
        double q10 = Math.Pow(2.0, (body.BloodTemperature - Physiology.ReferenceK) / 10.0);
        double basal = plan.BasalW * size * q10 * (body.Asleep ? 0.9 : 1.0);
        double posture = body.Conscious ? plan.AwakeW * size * q10 : 0.0;
        double work = Math.Min(body.PendingWorkJ, MaxWorkW * Dt);
        body.PendingWorkJ -= work;
        double wantJ = (basal + posture + body.ShiverW) * Dt + work;

        // Glycogen first while there is plenty, more fat as it runs low.
        double carbShare = Math.Clamp(0.3 + 0.6 * body.GlycogenKg / body.GlycogenCapacityKg, 0.0, 0.9);
        double carb = Math.Min(body.GlycogenKg, wantJ * carbShare / CarbHeatPerKg);
        double burnable = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
            if (anatomy.NodeTissue[n] == Tissue.Fat) burnable += Physiology.BurnableFat(body, n);
        double fat = Math.Min(burnable, (wantJ - carb * CarbHeatPerKg) / FatHeatPerKg);
        if (fat < 0.0) fat = 0.0;
        if (carb + fat < wantJ / FatHeatPerKg * 0.5 && body.GlycogenKg > 0.0)
            carb = Math.Min(body.GlycogenKg, (wantJ - fat * FatHeatPerKg) / CarbHeatPerKg);
        double o2 = carb * Physiology.GlycogenO2 + fat * Physiology.FatO2;
        if (o2 > body.OxygenKg && o2 > 0.0)
        {
            double scale = body.OxygenKg / o2;
            carb *= scale; fat *= scale; o2 = body.OxygenKg;
        }
        _fuelShort = wantJ > 0.0 && (carb * CarbHeatPerKg + fat * FatHeatPerKg) < 0.25 * wantJ && body.OxygenFraction > 0.2;

        double heat = 0.0;
        if (carb > 0.0)
        {
            double j = body.GlycogenJ * carb / body.GlycogenKg;
            body.GlycogenKg -= carb;
            body.GlycogenJ -= j;
            heat += j;
        }
        if (fat > 0.0)
        {
            double lipid = Physiology.LipidKg(body);
            double chem = body.FatJ * fat / lipid;
            body.FatJ -= chem;
            heat += chem;
            for (int n = 0; n < body.Mass.Length; n++)
            {
                if (anatomy.NodeTissue[n] != Tissue.Fat) continue;
                double dk = fat * Physiology.BurnableFat(body, n) / burnable;
                if (!(dk > 0.0)) continue;
                double sensible = Physiology.JoulesOf(w, body, n, dk);
                Physiology.AddMass(w, body, n, -dk, -sensible);
                heat += sensible;
            }
        }
        if (o2 > 0.0)
        {
            double j = body.OxygenJ * o2 / body.OxygenKg;
            body.OxygenKg -= o2;
            body.OxygenJ -= j;
            heat += j;
            double co2 = carb * Physiology.GlycogenCO2 + fat * Physiology.FatCO2;
            double water = carb + fat + o2 - co2;
            body.CarbonDioxideKg += co2;
            body.CarbonDioxideJ += co2 * w.CarbonDioxideJPerKg;
            heat -= co2 * w.CarbonDioxideJPerKg;
            // Made in the cells, at their temperature: counted as water at the core's.
            double wj = water * (w.WaterJPerKg + (body.BloodTemperature - Physiology.ReferenceK) * w.Table[w.Water].SpecificHeat);
            Physiology.AddWater(w, body, water, wj);
            heat -= wj;
        }
        body.MetabolicW = heat / Dt;

        // Resting heat where the organs make it; shivering and work in the muscles.
        double total = wantJ > 0.0 ? wantJ : 1.0;
        double restShare = basal * Dt / total, muscleShare = 1.0 - restShare;
        SpreadHeat(body, heat * restShare, heat * muscleShare);
    }

    // Set by Metabolise for Vitals: the body can't fuel even a quarter of its resting needs.
    private bool _fuelShort;

    /// <summary>Heat a kg of glycogen and of fat gives in the body, roughly, J: used only to decide how much to burn.</summary>
    private const double CarbHeatPerKg = 16_800_000.0, FatHeatPerKg = 39_000_000.0;

    /// <summary>Hardest a body works, W of fuel.</summary>
    public const double MaxWorkW = 1000.0;

    private static void SpreadHeat(Body body, double rest, double muscle)
    {
        var tissues = body.Anatomy.NodeTissue;
        Span<double> tissueKg = stackalloc double[BodyPlans.Tissues.Length];
        for (int n = 0; n < body.Mass.Length; n++) tissueKg[(int)tissues[n]] += body.Mass[n];
        // Shares of the tissues it still has: a body without a liver makes that heat elsewhere.
        double shares = 0.0;
        for (int t = 0; t < tissueKg.Length; t++)
            if (tissueKg[t] > 0.0) shares += BodyPlans.Tissues[t].BasalShare;
        int muscleT = (int)Tissue.Muscle;
        if (!(tissueKg[muscleT] > 0.0)) { rest += muscle; muscle = 0.0; }
        if (!(shares > 0.0)) { Physiology.Heat(body, 0, rest + muscle); return; }
        for (int n = 0; n < body.Mass.Length; n++)
        {
            int t = (int)tissues[n];
            if (!(tissueKg[t] > 0.0)) continue;
            double j = rest * BodyPlans.Tissues[t].BasalShare / shares * body.Mass[n] / tissueKg[t];
            if (t == muscleT) j += muscle * body.Mass[n] / tissueKg[t];
            Physiology.Heat(body, n, j);
        }
    }

    /// <summary>
    /// Puts heat into the parts that do a job, by heat capacity (the lungs warming a breath, the gut
    /// digesting); with none left, into the root part's innermost layer.
    /// </summary>
    internal static void HeatParts(Body body, PartFunction function, double joules)
    {
        var anatomy = body.Anatomy;
        double capacity = 0.0;
        for (int n = 0; n < body.Mass.Length; n++)
            if (anatomy.Parts[anatomy.NodePart[n]].Function == function) capacity += Physiology.Capacity(body, n);
        if (!(capacity > 0.0)) { Physiology.Heat(body, anatomy.Inner(0), joules); return; }
        for (int n = 0; n < body.Mass.Length; n++)
            if (anatomy.Parts[anatomy.NodePart[n]].Function == function)
                Physiology.Heat(body, n, joules * Physiology.Capacity(body, n) / capacity);
    }

    /// <summary>One breath's worth of the tick: gas in from the head's tile, oxygen kept, gas out warm and moist.</summary>
    private void Breathe(Simulation sim, Physiology.World w, Body body)
    {
        var grid = sim.Grid;
        var plan = body.Plan;
        int head = TileOf(grid, body, body.Anatomy.OuterOf(body.Anatomy.BrainPart));
        body.OxygenSaturation = 0.0;
        if (body.HeartStopped || !CanBreathe(grid, body)) return;
        double lungs = Math.Min(body.FunctionLevel(PartFunction.Lung), 2.0);

        var table = grid.Materials;
        var gasIds = table.GasIds;
        int species = gasIds.Count;
        double total = grid.TotalGasMass(head);
        double drive = 1.0 + 2.0 * Math.Clamp((0.9 - body.OxygenFraction) / 0.5, 0.0, 1.0);
        double breath = Math.Min(VentilationKgPerJ * Math.Max(body.MetabolicW, plan.BasalW) * drive * Dt, 0.2 * total);
        if (!(breath > 0.0)) return;

        // How much oxygen the blood can bind: alveolar oxygen pressure through the haemoglobin curve.
        double pressure = FluidSystem.PressureAt(grid, head);
        double mol = 0.0;
        for (int s = 0; s < species; s++) mol += grid.GasMass[s][head] / (double)table[gasIds[s]].MolarMass;
        double o2Mol = grid.GasMass[w.AirSpecies][head] * w.OxygenFraction / Physiology.MolarMassO2;
        double inspired = Math.Max(0.0, pressure - 6270.0) * o2Mol / mol;
        double alveolar = Math.Max(0.0, inspired - 6625.0);
        double sat = Hill(alveolar);
        body.OxygenSaturation = sat;

        Span<double> taken = stackalloc double[species];
        Span<double> given = stackalloc double[species];
        for (int s = 0; s < species; s++) given[s] = taken[s] = grid.GasMass[s][head] * breath / total;
        double available = taken[w.AirSpecies] * w.OxygenFraction;
        // Lungs take oxygen through their surface: less lung, less of each breath's oxygen.
        double uptake = Math.Min(0.3 * lungs * available, Math.Max(0.0, plan.OxygenStoreKg * sat - body.OxygenKg));
        given[w.AirSpecies] -= uptake / w.OxygenFraction;
        given[w.NitrogenSpecies] += uptake / w.OxygenFraction - uptake;
        // In air poorer than the blood, oxygen leaves the blood again: foul air washes it out in seconds.
        double excess = body.OxygenKg - plan.OxygenStoreKg * sat;
        double release = excess > 0.0 ? excess * (1.0 - Math.Exp(-Dt / WashoutSeconds)) : 0.0;
        release = Math.Min(release, given[w.NitrogenSpecies] * w.OxygenFraction / (1.0 - w.OxygenFraction));
        double releaseJ = 0.0;
        if (release > 0.0)
        {
            releaseJ = body.OxygenJ * release / body.OxygenKg;
            body.OxygenKg -= release;
            body.OxygenJ -= releaseJ;
            given[w.AirSpecies] += release / w.OxygenFraction;
            given[w.NitrogenSpecies] -= release / w.OxygenFraction - release;
        }
        double co2 = body.CarbonDioxideKg, co2J = body.CarbonDioxideJ;
        given[w.CarbonDioxideSpecies] += co2;

        // Breathed out at 34 °C and saturated with water from the body.
        double outK = Math.Min(307.15, body.BloodTemperature);
        double x = Math.Min(0.5, Physiology.SaturationPressurePa(outK) / Math.Max(pressure, 1.0));
        double dry = 0.0;
        for (int s = 0; s < species; s++)
            if (s != w.SteamSpecies) dry += given[s] / (double)table[gasIds[s]].MolarMass;
        double water = Math.Max(0.0, x / (1.0 - x) * dry * table[w.Steam].MolarMass - taken[w.SteamSpecies]);
        (water, double waterJ) = Physiology.RemoveWater(w, body, water);
        given[w.SteamSpecies] += water;

        double condensed = EnergyAudit.CondensedJoules(grid, head), gas = EnergyAudit.GasJoules(grid, head);
        double inJ = gas * breath / total, outJ = 0.0;
        for (int s = 0; s < species; s++)
        {
            outJ += given[s] * w.GasJPerKg(gasIds[s], outK);
            grid.GasMass[s][head] += given[s] - taken[s];
        }
        EnergyAudit.SolveTemperatures(grid, head, condensed, gas - inJ + outJ);

        body.OxygenKg += uptake;
        body.OxygenJ += uptake * w.OxygenJPerKg;
        body.CarbonDioxideKg = 0.0;
        body.CarbonDioxideJ = 0.0;
        // The lungs warm and wet the air, or the air warms the lungs.
        HeatParts(body, PartFunction.Lung, inJ - outJ - uptake * w.OxygenJPerKg + co2J + waterJ + releaseJ);
    }

    /// <summary>
    /// Whether air reaches its lungs: it has lungs and a brain, its spine carries the order to
    /// breathe, its head is in gas (not water or rubble), and its chest isn't buried. Breathing
    /// stops with the heart.
    /// </summary>
    public static bool CanBreathe(WorldGrid grid, Body body)
    {
        var anatomy = body.Anatomy;
        if (anatomy.BrainPart < 0 || !body.SpineIntact || !(body.FunctionLevel(PartFunction.Lung) > 0.0)) return false;
        var head = Shares(grid, TileOf(grid, body, anatomy.OuterOf(anatomy.BrainPart)));
        if (head.Gas < 0.5) return false;
        for (int p = 0; p < anatomy.Parts.Length; p++)
            if (anatomy.Parts[p].Function == PartFunction.Lung && Shares(grid, TileOf(grid, body, anatomy.OuterOf(p))).Solid >= 0.5)
                return false;
        return true;
    }

    /// <summary>Haemoglobin's oxygen saturation at an oxygen pressure, Pa (Hill, P50 26.8 mmHg).</summary>
    public static double Hill(double pressurePa)
    {
        double r = Math.Pow(pressurePa / 3570.0, 2.7);
        return r / (1.0 + r);
    }

    /// <summary>Half-lives of a stomach's water and starch, s.</summary>
    public const double WaterAbsorbSeconds = 900.0, StarchDigestSeconds = 7200.0;

    private static void Digest(Physiology.World w, Body body)
    {
        // A damaged gut digests slowly, and none at all with none left.
        double gut = Math.Min(body.FunctionLevel(PartFunction.Gut), 2.0);
        if (!(gut > 0.0)) return;
        if (body.GutWaterKg > 0.0)
        {
            double kg = body.GutWaterKg * (1.0 - Math.Exp(-gut * Dt / WaterAbsorbSeconds));
            if (body.GutWaterKg - kg < 1e-6) kg = body.GutWaterKg;
            double j = body.GutWaterJ * kg / body.GutWaterKg;
            body.GutWaterKg -= kg;
            body.GutWaterJ -= j;
            Physiology.AddWater(w, body, kg, j);
        }
        if (body.GutCarbKg > 0.0)
        {
            double kg = body.GutCarbKg * (1.0 - Math.Exp(-gut * Dt / StarchDigestSeconds));
            if (body.GutCarbKg - kg < 1e-6) kg = body.GutCarbKg;
            double j = body.GutCarbJ * kg / body.GutCarbKg;
            body.GutCarbKg -= kg;
            body.GutCarbJ -= j;

            double toGlycogen = Math.Min(kg, Math.Max(0.0, body.GlycogenCapacityKg - body.GlycogenKg));
            double glycogenJ = toGlycogen * Physiology.GlycogenJPerKg;
            body.GlycogenKg += toGlycogen;
            body.GlycogenJ += glycogenJ;
            j -= glycogenJ;

            // What glycogen can't hold becomes fat, giving off carbon dioxide and water.
            double toFat = kg - toGlycogen;
            if (toFat > 0.0)
            {
                double fat = toFat * Physiology.LipogenesisFat, co2 = toFat * Physiology.LipogenesisCO2;
                double water = toFat - fat - co2;
                double fatChem = fat * Physiology.FatJPerKg;
                body.FatJ += fatChem;
                j -= fatChem;
                body.CarbonDioxideKg += co2;
                body.CarbonDioxideJ += co2 * w.CarbonDioxideJPerKg;
                j -= co2 * w.CarbonDioxideJPerKg;
                double waterJ = water * (w.WaterJPerKg + (body.BloodTemperature - Physiology.ReferenceK) * w.Table[w.Water].SpecificHeat);
                Physiology.AddWater(w, body, water, waterJ);
                j -= waterJ;
                var anatomy = body.Anatomy;
                double initialFat = 0.0;
                for (int n = 0; n < body.Mass.Length; n++)
                    if (anatomy.NodeTissue[n] == Tissue.Fat) initialFat += anatomy.InitialMass[n];
                for (int n = 0; n < body.Mass.Length && initialFat > 0.0; n++)
                {
                    if (anatomy.NodeTissue[n] != Tissue.Fat) continue;
                    double dk = fat * anatomy.InitialMass[n] / initialFat;
                    double sensible = Physiology.JoulesOf(w, body, n, dk);
                    Physiology.AddMass(w, body, n, dk, sensible);
                    j -= sensible;
                }
            }
            // What is left over is the heat of digestion.
            HeatParts(body, PartFunction.Gut, j);
        }
    }

    /// <summary>Urine the kidneys must make even when short of water, kg/s (0.4 L a day), and how full a bladder empties.</summary>
    public const double MinUrineKgPerS = 4.6e-6, BladderVoidKg = 0.35;

    private static void Kidneys(Simulation sim, Physiology.World w, Body body)
    {
        if (body.HeartStopped) return;
        var plan = body.Plan;
        double kidneys = Math.Min(body.FunctionLevel(PartFunction.Kidney), 2.0);
        double surplus = Math.Max(0.0, -body.WaterDeficitKg);
        // One kidney does nearly all the work of two; failing ones make little urine.
        double kg = Math.Min(1.0, 1.6 * kidneys) * (MinUrineKgPerS + surplus / 7200.0) * Dt;
        if (kg > 0.0)
        {
            var (taken, j) = Physiology.RemoveWater(w, body, kg);
            body.BladderKg += taken;
            body.BladderJ += j;
        }

        // Blood lost is made good from the body's water, at most a litre an hour.
        double deficit = plan.BloodKg - body.BloodKg;
        if (deficit > 0.0)
        {
            double refill = Math.Min(deficit, Dt / 3600.0);
            var (taken, j) = Physiology.RemoveWater(w, body, refill);
            if (taken > 0.0) Physiology.AddBlood(w, body, taken, j);
        }

        if (body.BladderKg >= BladderVoidKg && Spill(sim, w, body, w.Water, body.BladderKg, body.BladderJ))
        {
            body.BladderKg = 0.0;
            body.BladderJ = 0.0;
        }
    }

    /// <summary>Water in blood, by mass.</summary>
    public const double BloodWaterFraction = 0.8;

    private static void Bleeding(Simulation sim, Physiology.World w, Body body)
    {
        if (body.HeartStopped) return;
        var anatomy = body.Anatomy;
        for (int p = 0; p < body.Bleed.Length; p++)
        {
            if (body.Bleed[p] <= 0.0) continue;
            double kg = Math.Min(body.Bleed[p] * Dt, 0.5 * body.BloodKg);
            double j = Physiology.BloodJoulesOf(w, body, kg);
            Physiology.AddBlood(w, body, -kg, -j);
            if (anatomy.Parts[p].Internal)
            {
                // A torn organ bleeds into the body around it: out of the circulation, but not out of the body.
                int n = anatomy.Inner(anatomy.OuterOf(p));
                Physiology.AddMass(w, body, n, kg, j);
                body.WaterKg[n] += BloodWaterFraction * kg;
            }
            else
            {
                body.SpillKg += kg;
                body.SpillJ += j;
            }
            body.Bleed[p] *= Math.Exp(-Dt / ClotSeconds);
            if (body.Bleed[p] < 1e-7) body.Bleed[p] = 0.0;
        }
        if (body.SpillKg >= 0.02 && Spill(sim, w, body, w.Blood, body.SpillKg, body.SpillJ))
        {
            body.SpillKg = 0.0;
            body.SpillJ = 0.0;
        }
    }

    /// <summary>
    /// Pours liquid from the body onto the ground at its feet: into the tile's liquid if it holds
    /// water or blood already (the two are the same thermally), or into the empty tile.
    /// Returns false, and nothing moves, if there is no room for it there.
    /// </summary>
    private static bool Spill(Simulation sim, Physiology.World w, Body body, ushort material, double kg, double joules)
    {
        var grid = sim.Grid;
        int i = grid.Index(body.X, body.Y, body.Z);
        double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
        if (!grid.HasCondensed(i))
        {
            grid.SetCondensed(i, material, kg);
            grid.Temperature[i] = grid.GasTemperatureAt(i);
        }
        else if (grid.Material[i] == w.Water || grid.Material[i] == w.Blood) grid.Mass[i] += kg;
        else return false;
        EnergyAudit.SolveTemperatures(grid, i, condensed + joules, gas);
        sim.Activity.WakeAround(body.X, body.Y, body.Z);
        return true;
    }

    /// <summary>
    /// Sweat and the water that seeps through the skin evaporate from each part into the gas
    /// around it, as fast as the drop in vapour pressure drives them; burned skin weeps plasma.
    /// </summary>
    private static void Sweat(Simulation sim, Physiology.World w, Body body)
    {
        var grid = sim.Grid;
        var anatomy = body.Anatomy;
        double cloR = body.ClothingClo * CloM2KPerW;
        double skinArea = 0.0;
        for (int p = 0; p < anatomy.Parts.Length; p++)
            if (HasSkin(anatomy, p)) skinArea += anatomy.Parts[p].SkinAreaM2;
        for (int p = 0; p < anatomy.Parts.Length; p++)
        {
            if (!HasSkin(anatomy, p)) continue;
            int i = TileOf(grid, body, p);
            var share = Shares(grid, i);
            if (share.Gas <= 0.0) continue;
            int s = anatomy.LayerStart[p];
            double skinK = body.Temperature[s];
            double drop = Physiology.SaturationPressurePa(skinK) - Physiology.SteamPressurePa(w, grid, i);
            if (drop <= 0.0) continue;
            double area = anatomy.Parts[p].SkinAreaM2 * share.Gas;
            double hc = Convection(grid, i);
            // Lewis relation: 16.5 K/kPa; clothing passes vapour about a third as well as heat.
            double he = 1.0 / (1.0 / (0.0165 * hc) + cloR / (0.0165 * 0.34));
            double maxW = he * drop * area;
            double sweatW = body.SweatKgPerS * anatomy.Parts[p].SkinAreaM2 / skinArea * 2.4e6;
            double evaporate = Math.Min(sweatW, maxW) + 0.06 * Math.Max(0.0, maxW - sweatW);
            double weep = body.Necrosis(s) * 0.6 * maxW;

            double steamJ = w.GasJPerKg(w.Steam, skinK);
            // Roughly: the water leaves at the core's temperature. The skin pays the exact difference below.
            double latent = steamJ - (w.WaterJPerKg + (body.BloodTemperature - Physiology.ReferenceK) * w.Table[w.Water].SpecificHeat);
            if (latent <= 0.0) continue;
            double plasma = Math.Min(weep * Dt / latent, 0.01 * body.BloodKg);
            var (kg, lost) = Physiology.RemoveWater(w, body, evaporate * Dt / latent);
            if (plasma > 0.0)
            {
                double j = Physiology.BloodJoulesOf(w, body, plasma);
                Physiology.AddBlood(w, body, -plasma, -j);
                lost += j;
            }
            double steam = kg + plasma;
            if (!(steam > 0.0)) continue;
            double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
            grid.GasMass[w.SteamSpecies][i] += steam;
            EnergyAudit.SolveTemperatures(grid, i, condensed, gas + steam * steamJ);
            // The skin pays the latent heat.
            Physiology.Heat(body, s, lost - steam * steamJ);
        }
    }

    private static void Sleep(Body body)
    {
        if (body.Asleep)
        {
            body.SleepPressure -= Dt / (8.0 * 3600.0);
            if (body.SleepPressure <= 0.0) { body.SleepPressure = 0.0; body.Asleep = false; }
        }
        else
        {
            body.SleepPressure += Dt / (16.0 * 3600.0);
            // Past a day and a half awake nothing keeps it up.
            if (body.SleepPressure >= 1.5) body.Asleep = true;
        }
    }

    // ---------------------------------------------------------------- heat

    /// <summary>Convection coefficient at a tile, W/(m²·K): still air's, or more in a draught (ASHRAE, 8.3·v^0.6).</summary>
    private static double Convection(WorldGrid grid, int i)
    {
        var (x, y, z) = grid.Coordinates(i);
        int plane = grid.Width * grid.Height;
        double vx = 0.5 * (grid.AirVelocity[0][i] + (x > 0 ? grid.AirVelocity[0][i - 1] : 0.0));
        double vy = 0.5 * (grid.AirVelocity[1][i] + (y > 0 ? grid.AirVelocity[1][i - grid.Width] : 0.0));
        double vz = 0.5 * (grid.AirVelocity[2][i] + (z > 0 ? grid.AirVelocity[2][i - plane] : 0.0));
        double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        return Math.Max(StillAirConvection, 8.3 * Math.Pow(speed, 0.6));
    }

    /// <summary>Joules passing in one tick from a body at ta to one at tb through conductance g, relaxed exactly so it can't overshoot.</summary>
    private static double Relax(double ta, double tb, double ca, double cb, double g)
    {
        if (!(g > 0.0) || !(ca > 0.0) || !(cb > 0.0) || ta == tb) return 0.0;
        double pair = ca * cb / (ca + cb);
        return (ta - tb) * pair * (1.0 - Math.Exp(-g * Dt / pair));
    }

    private void Exchange(Simulation sim, Physiology.World w, Body body)
    {
        var anatomy = body.Anatomy;
        var tissues = anatomy.NodeTissue;
        // The heart pumps the blood that carries heat; a failing one moves less of it.
        double cardiac = Math.Min(body.FunctionLevel(PartFunction.Heart), 1.5);
        int most = 0;
        for (int p = 0; p < anatomy.Parts.Length; p++) most = Math.Max(most, anatomy.Parts[p].Layers.Length);
        Span<double> radius = stackalloc double[most + 1];

        for (int p = 0; p < anatomy.Parts.Length; p++)
        {
            var part = anatomy.Parts[p];
            int first = anatomy.LayerStart[p], end = anatomy.LayerStart[p + 1], layers = end - first;
            bool sphere = part.Shape == PartShape.Sphere || part.Internal || !(part.SkinAreaM2 > 0.0);

            // Conduction between neighbouring layers, through the geometry of concentric shells.
            Geometry(body, sphere, part, first, layers, radius, out double length);
            for (int k = 0; k + 1 < layers; k++)
            {
                int a = first + k, b = first + k + 1;
                double rb = radius[k + 1];
                double outerMid = 0.5 * (radius[k] + radius[k + 1]);
                double innerMid = 0.5 * (radius[k + 1] + (k + 2 < layers ? radius[k + 2] : 0.0));
                double ka = BodyPlans.Of(tissues[a]).Conductivity, kb = BodyPlans.Of(tissues[b]).Conductivity;
                double resistance = sphere
                    ? (1.0 / rb - 1.0 / outerMid) / (4.0 * Math.PI * ka) + (1.0 / Math.Max(innerMid, 1e-4) - 1.0 / rb) / (4.0 * Math.PI * kb)
                    : Math.Log(outerMid / rb) / (2.0 * Math.PI * length * ka) + Math.Log(rb / Math.Max(innerMid, 1e-4)) / (2.0 * Math.PI * length * kb);
                Pair(body, a, b, 1.0 / resistance);
            }

            // An organ conducts with the tissue of the part it sits in: from its centre to its surface, and on into the tissue around.
            if (part.Internal)
            {
                int outer = first, around = anatomy.Inner(anatomy.ParentOf[p]);
                double r = radius[0];
                double ko = BodyPlans.Of(tissues[outer]).Conductivity, kp = BodyPlans.Of(tissues[around]).Conductivity;
                if (r > 0.0) Pair(body, outer, around, 1.0 / (1.0 / (8.0 * Math.PI * ko * r) + 1.0 / (4.0 * Math.PI * kp * r)));
            }

            // Blood between every layer and the core.
            if (!body.HeartStopped && cardiac > 0.0)
            {
                double cb = Physiology.BloodCapacity(body);
                for (int n = first; n < end; n++)
                {
                    var tissue = tissues[n];
                    var def = BodyPlans.Of(tissue);
                    double flow = cardiac * def.Perfusion * body.Mass[n] / def.Density;
                    if (tissue is Tissue.Skin or Tissue.Fat) flow *= body.Alive ? _skinFlow : 1.0;
                    if (tissue == Tissue.Muscle) flow *= 1.0 + body.ShiverW / 50.0;
                    double g = flow * BodyPlans.BloodDensity * BodyPlans.BloodSpecificHeat;
                    double q = Relax(body.Temperature[n], body.BloodTemperature, Physiology.Capacity(body, n), cb, g);
                    Physiology.Heat(body, n, -q);
                    body.BloodTemperature += q / cb;
                }
            }

            // The skin and the world around it.
            if (!part.Internal) Surroundings(sim, body, p);
        }
    }

    /// <summary>Outer radius of each layer of a part, m, from the volumes of its tissue, outermost first; for a cylinder, its length.</summary>
    private static void Geometry(Body body, bool sphere, PartDef part, int first, int layers, Span<double> radius, out double length)
    {
        var tissues = body.Anatomy.NodeTissue;
        double volume = 0.0;
        for (int k = 0; k < layers; k++)
            volume += body.Mass[first + k] / BodyPlans.Of(tissues[first + k]).Density;
        double outer;
        if (sphere)
        {
            outer = Math.Cbrt(3.0 * volume / (4.0 * Math.PI));
            length = 0.0;
        }
        else
        {
            outer = 2.0 * volume / part.SkinAreaM2;
            length = part.SkinAreaM2 / (2.0 * Math.PI * outer);
        }
        double inside = volume;
        for (int k = 0; k < layers; k++)
        {
            radius[k] = sphere
                ? Math.Cbrt(3.0 * Math.Max(inside, 0.0) / (4.0 * Math.PI))
                : Math.Sqrt(Math.Max(inside, 0.0) / (Math.PI * length));
            inside -= body.Mass[first + k] / BodyPlans.Of(tissues[first + k]).Density;
        }
        radius[0] = outer;
    }

    private static void Pair(Body body, int a, int b, double g)
    {
        double ca = Physiology.Capacity(body, a), cb = Physiology.Capacity(body, b);
        double q = Relax(body.Temperature[a], body.Temperature[b], ca, cb, g);
        body.Temperature[a] -= q / ca;
        body.Temperature[b] += q / cb;
    }

    /// <summary>A part's skin trades heat with what fills its tile: gas through clothing, water, or rubble; feet with the floor.</summary>
    private static void Surroundings(Simulation sim, Body body, int p)
    {
        var grid = sim.Grid;
        var part = body.Anatomy.Parts[p];
        int s = body.Anatomy.LayerStart[p];
        int i = TileOf(grid, body, p);
        var share = Shares(grid, i);
        double cloR = body.ClothingClo * CloM2KPerW;
        double cs = Physiology.Capacity(body, s);

        if (share.Gas > 0.0)
        {
            double tg = grid.GasTemperatureAt(i);
            double cg = grid.HasTwoTemperatures(i) ? grid.GasHeatCapacity(i) : grid.HeatCapacity(i);
            double ts = body.Temperature[s];
            double hr = SkinEmissivity * StefanBoltzmann * (ts * ts + tg * tg) * (ts + tg);
            double u = 1.0 / (cloR + 1.0 / (Convection(grid, i) + hr));
            double q = Relax(ts, tg, cs, cg, u * part.SkinAreaM2 * share.Gas);
            body.Temperature[s] -= q / cs;
            EnergyAudit.AddJoules(grid, i, 0.0, q);
        }
        if (share.Liquid > 0.0 || share.Solid > 0.0)
        {
            var def = grid.Materials[grid.Material[i]];
            double tc = grid.Temperature[i];
            double cc = grid.HasTwoTemperatures(i) ? grid.CondensedHeatCapacity(i) : grid.HeatCapacity(i);
            double u = share.Liquid > 0.0
                // Wet clothing keeps almost none of its insulation: water flushes through it.
                ? 1.0 / (0.02 * cloR + 1.0 / Math.Clamp(StillWaterConvection * Math.Sqrt(def.ThermalConductivity / 0.6), 100.0, 3000.0))
                : 1.0 / (cloR + ContactDepthM / Math.Max(def.ThermalConductivity, 1e-3));
            double q = Relax(body.Temperature[s], tc, cs, cc, u * part.SkinAreaM2 * (share.Liquid + share.Solid));
            body.Temperature[s] -= q / cs;
            EnergyAudit.AddJoules(grid, i, q, 0.0);
        }
        if (part.SoleAreaM2 > 0.0 && body.Z > 0)
        {
            int below = i - grid.Width * grid.Height;
            if (grid.HasCondensed(below) && grid.Materials[grid.Material[below]].Phase == Phase.Solid)
            {
                var def = grid.Materials[grid.Material[below]];
                double cc = grid.HasTwoTemperatures(below) ? grid.CondensedHeatCapacity(below) : grid.HeatCapacity(below);
                double u = 1.0 / (cloR + ContactDepthM / Math.Max(def.ThermalConductivity, 1e-3));
                double q = Relax(body.Temperature[s], grid.Temperature[below], cs, cc, u * part.SoleAreaM2);
                body.Temperature[s] -= q / cs;
                EnergyAudit.AddJoules(grid, below, q, 0.0);
            }
        }
    }

    /// <summary>How deep into a solid it touches the heat of skin reaches, for contact, m.</summary>
    public const double ContactDepthM = 0.02;

    // ---------------------------------------------------------------- tissue freezing, boiling, burning

    private static void TissuePhase(Simulation sim, Physiology.World w, Body body)
    {
        var grid = sim.Grid;
        var anatomy = body.Anatomy;
        const double ea = 6.28e5 / 8.314462618, lnA = 226.47; // Henriques: A = 3.1e98 /s, Ea = 628 kJ/mol
        for (int n = 0; n < body.Mass.Length; n++)
        {
            double c = Physiology.Capacity(body, n);
            double t = body.Temperature[n];
            if (t < Physiology.TissueFreezingK && body.FrozenKg[n] < body.WaterKg[n])
            {
                double kg = Math.Min(body.WaterKg[n] - body.FrozenKg[n], (Physiology.TissueFreezingK - t) * c / Physiology.FusionJPerKg);
                body.FrozenKg[n] += kg;
                body.Temperature[n] = t + kg * Physiology.FusionJPerKg / c;
                if (body.WaterKg[n] > 0.0) body.Frostbite[n] = Math.Max(body.Frostbite[n], body.FrozenKg[n] / body.WaterKg[n]);
            }
            else if (t > Physiology.TissueFreezingK && body.FrozenKg[n] > 0.0)
            {
                double kg = Math.Min(body.FrozenKg[n], (t - Physiology.TissueFreezingK) * c / Physiology.FusionJPerKg);
                body.FrozenKg[n] -= kg;
                body.Temperature[n] = t - kg * Physiology.FusionJPerKg / c;
            }
            t = body.Temperature[n];

            if (t > Physiology.BoilingK && Physiology.FreeWater(body, n) > 0.0)
            {
                // Its water boils off as steam into the tile, holding the tissue at 100 °C until it is dry.
                var tissue = anatomy.NodeTissue[n];
                double cs = BodyPlans.Of(tissue).SpecificHeat;
                double steamJ = w.GasJPerKg(w.Steam, Physiology.BoilingK);
                double latent = steamJ - (cs * Physiology.BoilingK + w.Offset(tissue));
                double kg = Math.Min(Physiology.FreeWater(body, n), (t - Physiology.BoilingK) * c / latent);
                kg = Math.Min(kg, 0.5 * body.Mass[n]);
                Physiology.AddMass(w, body, n, -kg, -kg * steamJ);
                body.WaterKg[n] -= kg;
                int i = TileOf(grid, body, anatomy.OuterOf(anatomy.NodePart[n]));
                double condensed = EnergyAudit.CondensedJoules(grid, i), gas = EnergyAudit.GasJoules(grid, i);
                grid.GasMass[w.SteamSpecies][i] += kg;
                EnergyAudit.SolveTemperatures(grid, i, condensed, gas + kg * steamJ);
                t = body.Temperature[n];
            }

            if (t > 317.15) body.Burn[n] += Math.Exp(Math.Min(lnA - ea / t, 30.0)) * Dt;
        }
    }

    // ---------------------------------------------------------------- life and death

    private void Vitals(Body body)
    {
        bool fuelShort = _fuelShort;
        _fuelShort = false;
        if (!body.Alive) return;
        var plan = body.Plan;
        if (body.IsRemains)
        {
            // Its brain went with another piece.
            Die(body, CauseOfDeath.Dismembered);
            return;
        }
        double core = body.BloodTemperature;
        if (!body.HeartStopped
            && (core < 297.15 || core > 317.15 || body.BloodFraction < 0.5 || body.OxygenFraction < 0.05
                || body.FunctionLevel(PartFunction.Heart) < FailedHealth || body.Toxins >= 1.0
                || body.WaterDeficitKg >= 0.12 * plan.InitialKg || fuelShort))
            body.HeartStopped = true;

        // The brain on what oxygen reaches it: none with the heart stopped.
        double want = body.HeartStopped ? 1.0 : Math.Clamp((0.3 - body.OxygenFraction) / 0.25, 0.0, 1.0);
        body.BrainHypoxia += want * Dt / BrainAnoxiaSeconds;

        if (body.Health(body.Anatomy.BrainPart) < 0.1) Die(body, CauseOfDeath.BrainInjury);
        else if (body.BrainHypoxia >= 1.0) Die(body, CauseOfDeath.Hypoxia);
    }

    private static void Die(Body body, CauseOfDeath cause)
    {
        body.Alive = false;
        body.HeartStopped = true;
        body.Asleep = false;
        body.Cause = cause;
    }
}
