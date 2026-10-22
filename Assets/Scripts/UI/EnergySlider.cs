using UnityEngine;
using UnityEngine.UI;

public class EnergySlider : MonoBehaviour
{
    public EnergyBar energyBar;

    PlayerManager pm => PlayerManager.Instance;

    private void Update()
    {
        if(pm == null) return;
        if (energyBar == null) return;
        energyBar.SetMax(pm.Data.MaxEnergy);
        energyBar.SetValue(pm.Data.Energy);
    }
}