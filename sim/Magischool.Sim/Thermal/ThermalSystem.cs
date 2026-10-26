using System;
using Magischool.Sim.Core;
using Magischool.Sim.Materials;

namespace Magischool.Sim.Thermal;

/// <summary>
/// Layer 2: heat moving between neighbouring tiles.
///
/// Every shared face between two tiles is a thermal conductance G (W/K), built
/// from two half-tile resistances in series. Heat flows from hot to cold at
/// G × ΔT watts. Each face's transfer is subtracted from one tile and added to
/// the other, so energy is conserved to double precision.
///
/// <para>Solids conduct: a half tile's resistance is (0.5 m) / k.</para>
/// <para>
/// Fluids also convect, which at a 1 m scale carries far more heat than
/// conduction (still air conducts so poorly that a hot stone would take days
/// to warm the air beside it). Until real fluid flow arrives in layer 4, a
/// fluid's side of a face uses whichever is lower: its conduction resistance
/// or a natural-convection film resistance 1 / h.
/// </para>
/// <para>
/// Not modelled yet: radiation (needs line of sight; planned with fire) and
/// heat carried by moving fluid (layer 4). World edges are insulated.
/// </para>
/// </summary>
public sealed class ThermalSystem : ISimSystem
{
    /// <summary>Natural convection between still air and a surface, W/(m²·K).</summary>
    public const float GasFilmCoefficient = 10f;

    /// <summary>Natural convection between still water and a surface, W/(m²·K).</summary>
    public const float LiquidFilmCoefficient = 500f;

    /// <summary>Faces per tile. Each face may move at most 1/6 of the energy that would equalise it, so a tile can never overshoot.</summary>
    private const int FacesPerTile = 6;

    private const float FaceAreaM2 = 1f;
    private const float HalfTileM = 0.5f;

    private MaterialTable? _preparedFor;
    private double[] _halfResistance = Array.Empty<double>();
    private double[] _specificHeat = Array.Empty<double>();
    private double[] _mass = Array.Empty<double>();
    private double[] _delta = Array.Empty<double>();

    public string Name => "thermal";

    /// <summary>Thermal conductance through the face between two materials, W/K.</summary>
    public static double FaceConductance(MaterialDef a, MaterialDef b) => 1.0 / (HalfResistance(a) + HalfResistance(b));

    /// <summary>Resistance of half a tile of this material, K/W.</summary>
    public static double HalfResistance(MaterialDef def)
    {
        double conduction = def.ThermalConductivity > 0f
            ? HalfTileM / (def.ThermalConductivity * FaceAreaM2)
            : double.PositiveInfinity;
        return def.Phase switch
        {
            Phase.Gas => Math.Min(conduction, 1.0 / (GasFilmCoefficient * FaceAreaM2)),
            Phase.Liquid => Math.Min(conduction, 1.0 / (LiquidFilmCoefficient * FaceAreaM2)),
            _ => conduction,
        };
    }

    public void Update(Simulation sim)
    {
        Prepare(sim);

        var grid = sim.Grid;
        ushort[] material = grid.Material;
        double[] temperature = grid.Temperature;
        _mass = grid.Mass;
        int width = grid.Width, height = grid.Height, depth = grid.Depth;
        int plane = width * height;

        // Gather every face's transfer first, then apply, so results don't depend on loop order.
        for (int z = 0; z < depth; z++)
        for (int y = 0; y < height; y++)
        {
            int row = z * plane + y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                if (x + 1 < width) Exchange(i, i + 1, material, temperature);
                if (y + 1 < height) Exchange(i, i + width, material, temperature);
                if (z + 1 < depth) Exchange(i, i + plane, material, temperature);
            }
        }

        for (int i = 0; i < grid.Count; i++)
        {
            double delta = _delta[i];
            if (delta == 0.0) continue;
            temperature[i] = Math.Max(0.0, temperature[i] + delta / (_mass[i] * _specificHeat[material[i]]));
            _delta[i] = 0.0;
        }
    }

    private void Exchange(int a, int b, ushort[] material, double[] temperature)
    {
        double dT = temperature[a] - temperature[b];
        if (dT == 0.0) return;

        ushort ma = material[a], mb = material[b];
        double conductance = 1.0 / (_halfResistance[ma] + _halfResistance[mb]);
        double joules = conductance * dT * Simulation.SecondsPerTick;

        // Never move more than this face's share of what would bring the pair to equilibrium.
        double ca = _mass[a] * _specificHeat[ma], cb = _mass[b] * _specificHeat[mb];
        if (ca <= 0.0 || cb <= 0.0) return;
        double limit = dT * (ca * cb / (ca + cb)) / FacesPerTile;
        if (Math.Abs(joules) > Math.Abs(limit)) joules = limit;

        _delta[a] -= joules;
        _delta[b] += joules;
    }

    private void Prepare(Simulation sim)
    {
        if (_delta.Length != sim.Grid.Count)
            _delta = new double[sim.Grid.Count];

        if (ReferenceEquals(_preparedFor, sim.Materials)) return;
        var table = sim.Materials;
        _halfResistance = new double[table.Count];
        _specificHeat = new double[table.Count];
        for (ushort id = 0; id < table.Count; id++)
        {
            _halfResistance[id] = HalfResistance(table[id]);
            _specificHeat[id] = table[id].SpecificHeat;
        }
        _preparedFor = table;
    }
}
