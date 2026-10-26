using Magischool.Sim.Thermal;

namespace Magischool.Sim.Core;

/// <summary>
/// The physics layers in the order they run each tick. Order matters for
/// determinism: append new layers where they belong and never reorder casually.
/// </summary>
public static class StandardSystems
{
    public static ISimSystem[] Create() => new ISimSystem[]
    {
        new ThermalSystem(),
    };
}
