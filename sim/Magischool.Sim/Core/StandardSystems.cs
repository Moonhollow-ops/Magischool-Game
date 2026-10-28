using Magischool.Sim.Bodies;
using Magischool.Sim.Combustion;
using Magischool.Sim.Ether;
using Magischool.Sim.Fluids;
using Magischool.Sim.Magic;
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
    /// <param name="skyTemperatureK">Temperature of the air beyond an open sky.</param>
    public static ISimSystem[] Create(bool openSky = true, double skyTemperatureK = Materials.BuiltInMaterials.RoomTemperatureK) => new ISimSystem[]
    {
        new EtherSystem(), // first, so a rupture's heat spreads the same tick
        new SpellSystem(), // spells in flight strike before heat spreads
        new BodySystem(), // bodies trade heat and breath with their tiles before heat spreads
        new ThermalSystem(),
        new PhaseSystem(),
        new CombustionSystem(),
        new StructureSystem(), // before fluids, so gas and water fill where rubble fell from
        new FluidSystem(openSky, skyTemperatureK), // after phase changes, so fresh steam moves the same tick
        new ActivitySystem(), // always last: decides what sleeps next tick
    };
}
