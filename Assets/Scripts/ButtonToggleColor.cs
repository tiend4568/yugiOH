using UnityEngine;
using UnityEngine.UI;

public class ButtonToggleColor : MonoBehaviour
{
    private Image bgImage;
    private bool isOn = true;

    // Phân biệt nút này điều khiển Nhạc nền hay SFX (Nhập "Music" hoặc "SFX" trong Inspector)
    public string soundType = "Music";

    void Start()
    {
        bgImage = GetComponent<Image>();

        // Đồng bộ trạng thái ban đầu chuẩn theo SoundManager
        if (SoundManager.Instance != null)
        {
            if (soundType == "Music") isOn = SoundManager.Instance.IsMusicOn;
            else if (soundType == "SFX") isOn = SoundManager.Instance.IsSFXOn;
        }
        UpdateColor();
    }

    // Gắn hàm này vào sự kiện On Click() của nút Bật/Tắt
    public void OnButtonClicked()
    {
        isOn = !isOn;
        UpdateColor();

        if (SoundManager.Instance != null)
        {
            if (soundType == "Music")
            {
                SoundManager.Instance.ToggleMusic(isOn);
            }
            else if (soundType == "SFX")
            {
                SoundManager.Instance.ToggleSFX(isOn);
            }
        }
    }

    void UpdateColor()
    {
        if (bgImage != null)
        {
            // Bật -> Sáng trắng, Tắt -> Xám đen
            bgImage.color = isOn ? Color.white : new Color(0.4f, 0.4f, 0.4f, 1f);
        }
    }
}