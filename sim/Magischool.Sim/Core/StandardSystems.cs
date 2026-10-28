using Magischool.Sim.Combustion;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Structure;
using Magischool.Sim.Thermal;

namespace Magischool.Sim.Core;

/// <summary>
/// The physics layers in the order they run each tick. Order matters for
/// determinism: append new layers where they belong and never reorder casually.
/// </summary>
public static class StandardSystems
{
    /// <param name="openSky">False seals the top of the world too, for lab tests that count every joule.</param>
    public static ISimSystem[] Create(bool openSky = true) => new ISimSystem[]
    {
        new EtherSystem(), // first, so a rupture's heat spreads the same tick
        new ThermalSystem(),
        new PhaseSystem(),
        new CombustionSystem(),
        new StructureSystem(), // before fluids, so gas and water fill where rubble fell from
        new FluidSystem(openSky), // after phase changes, so fresh steam moves the same tick
        new ActivitySystem(), // always last: decides what sleeps next tick
    };
}
