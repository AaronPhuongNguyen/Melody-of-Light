public class HangoutButton:LoadingManagerUser
{
    private void Update()
    {
        if(_button!=null && PlayerManager.Instance !=null) _button.interactable = PlayerManager.Instance.Data.Energy > 0;
    }
}