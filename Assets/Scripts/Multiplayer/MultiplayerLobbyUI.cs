using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Tự động thêm nút "ĐẤU ONLINE (1v1)" vào Main Menu và dựng giao diện Phòng chờ (Lobby) trên SampleScene.
/// Không yêu cầu kéo thả thủ công trên Scene; vẫn tương thích hoàn toàn với MainMenuManager của v1.
/// </summary>
public class MultiplayerLobbyUI : MonoBehaviour
{
    public static MultiplayerLobbyUI Instance { get; private set; }

    private MainMenuManager mainMenuManager;
    private MultiplayerLobbyManager lobbyManager;
    private Canvas targetCanvas;
    private TMP_FontAsset sharedFont;

    private GameObject lobbyPanelRoot;
    private GameObject connectGroup;
    private GameObject connectingGroup;
    private GameObject roomGroup;

    // Connect UI
    private TMP_InputField playerNameInput;
    private TMP_InputField roomIdInput;
    private TMP_InputField roomPasswordInput;
    private TextMeshProUGUI localDeckSummaryText;

    // Room UI
    private TextMeshProUGUI roomHeaderText;
    private TextMeshProUGUI roomAddressText;
    private TextMeshProUGUI hostNameText;
    private TextMeshProUGUI hostDeckText;
    private TextMeshProUGUI hostStatusText;
    private Image hostCardBg;
    private TextMeshProUGUI clientNameText;
    private TextMeshProUGUI clientDeckText;
    private TextMeshProUGUI clientStatusText;
    private Image clientCardBg;

    private Button readyButton;
    private TextMeshProUGUI readyButtonText;
    private Button startMatchButton;
    private TextMeshProUGUI statusText;

