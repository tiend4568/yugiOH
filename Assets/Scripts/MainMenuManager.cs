using UnityEngine;

public class MainMenuManager : MonoBehaviour
{
    [Header("Các Panel giao diện")]
    public GameObject mainMenuPanel;
    public GameObject deckPanel;
    public GameObject settingsPanel;

    void Start()
    {
        BackToMainMenu(); // Vừa vào game là hiện menu chính
    }

    // 1. Mở màn hình Bộ Bài (Tắt menu, bật DeckPanel)
    public void OpenDeckPanel()
    {
        if (mainMenuPanel != null) mainMenuPanel.SetActive(false);
        if (deckPanel != null) deckPanel.SetActive(true);
        if (settingsPanel != null) settingsPanel.SetActive(false);
    }

    // 2. Mở màn hình Cài Đặt (Tắt menu, bật SettingsPanel)
    public void OpenSettingsPanel()
    {
        if (mainMenuPanel != null) mainMenuPanel.SetActive(false);
        if (deckPanel != null) deckPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(true);
        Debug.Log("Settings trỏ tới: " + (settingsPanel ? settingsPanel.name : "NULL")
              + " | đang bật: " + (settingsPanel && settingsPanel.activeSelf), this);
    }

    // 3. Nút Quay Lại chung cho mọi màn hình phụ (Bật lại menu, tắt hết các Panel con)
    public void BackToMainMenu()
    {
        if (mainMenuPanel != null) mainMenuPanel.SetActive(true);
        if (deckPanel != null) deckPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
    }
}