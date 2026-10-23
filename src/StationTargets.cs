using UnityEngine;

namespace SharedInventory;

internal static class StationTargets
{
    // Resolve the same Interactable as Player.Interact. Output switches and
    // unrelated controls on the same machine must not activate polling.
    public static bool IsInputTarget(GameObject? hover)
    {
        if (hover == null) return false;
        var target = hover.GetComponentInParent<Interactable>();
        if (target is Switch control)
        {
            var smelter = control.GetComponentInParent<Smelter>();
            if (smelter != null) return control == smelter.m_addOreSwitch || control == smelter.m_addWoodSwitch;
            var cooking = control.GetComponentInParent<CookingStation>();
            if (cooking != null) return control == cooking.m_addFoodSwitch || control == cooking.m_addFuelSwitch;
            var fermenter = control.GetComponentInParent<Fermenter>();
            return fermenter != null && control == fermenter.m_addSwitch;
        }
        if (target is Fireplace fire) return fire.m_canRefill && !fire.m_infiniteFuel;
        if (target is CookingStation station) return station.m_addFoodSwitch == null;
        return target is Fermenter;
    }
}
