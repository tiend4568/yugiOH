using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class ButtonSound : MonoBehaviour
{
    void Start()
    {
        Button btn = GetComponent<Button>();
        if (btn != null)
        {
            btn.onClick.AddListener(PlayClickAudio);
        }
    }

    void PlayClickAudio()
    {
        if (SoundManager.Instance != null)
        {
            // Gọi phát âm thanh click có id là "Click" trong sfxList của SoundManager
            SoundManager.Instance.PlayDefaultClick();
        }
    }
}