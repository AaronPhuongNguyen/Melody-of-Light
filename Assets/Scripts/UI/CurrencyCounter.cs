using TMPro;
using UnityEngine;

public class CurrencyCounter : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI text;

    PlayerManager pm = PlayerManager.Instance;

    private void LateUpdate()
    {
        if (pm == null) return;
        if(text==null) return;

        text.text = pm.Data.Money.ToString("F0");
    }
}