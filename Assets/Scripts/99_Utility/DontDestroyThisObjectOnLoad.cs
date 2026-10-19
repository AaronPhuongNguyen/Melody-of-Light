using UnityEngine;

[DefaultExecutionOrder(-999)]
public class DontDestroyThisObjectOnLoad : MonoBehaviour
{
    private void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
    }
}