using UnityEngine;

namespace SharedInventory;

internal static class StationTargets
{
    // Resolve the same Interactable as Player.Interact. Output switches and
    // unrelated controls on the same machine must not activate polling.
    public static bool IsInputTarget(GameObject? hover) => TryGet(hover, out _, out _);

    public static bool TryGet(GameObject? hover, out Component? station, out StationInputKind kind)
    {
        station = null;
        kind = StationInputKind.Processing;
        if (hover == null) return false;
        var target = hover.GetComponentInParent<Interactable>();
        if (target is Switch control)
        {
            var smelter = control.GetComponentInParent<Smelter>();
            if (smelter != null)
            {
                station = smelter;
                kind = control == smelter.m_addWoodSwitch ? StationInputKind.Fuel : StationInputKind.Processing;
                return control == smelter.m_addOreSwitch || control == smelter.m_addWoodSwitch;
            }
            var cooking = control.GetComponentInParent<CookingStation>();
            if (cooking != null)
            {
                station = cooking;
                kind = control == cooking.m_addFuelSwitch ? StationInputKind.Fuel : StationInputKind.Processing;
                return control == cooking.m_addFoodSwitch || control == cooking.m_addFuelSwitch;
            }
            var fermenter = control.GetComponentInParent<Fermenter>();
            station = fermenter;
            return fermenter != null && control == fermenter.m_addSwitch;
        }
        if (target is Fireplace fire)
        {
            station = fire;
            kind = StationInputKind.Fuel;
            return fire.m_canRefill && !fire.m_infiniteFuel;
        }
        if (target is CookingStation directCooking) { station = directCooking; return directCooking.m_addFoodSwitch == null; }
        station = target as Fermenter;
        return station != null;
    }
}