    private bool isBuilt = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrapLobbyUI()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureExistsForScene(SceneManager.GetActiveScene());
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureExistsForScene(scene);
    }

    static void EnsureExistsForScene(Scene scene)
    {
        if (!scene.IsValid()) return;
        if (!scene.name.Equals("SampleScene", StringComparison.OrdinalIgnoreCase)) return;

        if (FindObjectOfType<MultiplayerLobbyUI>() == null)
        {
            new GameObject("AutoMultiplayerLobbyUI", typeof(MultiplayerLobbyUI));
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        if (FindObjectOfType<EventSystem>() == null)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        lobbyManager = MultiplayerLobbyManager.EnsureInstance();
        mainMenuManager = FindObjectOfType<MainMenuManager>();

        ResolveCanvasAndFont();
        BuildLobbyUI();
        EnsureMainMenuMultiplayerButton();

        if (lobbyManager != null)
        {
            lobbyManager.OnLobbyStateChanged += HandleLobbyStateChanged;
            lobbyManager.OnStatusChanged += HandleStatusChanged;
        }

        // Nếu đang từ trận quay lại hoặc vẫn đang ở trong phòng
        if (lobbyManager != null && (lobbyManager.IsInRoom || lobbyManager.IsConnecting))
        {
            OpenLobbyPanel();
        }
        else if (lobbyPanelRoot != null)
        {
            lobbyPanelRoot.SetActive(false);
        }

        RefreshUI();
    }

    void OnDestroy()
    {
        if (lobbyManager != null)
        {
            lobbyManager.OnLobbyStateChanged -= HandleLobbyStateChanged;
            lobbyManager.OnStatusChanged -= HandleStatusChanged;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void ResolveCanvasAndFont()
    {
        if (mainMenuManager != null && mainMenuManager.mainMenuPanel != null)
        {
            targetCanvas = mainMenuManager.mainMenuPanel.GetComponentInParent<Canvas>();
        }

        if (targetCanvas == null)
        {
            targetCanvas = FindObjectOfType<Canvas>();
        }

        if (targetCanvas == null)
        {
            GameObject cObj = new GameObject("MultiplayerLobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            targetCanvas = cObj.GetComponent<Canvas>();
            targetCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            targetCanvas.sortingOrder = 20;

            CanvasScaler scaler = cObj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        // Ưu tiên TMP_Settings.defaultFontAsset (ARIAL SDF hỗ trợ đầy đủ tiếng Việt)
        sharedFont = TMP_Settings.defaultFontAsset;
        if (sharedFont == null)
        {
            TextMeshProUGUI existingTmp = FindObjectOfType<TextMeshProUGUI>(true);
            if (existingTmp != null && existingTmp.font != null)
            {
                sharedFont = existingTmp.font;
            }
        }
    }

    private void EnsureMainMenuMultiplayerButton()
    {
        if (mainMenuManager == null || mainMenuManager.mainMenuPanel == null) return;

        Transform menuRoot = mainMenuManager.mainMenuPanel.transform;
        if (menuRoot.Find("Btn_Multiplayer") != null) return;

        // Tìm các nút sẵn có để học phong cách và vị trí đặt gọn gàng
        Button templateBtn = null;
        Button[] menuButtons = menuRoot.GetComponentsInChildren<Button>(true);

        float lowestY = float.MaxValue;
        float secondLowestY = float.MaxValue;
        RectTransform lowestRT = null;

        foreach (var b in menuButtons)
        {
            if (b == null || b.transform.parent == null) continue;
            string n = b.name.ToLower();
            if (n.Contains("settings") || n.Contains("deck") || n.Contains("play"))
            {
                RectTransform brt = b.GetComponent<RectTransform>();
                if (brt != null)
                {
                    if (brt.anchoredPosition.y < lowestY)
                    {
                        secondLowestY = lowestY;
                        lowestY = brt.anchoredPosition.y;
                        lowestRT = brt;
                    }
                    else if (brt.anchoredPosition.y < secondLowestY)
                    {
                        secondLowestY = brt.anchoredPosition.y;
                    }
                }
                if (templateBtn == null || n.Contains("settings"))
                {
                    templateBtn = b;
                }
            }
        }

        if (templateBtn != null)
        {
            GameObject cloneObj = Instantiate(templateBtn.gameObject, templateBtn.transform.parent);
            cloneObj.name = "Btn_Multiplayer";

            Button cloneBtn = cloneObj.GetComponent<Button>();
            cloneBtn.onClick = new Button.ButtonClickedEvent();
            cloneBtn.onClick.AddListener(OpenLobbyPanel);

            // Nếu parent không có LayoutGroup thì đặt ngay phía dưới nút thấp nhất
            LayoutGroup parentLayout = templateBtn.transform.parent.GetComponent<LayoutGroup>();
            if (parentLayout == null && lowestRT != null)
            {
                RectTransform cloneRT = cloneObj.GetComponent<RectTransform>();
                float step = (secondLowestY < float.MaxValue)
                    ? Mathf.Abs(secondLowestY - lowestY)
                    : (lowestRT.sizeDelta.y + 18f);
                if (step < 40f) step = lowestRT.sizeDelta.y + 18f;

                cloneRT.anchoredPosition = new Vector2(lowestRT.anchoredPosition.x, lowestY - step);
            }

            TextMeshProUGUI txt = cloneObj.GetComponentInChildren<TextMeshProUGUI>(true);
            if (txt != null)
            {
                if (sharedFont != null)
                {
                    txt.font = sharedFont;
                    if (sharedFont.material != null) txt.fontSharedMaterial = sharedFont.material;
                }
                txt.text = "ĐẤU ONLINE (1v1)";
            }

            Image img = cloneObj.GetComponent<Image>();
            if (img != null && img.sprite == null)
            {
                img.color = new Color(0.14f, 0.45f, 0.78f, 0.95f);
            }
        }
        else
        {
            Button fallbackBtn = CreateButton(
                menuRoot,
                "Btn_Multiplayer",
                "ĐẤU ONLINE (1v1)",
                new Vector2(0f, -210f),
                new Vector2(260f, 56f),
                new Color(0.14f, 0.45f, 0.78f, 0.96f),
                OpenLobbyPanel
            );
            fallbackBtn.gameObject.AddComponent<ButtonSound>();
        }
    }

    private void BuildLobbyUI()
    {
        if (isBuilt || targetCanvas == null) return;
        isBuilt = true;

        // Nền toàn màn hình (dùng ảnh Resources/UI/duel_1v1_load nếu có)
        lobbyPanelRoot = new GameObject("MultiplayerLobbyPanel", typeof(RectTransform), typeof(Image));
        lobbyPanelRoot.transform.SetParent(targetCanvas.transform, false);
        RectTransform rootRT = lobbyPanelRoot.GetComponent<RectTransform>();
        rootRT.anchorMin = Vector2.zero;
        rootRT.anchorMax = Vector2.one;
        rootRT.offsetMin = Vector2.zero;
        rootRT.offsetMax = Vector2.zero;

        Image rootBg = lobbyPanelRoot.GetComponent<Image>();
        Sprite lobbyBgSprite = LoadLobbyBackgroundSprite();
        if (lobbyBgSprite != null)
        {
            rootBg.sprite = lobbyBgSprite;
            rootBg.color = Color.white;
            rootBg.preserveAspect = false;

            // Lớp phủ tối nhẹ để ảnh nền nổi bật mà chữ vẫn dễ đọc
            GameObject dimObj = new GameObject("BackgroundDimOverlay", typeof(RectTransform), typeof(Image));
            dimObj.transform.SetParent(lobbyPanelRoot.transform, false);
            RectTransform dimRT = dimObj.GetComponent<RectTransform>();
            dimRT.anchorMin = Vector2.zero;
            dimRT.anchorMax = Vector2.one;
            dimRT.offsetMin = Vector2.zero;
            dimRT.offsetMax = Vector2.zero;
            Image dimImg = dimObj.GetComponent<Image>();
            dimImg.color = new Color(0.02f, 0.04f, 0.09f, 0.35f);
            dimImg.raycastTarget = false;
        }
        else
        {
            rootBg.color = new Color(0.03f, 0.05f, 0.1f, 0.88f);
        }

        if (mainMenuManager != null && mainMenuManager.multiplayerLobbyPanel == null)
        {
            mainMenuManager.multiplayerLobbyPanel = lobbyPanelRoot;
        }

        // Khung chính ở giữa màn hình
        GameObject card = new GameObject("LobbyCard", typeof(RectTransform), typeof(Image));
        card.transform.SetParent(lobbyPanelRoot.transform, false);
        RectTransform cardRT = card.GetComponent<RectTransform>();
        cardRT.anchorMin = new Vector2(0.5f, 0.5f);
        cardRT.anchorMax = new Vector2(0.5f, 0.5f);
        cardRT.sizeDelta = new Vector2(760f, 540f);
        card.GetComponent<Image>().color = new Color(0.06f, 0.09f, 0.16f, 0.84f);

        // Tiêu đề
        CreateText(
            card.transform,
            "TitleText",
            "ĐẤU ONLINE 1v1 - PHÒNG CHỜ",
            new Vector2(0f, 225f),
            new Vector2(700f, 44f),
            28f,
            FontStyles.Bold,
            new Color(0.98f, 0.86f, 0.42f, 1f),
            TextAlignmentOptions.Center
        );

        BuildConnectGroup(card.transform);
        BuildConnectingGroup(card.transform);
        BuildRoomGroup(card.transform);

        // Thanh trạng thái ở đáy khung
        GameObject statusBg = new GameObject("StatusBackdrop", typeof(RectTransform), typeof(Image));
        statusBg.transform.SetParent(card.transform, false);
        RectTransform sBgRT = statusBg.GetComponent<RectTransform>();
        sBgRT.anchoredPosition = new Vector2(0f, -225f);
        sBgRT.sizeDelta = new Vector2(700f, 48f);
        statusBg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

        statusText = CreateText(
            statusBg.transform,
            "StatusText",
            "Nhập ID Phòng (6 số) và Mật khẩu phòng để Tạo hoặc Vào phòng.",
            Vector2.zero,
            new Vector2(680f, 42f),
            16f,
            FontStyles.Bold,
            Color.white,
            TextAlignmentOptions.Center
        );
        statusText.enableWordWrapping = true;
    }

    private void BuildConnectGroup(Transform parent)
    {
        connectGroup = new GameObject("ConnectGroup", typeof(RectTransform));
        connectGroup.transform.SetParent(parent, false);
        RectTransform rt = connectGroup.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(680f, 360f);

        CreateText(connectGroup.transform, "LblName", "Tên người chơi:", new Vector2(-170f, 118f), new Vector2(210f, 32f), 18f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
        playerNameInput = CreateInputField(connectGroup.transform, "NameInput", "Nhập tên của bạn...", MatchSessionConfig.GetSavedPlayerName(), new Vector2(95f, 118f), new Vector2(360f, 42f));

        CreateText(connectGroup.transform, "LblRoomId", "ID Phòng (6 chữ số):", new Vector2(-170f, 56f), new Vector2(210f, 32f), 18f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
        roomIdInput = CreateInputField(connectGroup.transform, "RoomIdInput", "VD: 123456", MatchSessionConfig.GetSavedRoomId6(), new Vector2(32f, 56f), new Vector2(235f, 42f));
        roomIdInput.contentType = TMP_InputField.ContentType.IntegerNumber;
        roomIdInput.characterLimit = 6;
        CreateButton(connectGroup.transform, "BtnRandomRoomId", "TẠO MÃ", new Vector2(220f, 56f), new Vector2(115f, 42f), new Color(0.28f, 0.44f, 0.68f, 1f), OnClickRandomRoomId);

        CreateText(connectGroup.transform, "LblRoomPass", "Mật khẩu phòng:", new Vector2(-170f, -6f), new Vector2(210f, 32f), 18f, FontStyles.Bold, Color.white, TextAlignmentOptions.Left);
        roomPasswordInput = CreateInputField(connectGroup.transform, "RoomPassInput", "Để trống nếu không đặt mật khẩu...", MatchSessionConfig.GetSavedRoomPassword(), new Vector2(95f, -6f), new Vector2(360f, 42f));
        roomPasswordInput.characterLimit = 24;

        localDeckSummaryText = CreateText(
            connectGroup.transform,
            "LocalDeckSummary",
            "",
            new Vector2(0f, -44f),
            new Vector2(640f, 26f),
            15f,
            FontStyles.Bold,
            new Color(0.55f, 0.95f, 0.7f, 1f),
            TextAlignmentOptions.Center
        );

        CreateText(
            connectGroup.transform,
            "HintText",
            "Nhập chung ID Phòng 6 số & Mật khẩu phòng: 1 người bấm TẠO PHÒNG, người kia bấm VÀO PHÒNG (Hỗ trợ cả khác Wi-Fi/Internet & cùng mạng LAN).",
            new Vector2(0f, -74f),
            new Vector2(650f, 38f),
            13.5f,
            FontStyles.Italic,
            new Color(0.76f, 0.86f, 0.96f, 1f),
            TextAlignmentOptions.Center
        ).enableWordWrapping = true;

        CreateButton(connectGroup.transform, "BtnHost", "TẠO PHÒNG (HOST)", new Vector2(-150f, -126f), new Vector2(260f, 50f), new Color(0.18f, 0.62f, 0.32f, 1f), OnClickHost);
        CreateButton(connectGroup.transform, "BtnJoin", "VÀO PHÒNG (CLIENT)", new Vector2(150f, -126f), new Vector2(260f, 50f), new Color(0.18f, 0.48f, 0.82f, 1f), OnClickJoin);
        CreateButton(connectGroup.transform, "BtnBackMenu", "QUAY LẠI MENU", new Vector2(0f, -178f), new Vector2(200f, 38f), new Color(0.36f, 0.36f, 0.42f, 1f), OnClickBackToMenu);
    }

    private void BuildConnectingGroup(Transform parent)
    {
        connectingGroup = new GameObject("ConnectingGroup", typeof(RectTransform));
        connectingGroup.transform.SetParent(parent, false);
        RectTransform rt = connectingGroup.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(640f, 260f);

        CreateText(connectingGroup.transform, "ConnectingLabel", "ĐANG KẾT NỐI TỚI PHÒNG...", new Vector2(0f, 40f), new Vector2(560f, 44f), 24f, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
        CreateButton(connectingGroup.transform, "BtnCancelConnect", "HỦY KẾT NỐI", new Vector2(0f, -45f), new Vector2(220f, 48f), new Color(0.75f, 0.25f, 0.22f, 1f), OnClickLeaveRoom);

        connectingGroup.SetActive(false);
    }

    private void BuildRoomGroup(Transform parent)
    {
        roomGroup = new GameObject("RoomGroup", typeof(RectTransform));
        roomGroup.transform.SetParent(parent, false);
        RectTransform rt = roomGroup.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(700f, 370f);

        roomHeaderText = CreateText(roomGroup.transform, "RoomHeader", "PHÒNG 1v1 (1/2)", new Vector2(0f, 152f), new Vector2(640f, 34f), 22f, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
        roomAddressText = CreateText(roomGroup.transform, "RoomAddress", "", new Vector2(0f, 118f), new Vector2(660f, 28f), 15f, FontStyles.Normal, new Color(0.74f, 0.9f, 1f, 1f), TextAlignmentOptions.Center);

        // Slot 1: Host
        hostCardBg = CreateSlotCard(roomGroup.transform, "HostSlotCard", new Vector2(-165f, 15f), "NGƯỜI CHƠI 1 (HOST)", out hostNameText, out hostDeckText, out hostStatusText);

        // Slot 2: Client
        clientCardBg = CreateSlotCard(roomGroup.transform, "ClientSlotCard", new Vector2(165f, 15f), "NGƯỜI CHƠI 2 (CLIENT)", out clientNameText, out clientDeckText, out clientStatusText);

        readyButton = CreateButton(roomGroup.transform, "BtnReady", "SẴN SÀNG", new Vector2(-190f, -125f), new Vector2(200f, 50f), new Color(0.18f, 0.55f, 0.85f, 1f), OnClickToggleReady);
        readyButtonText = readyButton.GetComponentInChildren<TextMeshProUGUI>();

        startMatchButton = CreateButton(roomGroup.transform, "BtnStartMatch", "BẮT ĐẦU TRẬN", new Vector2(30f, -125f), new Vector2(200f, 50f), new Color(0.18f, 0.68f, 0.34f, 1f), OnClickStartMatch);

        CreateButton(roomGroup.transform, "BtnLeaveRoom", "RỜI PHÒNG", new Vector2(235f, -125f), new Vector2(160f, 50f), new Color(0.76f, 0.24f, 0.22f, 1f), OnClickLeaveRoom);

        roomGroup.SetActive(false);
    }

    private Image CreateSlotCard(Transform parent, string objName, Vector2 pos, string roleLabel, out TextMeshProUGUI nameTxt, out TextMeshProUGUI deckTxt, out TextMeshProUGUI stateTxt)
    {
        GameObject box = new GameObject(objName, typeof(RectTransform), typeof(Image));
        box.transform.SetParent(parent, false);
        RectTransform rt = box.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(305f, 168f);
        Image bg = box.GetComponent<Image>();
        bg.color = new Color(0.13f, 0.17f, 0.27f, 0.95f);

        CreateText(box.transform, "Role", roleLabel, new Vector2(0f, 58f), new Vector2(280f, 24f), 14f, FontStyles.Bold, new Color(0.75f, 0.84f, 0.98f, 1f), TextAlignmentOptions.Center);
        nameTxt = CreateText(box.transform, "PlayerName", "...", new Vector2(0f, 20f), new Vector2(280f, 34f), 22f, FontStyles.Bold, Color.white, TextAlignmentOptions.Center);
        deckTxt = CreateText(box.transform, "DeckInfo", "Bộ bài: --", new Vector2(0f, -14f), new Vector2(280f, 24f), 14f, FontStyles.Bold, new Color(0.72f, 0.92f, 1f, 1f), TextAlignmentOptions.Center);
        stateTxt = CreateText(box.transform, "ReadyState", "CHƯA SẴN SÀNG", new Vector2(0f, -52f), new Vector2(260f, 28f), 16f, FontStyles.Bold, new Color(1f, 0.82f, 0.36f, 1f), TextAlignmentOptions.Center);

        return bg;
    }

    // ==================== BUTTON HANDLERS ====================

    public void OpenLobbyPanel()
    {
        if (mainMenuManager != null)
        {
            mainMenuManager.OpenMultiplayerPanel();
        }
        else if (lobbyPanelRoot != null)
        {
            lobbyPanelRoot.SetActive(true);
        }
        RefreshUI();
    }

    private void OnClickBackToMenu()
    {
        if (lobbyManager != null && (lobbyManager.IsInRoom || lobbyManager.IsConnecting))
        {
            lobbyManager.LeaveRoom();
        }

        if (mainMenuManager != null)
        {
            mainMenuManager.BackToMainMenu();
        }
        else if (lobbyPanelRoot != null)
        {
            lobbyPanelRoot.SetActive(false);
        }
    }

    private void OnClickRandomRoomId()
    {
        if (roomIdInput != null)
        {
            roomIdInput.text = MatchSessionConfig.GenerateRandomRoomId6();
        }
    }

    private void OnClickHost()
    {
        if (lobbyManager == null) return;
        string playerName = playerNameInput != null ? playerNameInput.text : "Host";
        string roomId = roomIdInput != null ? roomIdInput.text.Trim() : "";
        if (string.IsNullOrEmpty(roomId))
        {
            roomId = MatchSessionConfig.GenerateRandomRoomId6();
            if (roomIdInput != null) roomIdInput.text = roomId;
        }
        string password = roomPasswordInput != null ? roomPasswordInput.text : "";
        lobbyManager.StartHostRoom(playerName, roomId, password);
        RefreshUI();
    }

    private void OnClickJoin()
    {
        if (lobbyManager == null) return;
        string playerName = playerNameInput != null ? playerNameInput.text : "Client";
        string roomId = roomIdInput != null ? roomIdInput.text.Trim() : "";
        string password = roomPasswordInput != null ? roomPasswordInput.text : "";
        lobbyManager.JoinRoom(playerName, roomId, password);
        RefreshUI();
    }

    private void OnClickToggleReady()
    {
        if (lobbyManager != null)
        {
            lobbyManager.ToggleLocalReady();
        }
    }

    private void OnClickStartMatch()
    {
        if (lobbyManager != null)
        {
            lobbyManager.RequestStartMatch();
        }
    }

    private void OnClickLeaveRoom()
    {
        if (lobbyManager != null)
        {
            lobbyManager.LeaveRoom();
        }
    }

    // ==================== REFRESH UI ====================

    private void HandleLobbyStateChanged(LobbyStateDto _)
    {
        RefreshUI();
    }

    private void HandleStatusChanged(string msg)
    {
        if (statusText != null)
        {
            statusText.text = msg;
        }
    }

    private void RefreshUI()
    {
        if (!isBuilt || lobbyManager == null) return;

        bool inRoom = lobbyManager.IsInRoom;
        bool connecting = (lobbyManager.IsConnecting || lobbyManager.IsBusyCreatingOrJoining) && !inRoom;

        if (connectGroup != null) connectGroup.SetActive(!inRoom && !connecting);
        if (connectingGroup != null) connectingGroup.SetActive(connecting);
        if (roomGroup != null) roomGroup.SetActive(inRoom);

        if (statusText != null)
        {
            statusText.text = lobbyManager.LastStatusMessage;
        }

        if (!inRoom)
        {
            if (localDeckSummaryText != null)
            {
                MatchSessionConfig.BuildLocalDeckJson(out int myDeckCnt, out bool myDeckOk, out string myDeckErr);
                localDeckSummaryText.text = myDeckOk
                    ? $"Bộ bài hiện tại của bạn: {myDeckCnt} lá (Hợp lệ)"
                    : $"Bộ bài chưa hợp lệ: {myDeckErr}";
                localDeckSummaryText.color = myDeckOk
                    ? new Color(0.5f, 0.95f, 0.65f, 1f)
                    : new Color(1f, 0.45f, 0.4f, 1f);
            }
            return;
        }

        LobbyStateDto st = lobbyManager.CurrentState ?? new LobbyStateDto();

        if (roomHeaderText != null)
        {
            roomHeaderText.text = $"PHÒNG 1v1 ({st.connectedCount}/{MultiplayerLobbyManager.MaxPlayers})";
        }

        if (roomAddressText != null)
        {
            string modeLabel = lobbyManager.IsUsingCloudRelay
                ? "CHẾ ĐỘ: INTERNET (MỌI MẠNG)"
                : (lobbyManager.IsHostMode ? $"CHẾ ĐỘ: LAN / CÙNG MÁY ({lobbyManager.HostLanIp})" : "CHẾ ĐỘ: LAN / CÙNG WI-FI");
            string passLabel = string.IsNullOrEmpty(lobbyManager.CurrentRoomPassword)
                ? "Không khóa"
                : lobbyManager.CurrentRoomPassword;

            roomAddressText.text = lobbyManager.IsHostMode
                ? $"ID PHÒNG: {lobbyManager.CurrentRoomId}   |   MẬT KHẨU: {passLabel}   |   {modeLabel}"
                : $"ID PHÒNG: {lobbyManager.CurrentRoomId}   |   {modeLabel}";
        }

        // Slot Host
        if (hostNameText != null) hostNameText.text = string.IsNullOrEmpty(st.hostName) ? "Host" : st.hostName;
        if (hostDeckText != null)
        {
            hostDeckText.text = st.hostDeckValid
                ? $"Bộ bài: {st.hostDeckCount} lá (Hợp lệ)"
                : $"Bộ bài: {st.hostDeckCount} lá (Chưa hợp lệ)";
            hostDeckText.color = st.hostDeckValid
                ? new Color(0.65f, 0.94f, 1f, 1f)
                : new Color(1f, 0.48f, 0.42f, 1f);
        }
        if (hostStatusText != null)
        {
            hostStatusText.text = st.hostReady ? "ĐÃ SẴN SÀNG" : "CHƯA SẴN SÀNG";
            hostStatusText.color = st.hostReady ? new Color(0.35f, 0.95f, 0.5f, 1f) : new Color(1f, 0.82f, 0.36f, 1f);
        }
        if (hostCardBg != null)
        {
            hostCardBg.color = st.hostReady
                ? new Color(0.11f, 0.25f, 0.19f, 0.96f)
                : new Color(0.13f, 0.17f, 0.27f, 0.95f);
        }

        // Slot Client
        if (st.hasClient)
        {
            if (clientNameText != null) clientNameText.text = string.IsNullOrEmpty(st.clientName) ? "Client" : st.clientName;
            if (clientDeckText != null)
            {
                clientDeckText.text = st.clientDeckValid
                    ? $"Bộ bài: {st.clientDeckCount} lá (Hợp lệ)"
                    : (st.clientDeckCount > 0 ? $"Bộ bài: {st.clientDeckCount} lá (Chưa hợp lệ)" : "Đang tải bộ bài...");
                clientDeckText.color = st.clientDeckValid
                    ? new Color(0.65f, 0.94f, 1f, 1f)
                    : new Color(1f, 0.68f, 0.42f, 1f);
            }
            if (clientStatusText != null)
            {
                clientStatusText.text = st.clientReady ? "ĐÃ SẴN SÀNG" : "CHƯA SẴN SÀNG";
                clientStatusText.color = st.clientReady ? new Color(0.35f, 0.95f, 0.5f, 1f) : new Color(1f, 0.82f, 0.36f, 1f);
            }
            if (clientCardBg != null)
            {
                clientCardBg.color = st.clientReady
                    ? new Color(0.11f, 0.25f, 0.19f, 0.96f)
                    : new Color(0.13f, 0.17f, 0.27f, 0.95f);
            }
        }
        else
        {
            if (clientNameText != null) clientNameText.text = "Trống";
            if (clientDeckText != null)
            {
                clientDeckText.text = "Bộ bài: --";
                clientDeckText.color = new Color(0.62f, 0.68f, 0.78f, 1f);
            }
            if (clientStatusText != null)
            {
                clientStatusText.text = "Đang chờ đối thủ...";
                clientStatusText.color = new Color(0.68f, 0.74f, 0.84f, 1f);
            }
            if (clientCardBg != null)
            {
                clientCardBg.color = new Color(0.11f, 0.13f, 0.2f, 0.82f);
            }
        }

        // Nút Ready & Start
        if (readyButtonText != null)
        {
            readyButtonText.text = lobbyManager.IsLocalReady ? "HỦY SẴN SÀNG" : "SẴN SÀNG";
        }
        if (readyButton != null)
        {
            Image rImg = readyButton.GetComponent<Image>();
            if (rImg != null)
            {
                rImg.color = lobbyManager.IsLocalReady
                    ? new Color(0.75f, 0.52f, 0.15f, 1f)
                    : new Color(0.18f, 0.55f, 0.85f, 1f);
            }
        }

        if (startMatchButton != null)
        {
            startMatchButton.gameObject.SetActive(lobbyManager.IsHostMode);
            startMatchButton.interactable = lobbyManager.CanHostStartMatch;
            Image sImg = startMatchButton.GetComponent<Image>();
            if (sImg != null)
            {
                sImg.color = lobbyManager.CanHostStartMatch
                    ? new Color(0.18f, 0.68f, 0.34f, 1f)
                    : new Color(0.24f, 0.32f, 0.28f, 0.65f);
            }
        }
    }

    // ==================== UI BUILDER HELPERS ====================

    private Sprite LoadLobbyBackgroundSprite()
    {
        Sprite sp = Resources.Load<Sprite>("UI/duel_1v1_load");
        if (sp != null) return sp;

        Texture2D tex = Resources.Load<Texture2D>("UI/duel_1v1_load");
        if (tex != null)
        {
            return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }
        return null;
    }

    private ushort ParsePortOrDefault(string raw)
    {
        if (ushort.TryParse(raw != null ? raw.Trim() : "", out ushort parsed) && parsed >= 1024)
        {
            return parsed;
        }
        return MatchSessionConfig.DefaultPort;
    }

    private TextMeshProUGUI CreateText(Transform parent, string objName, string content, Vector2 anchoredPos, Vector2 size, float fontSize, FontStyles style, Color color, TextAlignmentOptions align)
    {
        GameObject obj = new GameObject(objName, typeof(RectTransform), typeof(TextMeshProUGUI));
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        TextMeshProUGUI tmp = obj.GetComponent<TextMeshProUGUI>();
        if (sharedFont != null) tmp.font = sharedFont;
        tmp.text = content;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.alignment = align;
        tmp.raycastTarget = false;
        return tmp;
    }

    private Button CreateButton(Transform parent, string objName, string label, Vector2 anchoredPos, Vector2 size, Color bgColor, UnityEngine.Events.UnityAction onClick)
    {
        GameObject btnObj = new GameObject(objName, typeof(RectTransform), typeof(Image), typeof(Button));
        btnObj.transform.SetParent(parent, false);
        RectTransform rt = btnObj.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        Image img = btnObj.GetComponent<Image>();
        img.color = bgColor;

        Button btn = btnObj.GetComponent<Button>();
        btn.onClick.AddListener(() =>
        {
            if (SoundManager.Instance != null) SoundManager.Instance.PlayDefaultClick();
            onClick?.Invoke();
        });

        GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        txtObj.transform.SetParent(btnObj.transform, false);
        RectTransform txtRT = txtObj.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = new Vector2(6f, 2f);
        txtRT.offsetMax = new Vector2(-6f, -2f);

        TextMeshProUGUI tmp = txtObj.GetComponent<TextMeshProUGUI>();
        if (sharedFont != null) tmp.font = sharedFont;
        tmp.text = label;
        tmp.fontSize = 18f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        return btn;
    }

    private TMP_InputField CreateInputField(Transform parent, string objName, string placeholder, string initialValue, Vector2 anchoredPos, Vector2 size)
    {
        GameObject root = new GameObject(objName, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        root.transform.SetParent(parent, false);
        RectTransform rt = root.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        Image bg = root.GetComponent<Image>();
        bg.color = new Color(0.16f, 0.21f, 0.32f, 1f);

        GameObject textArea = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
        textArea.transform.SetParent(root.transform, false);
        RectTransform taRT = textArea.GetComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero;
        taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(12f, 4f);
        taRT.offsetMax = new Vector2(-12f, -4f);

        GameObject phObj = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
        phObj.transform.SetParent(textArea.transform, false);
        RectTransform phRT = phObj.GetComponent<RectTransform>();
        phRT.anchorMin = Vector2.zero;
        phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero;
        phRT.offsetMax = Vector2.zero;
        TextMeshProUGUI phTMP = phObj.GetComponent<TextMeshProUGUI>();
        if (sharedFont != null) phTMP.font = sharedFont;
        phTMP.text = placeholder;
        phTMP.fontSize = 18f;
        phTMP.fontStyle = FontStyles.Italic;
        phTMP.color = new Color(0.6f, 0.66f, 0.78f, 0.75f);
        phTMP.alignment = TextAlignmentOptions.MidlineLeft;
        phTMP.enableWordWrapping = false;

        GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        txtObj.transform.SetParent(textArea.transform, false);
        RectTransform txtRT = txtObj.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = Vector2.zero;
        txtRT.offsetMax = Vector2.zero;
        TextMeshProUGUI txtTMP = txtObj.GetComponent<TextMeshProUGUI>();
        if (sharedFont != null) txtTMP.font = sharedFont;
        txtTMP.fontSize = 18f;
        txtTMP.color = Color.white;
        txtTMP.alignment = TextAlignmentOptions.MidlineLeft;
        txtTMP.enableWordWrapping = false;

        TMP_InputField input = root.GetComponent<TMP_InputField>();
        input.targetGraphic = bg;
        input.textViewport = taRT;
        input.textComponent = txtTMP;
        input.placeholder = phTMP;
        if (sharedFont != null) input.fontAsset = sharedFont;
        input.caretColor = Color.white;
        input.selectionColor = new Color(0.25f, 0.55f, 0.95f, 0.45f);
        input.text = initialValue ?? "";

        return input;
    }
}
