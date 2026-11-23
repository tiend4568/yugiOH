using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using TMPro;

public class BattleManager : MonoBehaviour
{
    public static BattleManager Instance;

    public enum TurnState { DrawPhase, MainPhase, BattlePhase, EndPhase, GameOver }
    public TurnState currentState;

    public bool IsMultiplayer => MatchSessionConfig.IsMultiplayer;
    public string LocalDisplayName => MatchSessionConfig.GetLocalDisplayName();
    public string OpponentDisplayName => MatchSessionConfig.GetOpponentDisplayName();

    public int LocalDeckCount => IsMultiplayer ? networkMyDeckCount : playerDeck.Count;
    public int OpponentDeckCount => IsMultiplayer ? networkOpponentDeckCount : aiDeck.Count;
    public int OpponentHandCount => IsMultiplayer ? networkOpponentHandCount : aiHand.Count;

    [Header("Cấu hình luật đấu")]
    public int startingLP = 8000;
    public const int MAX_HAND_SIZE_END_PHASE = 6;
    public const float AFK_IDLE_GRACE_SECONDS = 10f;
    public const float AFK_COUNTDOWN_SECONDS = 35f;

    public CardDatabaseSO cardDatabase;
    public string cardResourceFolder = "Cards/Monster";
    public string spellResourceFolder = "Cards/Spell";
    public string trapResourceFolder = "Cards/Trap";

    [Header("Life Points (LP) UI")]
    public TextMeshProUGUI playerLPText;
    public TextMeshProUGUI aiLPText;
    public TextMeshProUGUI playerLPDamageText;
    public TextMeshProUGUI aiLPDamageText;
    public float lpRollDuration = 1.8f;

    [Header("Deck Count UI (Tùy chọn)")]
    public TextMeshProUGUI playerDeckCountText;
    public TextMeshProUGUI aiDeckCountText;

    [Header("Thông báo lượt đi")]
    public TextMeshProUGUI statusText;

    [Header("Bảng Kết Quả Thắng / Thua (Tùy chọn)")]
    public GameObject gameOverPanel;
    public TextMeshProUGUI gameOverTitleText;
    public TextMeshProUGUI gameOverReasonText;

    [Header("Dữ liệu trận đấu (Runtime)")]
    public List<CardData> playerDeck = new List<CardData>();
    public List<CardData> aiDeck = new List<CardData>();

    public List<CardData> playerHand = new List<CardData>();
    public List<CardData> aiHand = new List<CardData>();

    public List<CardData> playerGraveyard = new List<CardData>();
    public List<CardData> aiGraveyard = new List<CardData>();

    public CardData[] playerSpellZones = new CardData[Player.ZoneCount];
    public bool[] playerSpellFaceDown = new bool[Player.ZoneCount];
    public CardData[] aiSpellZones = new CardData[Player.ZoneCount];
    public bool[] aiSpellFaceDown = new bool[Player.ZoneCount];

    public Player humanPlayer;
    public Player aiPlayer;
    public Duel duel;

    private Canvas overlayCanvas;
    private RectTransform statusBoxRT;

    private Dictionary<string, Sprite> monsterSpriteCache = new Dictionary<string, Sprite>();
    private Dictionary<string, Sprite> cleanMonsterSpriteCache = new Dictionary<string, Sprite>();
    private Dictionary<string, Sprite> spellSpriteCache = new Dictionary<string, Sprite>();
    private Dictionary<string, Sprite> cleanSpellSpriteCache = new Dictionary<string, Sprite>();
    private Dictionary<string, Sprite> trapSpriteCache = new Dictionary<string, Sprite>();
    private Dictionary<string, Sprite> cleanTrapSpriteCache = new Dictionary<string, Sprite>();

    public const int KURIBOH_HAND_REACTION_ZONE = -100;

    private string pendingFlipCardName = "";
    private bool isGraveyardSelectionOpen = false;
    private bool isEndPhaseDiscardOpen = false;
    private TurnState stateBeforeEndPhaseDiscard = TurnState.MainPhase;
    private int pendingRaSummonZoneIndex = -1;
    private Coroutine delayedRaPromptCoroutine = null;

    // Bộ đếm Anti-AFK (10s không thao tác game -> đếm ngược 35s -> xử thua)
    private RectTransform afkBannerBoxRT;
    private Image afkBannerBg;
    private TextMeshProUGUI afkBannerText;
    private float localAfkIdleElapsed = 0f;
    private int lastDisplayedLocalAfkSeconds = -1;

    public bool IsSelectingEndPhaseDiscard => isEndPhaseDiscardOpen ||
        (IsMultiplayer && isNetworkPromptOpen && lastOpenedPromptType == NetPendingPromptType.EndPhaseDiscardHand);
    private NetPendingPromptType lastOpenedPromptType = NetPendingPromptType.None;

    private int displayedPlayerLP;
    private int displayedAILP;
    private Coroutine playerLPCoroutine;
    private Coroutine aiLPCoroutine;

    // Popup hỏi kích hoạt Bẫy trong Battle Phase của đối thủ
    private GameObject trapPromptPanel;
    private TextMeshProUGUI trapPromptText;
    private bool waitingTrapDecision = false;
    private bool playerAcceptedTrap = false;

    // Trạng thái đồng bộ Multiplayer (Bước 3, 4, 5)
    private int networkMyDeckCount = 0;
    private int networkOpponentDeckCount = 0;
    private int networkOpponentHandCount = 0;
    private int lastAppliedSnapshotSeq = 0;
    private bool hasAppliedInitialSnapshot = false;
    private int activeNetworkPromptId = 0;
    private int lastOpenedPromptId = -1;
    private bool isNetworkPromptOpen = false;
    private bool opponentLeftMultiplayerMatch = false;
    private readonly bool[] networkMySpellCanReact = new bool[Player.ZoneCount];
    private readonly string[] networkMySpellReactHint = new string[Player.ZoneCount];
    private Button endTurnButton;
    private TextMeshProUGUI endTurnButtonText;

    // Đồng bộ thời điểm va chạm của Quả cầu lửa với hiệu ứng vỡ đa giác vàng khi phá hủy quái thú
    private struct PendingAttackDestroySlot
    {
        public bool isPlayer;
        public int zoneIndex;
    }
    private List<PendingAttackDestroySlot> activeAttackDestroyBatch = null;
    private float activeAttackImpactDelay = 0f;
    private bool[] activeAttackDestroySfxFlag = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrapBattleScene()
    {
        SceneManager.sceneLoaded -= OnBattleSceneLoaded;
        SceneManager.sceneLoaded += OnBattleSceneLoaded;
        EnsureBattleManagerExists(SceneManager.GetActiveScene());
    }

    static void OnBattleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureBattleManagerExists(scene);
    }

    static void EnsureBattleManagerExists(Scene scene)
    {
        if (!scene.IsValid()) return;
        if (scene.name.Equals("BattleScene", StringComparison.OrdinalIgnoreCase))
        {
            if (FindObjectOfType<BattleManager>() == null)
            {
                new GameObject("AutoBattleManager", typeof(BattleManager));
            }
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
        Time.timeScale = 1f;
        if (startingLP <= 4000) startingLP = 8000;

        if (FindObjectOfType<EventSystem>() == null)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        if (cardDatabase == null)
        {
            cardDatabase = Resources.Load<CardDatabaseSO>("CardDatabase");
        }

        if (playerLPText == null)
        {
            GameObject pLP = GameObject.Find("Player_LB");
            if (pLP != null) playerLPText = pLP.GetComponent<TextMeshProUGUI>();
        }

        if (aiLPText == null)
        {
            GameObject aLP = GameObject.Find("AI_LB");
            if (aLP != null) aiLPText = aLP.GetComponent<TextMeshProUGUI>();
        }

        EnsureLPDamageTexts();

        BattleAutoBoard board = FindObjectOfType<BattleAutoBoard>();
        if (board == null)
        {
            board = gameObject.AddComponent<BattleAutoBoard>();
        }
        board.InitializeBoard();

        EnsureBattleHUD();
    }

    void EnsureLPDamageTexts()
    {
        if (playerLPDamageText == null && playerLPText != null)
        {
            playerLPDamageText = CreateLPDamageLabel(playerLPText, "PlayerLP_DamageText", placeAbove: true);
        }
        if (aiLPDamageText == null && aiLPText != null)
        {
            aiLPDamageText = CreateLPDamageLabel(aiLPText, "AILP_DamageText", placeAbove: false);
        }

        if (playerLPDamageText != null) playerLPDamageText.gameObject.SetActive(false);
        if (aiLPDamageText != null) aiLPDamageText.gameObject.SetActive(false);
    }

    TextMeshProUGUI CreateLPDamageLabel(TextMeshProUGUI baseLPText, string objName, bool placeAbove)
    {
        GameObject dmgObj = new GameObject(objName, typeof(RectTransform), typeof(TextMeshProUGUI));
        dmgObj.transform.SetParent(baseLPText.transform, false);

        RectTransform baseRT = baseLPText.GetComponent<RectTransform>();
        RectTransform rt = dmgObj.GetComponent<RectTransform>();

        if (placeAbove)
        {
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 6f);
        }
        else
        {
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -6f);
        }

        rt.sizeDelta = new Vector2(Mathf.Max(220f, baseRT.rect.width + 40f), 44f);

        GameObject bgObj = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bgObj.transform.SetParent(dmgObj.transform, false);
        bgObj.transform.SetAsFirstSibling();
        RectTransform bgRT = bgObj.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero;
        bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero;
        bgRT.offsetMax = Vector2.zero;

        Image bg = bgObj.GetComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.72f);
        bg.raycastTarget = false;

        TextMeshProUGUI tmp = dmgObj.GetComponent<TextMeshProUGUI>();
        if (baseLPText.font != null) tmp.font = baseLPText.font;
        tmp.fontSize = Mathf.Clamp(baseLPText.fontSize * 0.85f, 24f, 44f);
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = baseLPText.alignment;
        tmp.color = Color.white;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;

        return tmp;
    }

    public Canvas GetBattleCanvas()
    {
        if (overlayCanvas != null) return overlayCanvas;

        GameObject cObj = new GameObject("BattleOverlayCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        overlayCanvas = cObj.GetComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.sortingOrder = 10;

        CanvasScaler scaler = cObj.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        return overlayCanvas;
    }

    void EnsureBattleHUD()
    {
        Canvas canvas = GetBattleCanvas();
        if (canvas == null) return;

        // Thanh thông báo trạng thái: chữ trắng + khung chữ nhật đen bán trong suốt bao quanh
        if (statusText == null)
        {
            GameObject boxObj = new GameObject("AutoStatusBox", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            boxObj.transform.SetParent(canvas.transform, false);
            statusBoxRT = boxObj.GetComponent<RectTransform>();
            statusBoxRT.anchorMin = new Vector2(0.5f, 1f);
            statusBoxRT.anchorMax = new Vector2(0.5f, 1f);
            statusBoxRT.pivot = new Vector2(0.5f, 1f);
            statusBoxRT.anchoredPosition = new Vector2(0f, -12f);

            Image boxBg = boxObj.GetComponent<Image>();
            boxBg.color = new Color(0f, 0f, 0f, 0.74f);
            boxBg.raycastTarget = false;

            HorizontalLayoutGroup hlg = boxObj.GetComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(22, 22, 8, 8);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            ContentSizeFitter csf = boxObj.GetComponent<ContentSizeFitter>();
            csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject stObj = new GameObject("AutoStatusText", typeof(RectTransform), typeof(TextMeshProUGUI));
            stObj.transform.SetParent(boxObj.transform, false);

            statusText = stObj.GetComponent<TextMeshProUGUI>();
            statusText.fontSize = 21;
            statusText.fontStyle = FontStyles.Bold;
            statusText.alignment = TextAlignmentOptions.Center;
            statusText.color = Color.white;
            statusText.enableWordWrapping = false;
            statusText.raycastTarget = false;
        }
        else
        {
            statusText.color = Color.white;
            if (statusText.transform.parent == canvas.transform)
            {
                GameObject boxObj = new GameObject("AutoStatusBox", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
                boxObj.transform.SetParent(canvas.transform, false);
                statusBoxRT = boxObj.GetComponent<RectTransform>();
                statusBoxRT.anchorMin = new Vector2(0.5f, 1f);
                statusBoxRT.anchorMax = new Vector2(0.5f, 1f);
                statusBoxRT.pivot = new Vector2(0.5f, 1f);
                statusBoxRT.anchoredPosition = new Vector2(0f, -12f);

                Image boxBg = boxObj.GetComponent<Image>();
                boxBg.color = new Color(0f, 0f, 0f, 0.74f);
                boxBg.raycastTarget = false;

                HorizontalLayoutGroup hlg = boxObj.GetComponent<HorizontalLayoutGroup>();
                hlg.padding = new RectOffset(22, 22, 8, 8);
                hlg.childAlignment = TextAnchor.MiddleCenter;
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = false;

                ContentSizeFitter csf = boxObj.GetComponent<ContentSizeFitter>();
                csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                statusText.transform.SetParent(boxObj.transform, false);
            }
        }

        GameObject existingEndBtn = GameObject.Find("EndTurnButton");
        if (existingEndBtn == null)
        {
            GameObject btnObj = new GameObject("EndTurnButton", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(canvas.transform, false);
            RectTransform btnRT = btnObj.GetComponent<RectTransform>();
            btnRT.anchorMin = new Vector2(1f, 0.5f);
            btnRT.anchorMax = new Vector2(1f, 0.5f);
            btnRT.pivot = new Vector2(1f, 0.5f);
            btnRT.anchoredPosition = new Vector2(-25f, 0f);
            btnRT.sizeDelta = new Vector2(155f, 56f);

            btnObj.GetComponent<Image>().color = new Color(0.8f, 0.25f, 0.15f, 0.95f);
            endTurnButton = btnObj.GetComponent<Button>();
            endTurnButton.onClick.AddListener(EndPlayerTurn);

            GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtObj.transform.SetParent(btnObj.transform, false);
            RectTransform txtRT = txtObj.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;

            endTurnButtonText = txtObj.GetComponent<TextMeshProUGUI>();
            endTurnButtonText.text = "END TURN";
            endTurnButtonText.fontSize = 20;
            endTurnButtonText.fontStyle = FontStyles.Bold;
            endTurnButtonText.alignment = TextAlignmentOptions.Center;
            endTurnButtonText.color = Color.white;
        }
        else
        {
            endTurnButton = existingEndBtn.GetComponent<Button>();
            endTurnButtonText = existingEndBtn.GetComponentInChildren<TextMeshProUGUI>();
        }

        if (gameOverPanel == null)
        {
            gameOverPanel = new GameObject("AutoGameOverPanel", typeof(RectTransform), typeof(Image));
            gameOverPanel.transform.SetParent(canvas.transform, false);
            RectTransform panelRT = gameOverPanel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(460f, 260f);
            gameOverPanel.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.1f, 0.95f);

            GameObject titleBg = CreateTextBackdrop(gameOverPanel.transform, new Vector2(0f, 65f), new Vector2(360f, 52f));
            GameObject titleObj = new GameObject("TitleText", typeof(RectTransform), typeof(TextMeshProUGUI));
            titleObj.transform.SetParent(titleBg.transform, false);
            RectTransform tRT = titleObj.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero;
            tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero;
            tRT.offsetMax = Vector2.zero;
            gameOverTitleText = titleObj.GetComponent<TextMeshProUGUI>();
            gameOverTitleText.fontSize = 34;
            gameOverTitleText.fontStyle = FontStyles.Bold;
            gameOverTitleText.alignment = TextAlignmentOptions.Center;
            gameOverTitleText.color = Color.white;

            GameObject reasonObj = new GameObject("ReasonText", typeof(RectTransform), typeof(TextMeshProUGUI));
            reasonObj.transform.SetParent(gameOverPanel.transform, false);
            RectTransform rRT = reasonObj.GetComponent<RectTransform>();
            rRT.anchoredPosition = new Vector2(0f, 10f);
            rRT.sizeDelta = new Vector2(420f, 45f);
            gameOverReasonText = reasonObj.GetComponent<TextMeshProUGUI>();
            gameOverReasonText.fontSize = 20;
            gameOverReasonText.alignment = TextAlignmentOptions.Center;
            gameOverReasonText.color = Color.white;

            CreatePopupButton(gameOverPanel.transform, "CHƠI LẠI", new Vector2(-100f, -65f), new Color(0.2f, 0.65f, 0.3f, 1f), RestartBattle);
            CreatePopupButton(gameOverPanel.transform, "VỀ MENU", new Vector2(100f, -65f), new Color(0.75f, 0.25f, 0.2f, 1f), ReturnToMainMenu);
        }

        if (trapPromptPanel == null)
        {
            trapPromptPanel = new GameObject("AutoTrapPromptPanel", typeof(RectTransform), typeof(Image));
            trapPromptPanel.transform.SetParent(canvas.transform, false);
            RectTransform tpRT = trapPromptPanel.GetComponent<RectTransform>();
            tpRT.anchorMin = new Vector2(0.5f, 0.5f);
            tpRT.anchorMax = new Vector2(0.5f, 0.5f);
            tpRT.sizeDelta = new Vector2(480f, 190f);
            trapPromptPanel.GetComponent<Image>().color = new Color(0.18f, 0.04f, 0.16f, 0.96f);

            GameObject msgBg = CreateTextBackdrop(trapPromptPanel.transform, new Vector2(0f, 34f), new Vector2(440f, 70f));
            GameObject msgObj = new GameObject("TrapPromptText", typeof(RectTransform), typeof(TextMeshProUGUI));
            msgObj.transform.SetParent(msgBg.transform, false);
            RectTransform mRT = msgObj.GetComponent<RectTransform>();
            mRT.anchorMin = Vector2.zero;
            mRT.anchorMax = Vector2.one;
            mRT.offsetMin = new Vector2(8f, 4f);
            mRT.offsetMax = new Vector2(-8f, -4f);
            trapPromptText = msgObj.GetComponent<TextMeshProUGUI>();
            trapPromptText.fontSize = 20;
            trapPromptText.fontStyle = FontStyles.Bold;
            trapPromptText.alignment = TextAlignmentOptions.Center;
            trapPromptText.color = Color.white;

            CreatePopupButton(trapPromptPanel.transform, "KÍCH HOẠT", new Vector2(-105f, -45f), new Color(0.78f, 0.18f, 0.55f, 1f), () =>
            {
                RecordLocalValidGameAction();
                trapPromptPanel.SetActive(false);
                if (IsMultiplayer)
                {
                    SubmitMultiplayerPromptChoice(1);
                }
                else if (pendingRaSummonZoneIndex >= 0)
                {
                    int raZone = pendingRaSummonZoneIndex;
                    pendingRaSummonZoneIndex = -1;
                    PlayerActivateMonsterFieldEffect(raZone, -1, null);
                }
                else
                {
                    playerAcceptedTrap = true;
                    waitingTrapDecision = false;
                }
            });

            CreatePopupButton(trapPromptPanel.transform, "BỎ QUA", new Vector2(105f, -45f), new Color(0.32f, 0.32f, 0.38f, 1f), () =>
            {
                RecordLocalValidGameAction();
                trapPromptPanel.SetActive(false);
                if (IsMultiplayer)
                {
                    SubmitMultiplayerPromptChoice(0);
                }
                else if (pendingRaSummonZoneIndex >= 0)
                {
                    pendingRaSummonZoneIndex = -1;
                    SetStatus("Bạn giữ lại LP để dùng hiệu ứng trả 1000 LP phá hủy quái của Rồng Cánh Thần Của Ra!");
                }
                else
                {
                    playerAcceptedTrap = false;
                    waitingTrapDecision = false;
                }
            });

            trapPromptPanel.SetActive(false);
        }

        if (afkBannerBoxRT == null)
        {
            GameObject afkObj = new GameObject("AutoAfkCountdownBox", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            afkObj.transform.SetParent(canvas.transform, false);
            afkBannerBoxRT = afkObj.GetComponent<RectTransform>();
            afkBannerBoxRT.anchorMin = new Vector2(0.5f, 1f);
            afkBannerBoxRT.anchorMax = new Vector2(0.5f, 1f);
            afkBannerBoxRT.pivot = new Vector2(0.5f, 1f);
            afkBannerBoxRT.anchoredPosition = new Vector2(0f, -64f);

            afkBannerBg = afkObj.GetComponent<Image>();
            afkBannerBg.color = new Color(0.76f, 0.14f, 0.10f, 0.94f);
            afkBannerBg.raycastTarget = false;

            HorizontalLayoutGroup ahlg = afkObj.GetComponent<HorizontalLayoutGroup>();
            ahlg.padding = new RectOffset(20, 20, 6, 6);
            ahlg.childAlignment = TextAnchor.MiddleCenter;
            ahlg.childControlWidth = true;
            ahlg.childControlHeight = true;
            ahlg.childForceExpandWidth = false;
            ahlg.childForceExpandHeight = false;

            ContentSizeFitter acsf = afkObj.GetComponent<ContentSizeFitter>();
            acsf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            acsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GameObject atObj = new GameObject("AutoAfkCountdownText", typeof(RectTransform), typeof(TextMeshProUGUI));
            atObj.transform.SetParent(afkObj.transform, false);
            afkBannerText = atObj.GetComponent<TextMeshProUGUI>();
            afkBannerText.fontSize = 18;
            afkBannerText.fontStyle = FontStyles.Bold;
            afkBannerText.alignment = TextAlignmentOptions.Center;
            afkBannerText.color = new Color(1f, 0.95f, 0.72f, 1f);
            afkBannerText.enableWordWrapping = false;
            afkBannerText.raycastTarget = false;

            afkObj.SetActive(false);
        }
    }

    GameObject CreateTextBackdrop(Transform parent, Vector2 anchoredPos, Vector2 size)
    {
        GameObject bgObj = new GameObject("TextBackdrop", typeof(RectTransform), typeof(Image));
        bgObj.transform.SetParent(parent, false);
        RectTransform rt = bgObj.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        Image img = bgObj.GetComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.74f);
        img.raycastTarget = false;
        return bgObj;
    }

    void CreatePopupButton(Transform parent, string label, Vector2 anchoredPos, Color bgColor, UnityEngine.Events.UnityAction onClick)
    {
        GameObject btnObj = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        btnObj.transform.SetParent(parent, false);
        RectTransform rt = btnObj.GetComponent<RectTransform>();
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(165f, 48f);
        btnObj.GetComponent<Image>().color = bgColor;
        btnObj.GetComponent<Button>().onClick.AddListener(onClick);

        GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        txtObj.transform.SetParent(btnObj.transform, false);
        RectTransform txtRT = txtObj.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = Vector2.zero;
        txtRT.offsetMax = Vector2.zero;
        TextMeshProUGUI txt = txtObj.GetComponent<TextMeshProUGUI>();
        txt.text = label;
        txt.fontSize = 18;
        txt.fontStyle = FontStyles.Bold;
        txt.alignment = TextAlignmentOptions.Center;
        txt.color = Color.white;
    }

    void Start()
    {
        if (gameOverPanel != null) gameOverPanel.SetActive(false);
        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        CacheAllCardSprites();

        if (IsMultiplayer)
        {
            InitializeMultiplayerWaitState();
        }
        else
        {
            InitializeBattle();
        }
    }

    void OnDestroy()
    {
        if (MultiplayerLobbyManager.Instance != null)
        {
            MultiplayerLobbyManager.Instance.OnMatchDisconnect -= HandleMultiplayerDisconnect;
        }
    }

    void CacheAllCardSprites()
    {
        monsterSpriteCache.Clear();
        cleanMonsterSpriteCache.Clear();
        spellSpriteCache.Clear();
        cleanSpellSpriteCache.Clear();
        trapSpriteCache.Clear();
        cleanTrapSpriteCache.Clear();

        Sprite[] monsterSprites = Resources.LoadAll<Sprite>(cardResourceFolder);
        foreach (var s in monsterSprites)
        {
            if (s == null) continue;
            monsterSpriteCache[s.name] = s;
            string cleanKey = CardDatabaseSO.ExtractCleanName(s.name).ToLower();
            if (!cleanMonsterSpriteCache.ContainsKey(cleanKey))
                cleanMonsterSpriteCache[cleanKey] = s;
        }

        if (!string.IsNullOrEmpty(spellResourceFolder))
        {
            Sprite[] spellSprites = Resources.LoadAll<Sprite>(spellResourceFolder);
            foreach (var s in spellSprites)
            {
                if (s == null) continue;
                spellSpriteCache[s.name] = s;
                string cleanKey = CardDatabaseSO.ExtractCleanName(s.name).ToLower();
                if (!cleanSpellSpriteCache.ContainsKey(cleanKey))
                    cleanSpellSpriteCache[cleanKey] = s;
            }
        }

        if (!string.IsNullOrEmpty(trapResourceFolder))
        {
            Sprite[] trapSprites = Resources.LoadAll<Sprite>(trapResourceFolder);
            foreach (var s in trapSprites)
            {
                if (s == null) continue;
                trapSpriteCache[s.name] = s;
                string cleanKey = CardDatabaseSO.ExtractCleanName(s.name).ToLower();
                if (!cleanTrapSpriteCache.ContainsKey(cleanKey))
                    cleanTrapSpriteCache[cleanKey] = s;
            }
        }
    }

    public Sprite GetResolvedSprite(string cardName, bool preferSpell = false)
    {
        if (string.IsNullOrEmpty(cardName)) return null;
        string cleanKey = CardDatabaseSO.ExtractCleanName(cardName).ToLower();

        if (preferSpell)
        {
            if (spellSpriteCache.TryGetValue(cardName, out Sprite s1)) return s1;
            if (cleanSpellSpriteCache.TryGetValue(cleanKey, out Sprite s2)) return s2;
            if (trapSpriteCache.TryGetValue(cardName, out Sprite t1)) return t1;
            if (cleanTrapSpriteCache.TryGetValue(cleanKey, out Sprite t2)) return t2;
        }

        if (monsterSpriteCache.TryGetValue(cardName, out Sprite m1)) return m1;
        if (cleanMonsterSpriteCache.TryGetValue(cleanKey, out Sprite m2)) return m2;
        if (spellSpriteCache.TryGetValue(cardName, out Sprite s3)) return s3;
        if (cleanSpellSpriteCache.TryGetValue(cleanKey, out Sprite s4)) return s4;
        if (trapSpriteCache.TryGetValue(cardName, out Sprite t3)) return t3;
        if (cleanTrapSpriteCache.TryGetValue(cleanKey, out Sprite t4)) return t4;

        return null;
    }

    public CardData BuildCardDataByName(string cardName)
    {
        return CreateCardData(cardName);
    }

    public List<CardData> BuildDeckFromJson(string deckJson)
    {
        List<CardData> deck = new List<CardData>();
        if (string.IsNullOrWhiteSpace(deckJson)) return deck;

        DeckSaveWrapper wrapper = JsonUtility.FromJson<DeckSaveWrapper>(deckJson);
        if (wrapper == null || wrapper.cards == null) return deck;

        foreach (var item in wrapper.cards)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.cardName) || item.count <= 0) continue;
            for (int i = 0; i < item.count; i++)
            {
                deck.Add(CreateCardData(item.cardName));
            }
        }

        return deck;
    }

    void InitializeMultiplayerWaitState()
    {
        isGraveyardSelectionOpen = false;
        playerDeck.Clear();
        aiDeck.Clear();
        playerHand.Clear();
        aiHand.Clear();
        playerGraveyard.Clear();
        aiGraveyard.Clear();

        Array.Clear(playerSpellZones, 0, playerSpellZones.Length);
        Array.Clear(playerSpellFaceDown, 0, playerSpellFaceDown.Length);
        Array.Clear(aiSpellZones, 0, aiSpellZones.Length);
        Array.Clear(aiSpellFaceDown, 0, aiSpellFaceDown.Length);

        humanPlayer = new Player(LocalDisplayName) { lifePoints = startingLP };
        aiPlayer = new Player(OpponentDisplayName) { lifePoints = startingLP };
        displayedPlayerLP = startingLP;
        displayedAILP = startingLP;

        if (playerLPDamageText != null) playerLPDamageText.gameObject.SetActive(false);
        if (aiLPDamageText != null) aiLPDamageText.gameObject.SetActive(false);

        // Khởi tạo Duel giữ chỗ để BattleAutoBoard không bị null khi chờ Bước 3 đồng bộ Snapshot
        duel = new Duel(humanPlayer, aiPlayer);
        currentState = TurnState.DrawPhase;
        lastAppliedSnapshotSeq = 0;
        hasAppliedInitialSnapshot = false;
        opponentLeftMultiplayerMatch = false;
        networkMyDeckCount = MatchSessionConfig.LocalIsHost ? MatchSessionConfig.HostDeckCount : MatchSessionConfig.ClientDeckCount;
        networkOpponentDeckCount = MatchSessionConfig.LocalIsHost ? MatchSessionConfig.ClientDeckCount : MatchSessionConfig.HostDeckCount;
        networkOpponentHandCount = 0;

        UpdateLPUI();
        UpdateDeckCountUI();
        EnsureMultiplayerLeaveButton();
        RefreshEndTurnButtonState();

        if (MultiplayerLobbyManager.Instance != null)
        {
            MultiplayerLobbyManager.Instance.OnMatchDisconnect -= HandleMultiplayerDisconnect;
            MultiplayerLobbyManager.Instance.OnMatchDisconnect += HandleMultiplayerDisconnect;
        }

        if (!MatchSessionConfig.LocalIsHost)
        {
            SetStatus($"Đang nhận trạng thái bàn đấu khởi đầu từ Host ({OpponentDisplayName})...");
        }

        // Khởi chạy NetworkDuelController để rút 5 lá mở màn và đồng bộ Snapshot (Bước 3)
        NetworkDuelController.EnsureInstance().InitializeForBattleScene(this);
    }

    public void ApplyNetworkSnapshot(MatchStateSnapshot snap)
    {
        if (snap == null) return;
        if (hasAppliedInitialSnapshot && snap.snapshotSeq < lastAppliedSnapshotSeq) return;

        lastAppliedSnapshotSeq = snap.snapshotSeq;

        if (humanPlayer == null) humanPlayer = new Player(snap.myName) { lifePoints = snap.myLP };
        if (aiPlayer == null) aiPlayer = new Player(snap.opponentName) { lifePoints = snap.opponentLP };
        if (duel == null) duel = new Duel(humanPlayer, aiPlayer);

        humanPlayer.name = string.IsNullOrWhiteSpace(snap.myName) ? LocalDisplayName : snap.myName;
        aiPlayer.name = string.IsNullOrWhiteSpace(snap.opponentName) ? OpponentDisplayName : snap.opponentName;

        int prevMyLP = humanPlayer.lifePoints;
        int prevOppLP = aiPlayer.lifePoints;
        humanPlayer.lifePoints = snap.myLP;
        aiPlayer.lifePoints = snap.opponentLP;

        if (snap.isRematchReset || (!snap.isGameOver && gameOverPanel != null && gameOverPanel.activeSelf))
        {
            if (gameOverPanel != null) gameOverPanel.SetActive(false);
            if (playerLPCoroutine != null) { StopCoroutine(playerLPCoroutine); playerLPCoroutine = null; }
            if (aiLPCoroutine != null) { StopCoroutine(aiLPCoroutine); aiLPCoroutine = null; }
            if (playerLPDamageText != null) playerLPDamageText.gameObject.SetActive(false);
            if (aiLPDamageText != null) aiLPDamageText.gameObject.SetActive(false);
            if (BattleAutoBoard.Instance != null) BattleAutoBoard.Instance.ClearVfxAndHolds();
            displayedPlayerLP = snap.myLP;
            displayedAILP = snap.opponentLP;
            UpdateLPUI();
        }
        else if (!hasAppliedInitialSnapshot)
        {
            displayedPlayerLP = snap.myLP;
            displayedAILP = snap.opponentLP;
            UpdateLPUI();
        }
        else
        {
            if (snap.myLP != prevMyLP)
            {
                int dmg = Mathf.Max(0, prevMyLP - snap.myLP);
                HandleDamage(humanPlayer, dmg);
            }
            if (snap.opponentLP != prevOppLP)
            {
                int dmg = Mathf.Max(0, prevOppLP - snap.opponentLP);
                HandleDamage(aiPlayer, dmg);
            }
        }

        humanPlayer.normalSummonUsed = snap.myNormalSummonUsed;
        humanPlayer.swordsBlockTurnsRemaining = snap.mySwordsBlockTurns;
        aiPlayer.normalSummonUsed = snap.opponentNormalSummonUsed;
        aiPlayer.swordsBlockTurnsRemaining = snap.opponentSwordsBlockTurns;

        // Đồng bộ lượt: players[0] luôn là humanPlayer (bên dưới), players[1] luôn là aiPlayer (đối thủ bên trên)
        duel.turnCount = Mathf.Max(1, snap.turnCount);
        duel.currentIndex = snap.isMyTurn ? 0 : 1;
        TurnState syncedState = (TurnState)snap.turnState;
        // Bảo đảm khi không kết thúc trận và không ở BattlePhase thì luôn ở MainPhase để người giữ lượt thao tác bình thường
        if (!snap.isGameOver && syncedState != TurnState.BattlePhase)
        {
            syncedState = TurnState.MainPhase;
        }
        currentState = syncedState;

        networkMyDeckCount = snap.myDeckCount;
        networkOpponentDeckCount = snap.opponentDeckCount;
        networkOpponentHandCount = snap.opponentHandCount;

        // 1. Tay bài của mình
        playerHand.Clear();
        if (snap.myHandCardNames != null)
        {
            foreach (string cName in snap.myHandCardNames)
            {
                if (!string.IsNullOrEmpty(cName))
                {
                    playerHand.Add(CreateCardData(cName));
                }
            }
        }

        // Tay bài đối thủ (chỉ giữ số lượng ẩn danh)
        aiHand.Clear();
        for (int i = 0; i < snap.opponentHandCount; i++)
        {
            aiHand.Add(new CardData("HiddenCard", null, 0, 0, 1, "Lá bài trên tay đối thủ."));
        }
        SyncPlayerHandCounts();

        // 2. Mộ bài 2 bên
        playerGraveyard.Clear();
        if (snap.myGraveyardCardNames != null)
        {
            foreach (string cName in snap.myGraveyardCardNames)
            {
                if (!string.IsNullOrEmpty(cName)) playerGraveyard.Add(CreateCardData(cName));
            }
        }

        aiGraveyard.Clear();
        if (snap.opponentGraveyardCardNames != null)
        {
            foreach (string cName in snap.opponentGraveyardCardNames)
            {
                if (!string.IsNullOrEmpty(cName)) aiGraveyard.Add(CreateCardData(cName));
            }
        }

        // Lưu tạm Sprite cũ trên các ô quái trước khi ghi đè snapshot để dùng cho hiệu ứng giữ lá bài chờ cầu lửa va chạm
        Sprite[] prevMySprites = new Sprite[Player.ZoneCount];
        Sprite[] prevOppSprites = new Sprite[Player.ZoneCount];
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (humanPlayer.monsterZones[i] != null && humanPlayer.monsterZones[i].data != null)
                prevMySprites[i] = humanPlayer.monsterZones[i].data.cardSprite;
            if (aiPlayer.monsterZones[i] != null && aiPlayer.monsterZones[i].data != null)
                prevOppSprites[i] = aiPlayer.monsterZones[i].data.cardSprite;
        }

        // 3. 5 ô Quái thú của mình & đối thủ
        ApplySnapshotMonsterZones(humanPlayer, snap.myMonsters, isOpponent: false);
        ApplySnapshotMonsterZones(aiPlayer, snap.opponentMonsters, isOpponent: true);

        // 4. 5 ô Phép/Bẫy của mình & đối thủ
        ApplySnapshotSpellZones(playerSpellZones, playerSpellFaceDown, snap.mySpells, isOpponent: false);
        ApplySnapshotSpellZones(aiSpellZones, aiSpellFaceDown, snap.opponentSpells, isOpponent: true);

        hasAppliedInitialSnapshot = true;

        UpdateDeckCountUI();
        RefreshEndTurnButtonState();

        // 5. Kích hoạt hiệu ứng Quả cầu lửa (Attack) & Vỡ đa giác vàng (Destroy) đồng bộ từ Host
        bool deferDestroySfxToImpact = TriggerNetworkSnapshotVfx(snap, prevMySprites, prevOppSprites);

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.ForceRefreshHand();
        }

        if (snap.sfxList != null && snap.sfxList.Count > 0)
        {
            foreach (string sfx in snap.sfxList)
            {
                if (!string.IsNullOrEmpty(sfx) && sfx != "Damage")
                {
                    if (deferDestroySfxToImpact && sfx == "Destroy") continue;
                    PlayBattleSFX(sfx);
                }
            }
        }
        else if (!string.IsNullOrEmpty(snap.sfxToPlay) && snap.sfxToPlay != "Damage")
        {
            if (!(deferDestroySfxToImpact && snap.sfxToPlay == "Destroy"))
            {
                PlayBattleSFX(snap.sfxToPlay);
            }
        }

        if (!string.IsNullOrEmpty(snap.statusMessage))
        {
            SetStatus(snap.statusMessage);
        }

        // Xử lý Prompt bất đồng bộ (Bước 5: FLIP chọn Mộ / Hỏi kích hoạt Bẫy / Bỏ bài End Phase)
        ApplySnapshotPendingPrompt(snap);

        // Cập nhật đồng hồ đếm ngược Anti-AFK (nếu đã quá 10s không thao tác)
        UpdateAfkCountdownBanner(
            snap.afkCountdownSeconds,
            snap.isAfkCountdownForMe,
            snap.isAfkCountdownForMe ? LocalDisplayName : OpponentDisplayName
        );

        if (snap.isGameOver)
        {
            UpdateAfkCountdownBanner(-1, false);
            ShowMultiplayerGameOver(snap.didLocalWin, snap.gameOverReason);
        }
    }

    private void ApplySnapshotPendingPrompt(MatchStateSnapshot snap)
    {
        activeNetworkPromptId = snap.pendingPromptId;
        NetPendingPromptType pType = (NetPendingPromptType)snap.pendingPromptType;

        if (pType == NetPendingPromptType.None || snap.isGameOver)
        {
            CancelDelayedRaPrompt();
            if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
            isNetworkPromptOpen = false;
            lastOpenedPromptType = NetPendingPromptType.None;
            isGraveyardSelectionOpen = false;
            isEndPhaseDiscardOpen = false;
            if (BattleAutoBoard.Instance != null)
            {
                BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
                BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
            }
            return;
        }

        if (!snap.isPromptForMe)
        {
            CancelDelayedRaPrompt();
            if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
            isNetworkPromptOpen = false;
            lastOpenedPromptType = NetPendingPromptType.None;
            isGraveyardSelectionOpen = false;
            isEndPhaseDiscardOpen = false;
            if (BattleAutoBoard.Instance != null)
            {
                BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
                BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
            }
            return;
        }

        lastOpenedPromptType = pType;

        if (pType == NetPendingPromptType.FlipSelectGraveyardToDeck)
        {
            if (!isNetworkPromptOpen || lastOpenedPromptId != snap.pendingPromptId)
            {
                isNetworkPromptOpen = true;
                lastOpenedPromptId = snap.pendingPromptId;
                isGraveyardSelectionOpen = true;

                if (BattleAutoBoard.Instance != null && playerGraveyard.Count > 0)
                {
                    BattleAutoBoard.Instance.OpenGraveyardSelection(
                        playerGraveyard,
                        gyIndex => SubmitMultiplayerPromptChoice(gyIndex),
                        customTitle: string.IsNullOrEmpty(snap.promptTitle)
                            ? "[FLIP] CHỌN 1 LÁ TRONG MỘ TRẢ VỀ DECK"
                            : snap.promptTitle
                    );
                }
                else
                {
                    SubmitMultiplayerPromptChoice(0);
                }
            }
        }
        else if (pType == NetPendingPromptType.TrapMagicalHats || pType == NetPendingPromptType.TrapHoleOnSummon)
        {
            if (!isNetworkPromptOpen || lastOpenedPromptId != snap.pendingPromptId)
            {
                isNetworkPromptOpen = true;
                lastOpenedPromptId = snap.pendingPromptId;
                if (trapPromptPanel != null) trapPromptPanel.SetActive(false);

                if (BattleAutoBoard.Instance != null)
                {
                    bool isTrapHoleOnly = (pType == NetPendingPromptType.TrapHoleOnSummon);
                    string modalTitle = string.IsNullOrEmpty(snap.promptTitle)
                        ? (isTrapHoleOnly
                            ? "ĐỐI THỦ VỪA TRIỆU HỒI QUÁI THÚ (ATK >= 1000)! CHỌN KÍCH HOẠT [HANG RƠI] HOẶC BỎ QUA"
                            : "ĐỐI THỦ TẤN CÔNG! CHỌN KÍCH HOẠT BÀI ÚP / KURIBOH HOẶC BỎ QUA")
                        : snap.promptTitle;

                    BattleAutoBoard.Instance.OpenDefenseReactionModal(
                        modalTitle,
                        chosenSpellZone => HandleMultiplayerDefenseReactionZonePicked(chosenSpellZone),
                        isTrapHolePrompt: isTrapHoleOnly
                    );
                }
                else
                {
                    SubmitMultiplayerDefenseReaction(-1, -1);
                }
            }
        }
        else if (pType == NetPendingPromptType.RaSummonPayLP)
        {
            if (!isNetworkPromptOpen || lastOpenedPromptId != snap.pendingPromptId)
            {
                isNetworkPromptOpen = true;
                lastOpenedPromptId = snap.pendingPromptId;
                if (trapPromptPanel != null)
                {
                    string raText = string.IsNullOrEmpty(snap.promptTitle)
                        ? "Kích hoạt [Rồng Cánh Thần Của Ra]: Trả LP chỉ còn 100 để tăng ATK/DEF?"
                        : snap.promptTitle;
                    float delay = (GodSummonVfxController.Instance != null && GodSummonVfxController.Instance.IsPlaying) ? 1.85f : 0f;
                    ShowRaPromptPanel(raText, delay);
                }
                else
                {
                    SubmitMultiplayerPromptChoice(1);
                }
            }
        }
        else if (pType == NetPendingPromptType.EndPhaseDiscardHand)
        {
            if (!isNetworkPromptOpen || lastOpenedPromptId != snap.pendingPromptId)
            {
                isNetworkPromptOpen = true;
                lastOpenedPromptId = snap.pendingPromptId;
                isGraveyardSelectionOpen = true;
                isEndPhaseDiscardOpen = true;

                int excess = Mathf.Max(1, playerHand.Count - MAX_HAND_SIZE_END_PHASE);
                string discardTitle = string.IsNullOrEmpty(snap.promptTitle)
                    ? $"[END PHASE - GIỚI HẠN {MAX_HAND_SIZE_END_PHASE} LÁ TRÊN TAY] BẠN ĐANG CÓ {playerHand.Count} LÁ — CHỌN BỎ {excess} LÁ XUỐNG MỘ"
                    : snap.promptTitle;

                if (BattleAutoBoard.Instance != null && playerHand.Count > MAX_HAND_SIZE_END_PHASE)
                {
                    BattleAutoBoard.Instance.OpenGraveyardSelection(
                        playerHand,
                        handIdx => SubmitMultiplayerPromptChoice(handIdx),
                        customTitle: discardTitle,
                        filter: null,
                        onCancelled: CancelEndPhaseDiscardAndReturnToTurn,
                        cancelButtonLabel: "HỦY KẾT THÚC LƯỢT (QUAY LẠI)"
                    );
                }
                else
                {
                    SubmitMultiplayerPromptChoice(0);
                }
            }
        }
    }

    private void HandleMultiplayerDefenseReactionZonePicked(int chosenSpellZone)
    {
        if (chosenSpellZone == KURIBOH_HAND_REACTION_ZONE)
        {
            SubmitMultiplayerDefenseReaction(KURIBOH_HAND_REACTION_ZONE, -1);
            return;
        }

        if (chosenSpellZone < 0 || chosenSpellZone >= Player.ZoneCount)
        {
            SubmitMultiplayerDefenseReaction(-1, -1);
            return;
        }

        CardData chosenCard = playerSpellZones[chosenSpellZone];
        if (chosenCard != null && chosenCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            if (BattleAutoBoard.Instance != null && playerGraveyard.Any(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned))
            {
                isGraveyardSelectionOpen = true;
                SetStatus($"[{chosenCard.cardName}] Chọn 1 quái thú trong Mộ bài để hồi sinh ngay (hoặc bấm Quay lại / Hủy)!");
                BattleAutoBoard.Instance.OpenGraveyardSelection(
                    playerGraveyard,
                    gyIndex =>
                    {
                        isGraveyardSelectionOpen = false;
                        SubmitMultiplayerDefenseReaction(chosenSpellZone, gyIndex);
                    },
                    customTitle: $"[{chosenCard.cardName}] CHỌN QUÁI THÚ TRONG MỘ ĐỂ HỒI SINH",
                    filter: c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned,
                    onCancelled: () =>
                    {
                        isGraveyardSelectionOpen = false;
                        SetStatus($"Đã hủy kích hoạt [{chosenCard.cardName}]. Hãy chọn lá bài úp khác hoặc bấm Không kích hoạt.");
                        if (BattleAutoBoard.Instance != null)
                        {
                            BattleAutoBoard.Instance.ReopenLastDefenseReactionModal();
                        }
                    },
                    cancelButtonLabel: "QUAY LẠI CHỌN BÀI ÚP KHÁC",
                    onSecondaryCancelled: () =>
                    {
                        isGraveyardSelectionOpen = false;
                        SubmitMultiplayerDefenseReaction(-1, -1);
                    },
                    secondaryCancelButtonLabel: "HỦY LUÔN (KHÔNG KÍCH HOẠT)"
                );
                return;
            }
        }

        SubmitMultiplayerDefenseReaction(chosenSpellZone, -1);
    }

    public void SubmitMultiplayerDefenseReaction(int chosenSpellZone, int graveyardTargetIndex = -1)
    {
        isNetworkPromptOpen = false;
        lastOpenedPromptType = NetPendingPromptType.None;
        isGraveyardSelectionOpen = false;
        isEndPhaseDiscardOpen = false;
        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
            BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
        }

        if (NetworkDuelController.Instance != null)
        {
            NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
            {
                actionType = (int)NetDuelActionType.RespondPromptChoice,
                promptId = activeNetworkPromptId,
                myZoneIndex = chosenSpellZone,
                graveyardTargetIndex = graveyardTargetIndex
            });
        }
    }

    public void SubmitMultiplayerPromptChoice(int choiceValue)
    {
        isNetworkPromptOpen = false;
        lastOpenedPromptType = NetPendingPromptType.None;
        isGraveyardSelectionOpen = false;
        isEndPhaseDiscardOpen = false;
        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
            BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
        }

        if (NetworkDuelController.Instance != null)
        {
            NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
            {
                actionType = (int)NetDuelActionType.RespondPromptChoice,
                promptId = activeNetworkPromptId,
                myZoneIndex = choiceValue,
                graveyardTargetIndex = choiceValue
            });
        }
    }

    public void ShowMultiplayerGameOver(bool didLocalWin, string reason)
    {
        currentState = TurnState.GameOver;
        isGraveyardSelectionOpen = false;

        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        RefreshEndTurnButtonState();

        PlayBattleSFX(didLocalWin ? "Win" : "Lose");

        string title = didLocalWin ? "CHIẾN THẮNG!" : "THẤT BẠI!";
        SetStatus($"{title} ({reason})");

        if (gameOverPanel != null)
        {
            gameOverPanel.SetActive(true);
            if (gameOverTitleText != null) gameOverTitleText.text = title;
            if (gameOverReasonText != null) gameOverReasonText.text = reason;
        }
    }

    private void ApplySnapshotMonsterZones(Player targetPlayer, NetMonsterSlotDto[] slots, bool isOpponent)
    {
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (slots == null || i >= slots.Length || slots[i] == null || !slots[i].hasCard)
            {
                targetPlayer.monsterZones[i] = null;
                continue;
            }

            NetMonsterSlotDto dto = slots[i];
            BattlePosition pos = (BattlePosition)dto.position;
            CardData cardData;

            if (isOpponent && pos == BattlePosition.FaceDownDefense && string.IsNullOrEmpty(dto.cardName))
            {
                cardData = new CardData("Quái thú úp", null, 0, 0, 4, "Quái thú đang đặt úp của đối thủ.");
            }
            else if (dto.isMagicalHatDecoy && string.IsNullOrEmpty(dto.cardName))
            {
                cardData = new CardData("Mũ Tơ Ma Thuật", null, 0, 0, 1, "Quái thú ảo ảnh (0/0) từ Mũ Tơ Ma Thuật.");
            }
            else
            {
                cardData = CreateCardData(dto.cardName);
            }

            targetPlayer.monsterZones[i] = new MonsterCard
            {
                data = cardData,
                owner = targetPlayer,
                zoneIndex = i,
                position = pos,
                atkModifier = dto.atkModifier,
                defModifier = dto.defModifier,
                summonedThisTurn = dto.summonedThisTurn,
                isSpecialSummoned = dto.isSpecialSummoned,
                hasAttacked = dto.hasAttacked,
                positionChanged = dto.positionChanged,
                faceUpEffectTriggered = dto.faceUpEffectTriggered,
                isMagicalHatDecoy = dto.isMagicalHatDecoy
            };
        }
    }

    private void ApplySnapshotSpellZones(CardData[] targetZones, bool[] targetFaceDown, NetSpellSlotDto[] slots, bool isOpponent)
    {
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (slots == null || i >= slots.Length || slots[i] == null || !slots[i].hasCard)
            {
                targetZones[i] = null;
                targetFaceDown[i] = false;
                if (!isOpponent)
                {
                    networkMySpellCanReact[i] = false;
                    networkMySpellReactHint[i] = "";
                }
                continue;
            }

            NetSpellSlotDto dto = slots[i];
            targetFaceDown[i] = dto.isFaceDown;
            if (!isOpponent)
            {
                networkMySpellCanReact[i] = dto.canActivateInReaction;
                networkMySpellReactHint[i] = dto.reactionHint ?? "";
            }

            if (isOpponent && dto.isFaceDown && string.IsNullOrEmpty(dto.cardName))
            {
                targetZones[i] = CardData.CreateSpell("Bài úp", null, SpellEffectType.None, 0, "Bài Phép/Bẫy đang đặt úp của đối thủ.");
            }
            else
            {
                targetZones[i] = CreateCardData(dto.cardName);
            }
        }
    }

    public bool CanPlayerSetCardReact(int spellZone, out string failReason)
    {
        failReason = "";
        if (spellZone < 0 || spellZone >= Player.ZoneCount)
        {
            failReason = "Ô bài không hợp lệ.";
            return false;
        }

        CardData card = playerSpellZones[spellZone];
        if (card == null || !playerSpellFaceDown[spellZone])
        {
            failReason = "Lá bài không ở trạng thái đặt úp.";
            return false;
        }

        if (IsMultiplayer)
        {
            failReason = networkMySpellReactHint[spellZone];
            return networkMySpellCanReact[spellZone];
        }

        bool deckHasSpellTrap = playerDeck.Any(c => c != null && c.IsSpellOrTrap);
        bool gyHasMonster = playerGraveyard.Any(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned);

        return Duel.CanActivateSpellOrTrap(
            card,
            humanPlayer,
            aiPlayer,
            playerDeck.Count,
            deckHasSpellTrap,
            gyHasMonster,
            out failReason
        );
    }

    public bool HasAnyActivatablePlayerSetCardForReaction()
    {
        if (HasKuribohInPlayerHand()) return true;
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (CanPlayerSetCardReact(i, out _)) return true;
        }
        return false;
    }

    public void SyncPlayerHandCounts()
    {
        if (humanPlayer != null)
        {
            humanPlayer.handCount = playerHand != null ? playerHand.Count : 0;
        }
        if (aiPlayer != null)
        {
            aiPlayer.handCount = IsMultiplayer
                ? networkOpponentHandCount
                : (aiHand != null ? aiHand.Count : 0);
        }
    }

    public bool HasKuribohInPlayerHand()
    {
        return playerHand != null && playerHand.Any(c => c != null && c.monsterEffect == MonsterEffectType.KuribohHandGuard);
    }

    public CardData GetFirstKuribohInPlayerHand()
    {
        return playerHand != null
            ? playerHand.FirstOrDefault(c => c != null && c.monsterEffect == MonsterEffectType.KuribohHandGuard)
            : null;
    }

    public bool CanPlayerTrapHoleReactOnSummon(int spellZone, MonsterCard summonedEnemy, out string failReason)
    {
        failReason = "";
        if (spellZone < 0 || spellZone >= Player.ZoneCount)
        {
            failReason = "Ô bài không hợp lệ.";
            return false;
        }

        CardData card = playerSpellZones[spellZone];
        if (card == null || !playerSpellFaceDown[spellZone])
        {
            failReason = "Lá bài không ở trạng thái đặt úp.";
            return false;
        }

        if (IsMultiplayer)
        {
            failReason = networkMySpellReactHint[spellZone];
            return networkMySpellCanReact[spellZone];
        }

        return Duel.CanActivateTrapHoleOnSummon(card, summonedEnemy, out failReason);
    }

    public bool HasActivatablePlayerTrapHoleForSummon(MonsterCard summonedEnemy)
    {
        if (summonedEnemy == null) return false;
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (CanPlayerTrapHoleReactOnSummon(i, summonedEnemy, out _)) return true;
        }
        return false;
    }

    private void RefreshEndTurnButtonState()
    {
        if (endTurnButton == null) return;

        if (!IsMultiplayer)
        {
            endTurnButton.interactable = true;
            if (endTurnButtonText != null) endTurnButtonText.text = "END TURN";
            Image img = endTurnButton.GetComponent<Image>();
            if (img != null) img.color = new Color(0.8f, 0.25f, 0.15f, 0.95f);
            return;
        }

        bool isMyTurn = (duel != null && duel.Current == humanPlayer && currentState != TurnState.GameOver && hasAppliedInitialSnapshot);
        endTurnButton.interactable = isMyTurn;
        if (endTurnButtonText != null)
        {
            endTurnButtonText.text = isMyTurn ? "END TURN" : "LƯỢT ĐỐI THỦ";
        }
        Image btnImg = endTurnButton.GetComponent<Image>();
        if (btnImg != null)
        {
            btnImg.color = isMyTurn
                ? new Color(0.8f, 0.25f, 0.15f, 0.95f)
                : new Color(0.28f, 0.3f, 0.35f, 0.8f);
        }
    }

    void EnsureMultiplayerLeaveButton()
    {
        Canvas canvas = GetBattleCanvas();
        if (canvas == null || GameObject.Find("MultiplayerLeaveButton") != null) return;

        GameObject btnObj = new GameObject("MultiplayerLeaveButton", typeof(RectTransform), typeof(Image), typeof(Button));
        btnObj.transform.SetParent(canvas.transform, false);
        RectTransform rt = btnObj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-18f, -14f);
        rt.sizeDelta = new Vector2(145f, 40f);

        btnObj.GetComponent<Image>().color = new Color(0.68f, 0.2f, 0.2f, 0.92f);
        btnObj.GetComponent<Button>().onClick.AddListener(ReturnToMainMenu);

        GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        txtObj.transform.SetParent(btnObj.transform, false);
        RectTransform txtRT = txtObj.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = Vector2.zero;
        txtRT.offsetMax = Vector2.zero;

        TextMeshProUGUI txt = txtObj.GetComponent<TextMeshProUGUI>();
        txt.text = "RỜI TRẬN";
        txt.fontSize = 16;
        txt.fontStyle = FontStyles.Bold;
        txt.alignment = TextAlignmentOptions.Center;
        txt.color = Color.white;
        txt.raycastTarget = false;
    }

    void HandleMultiplayerDisconnect(string reason)
    {
        NotifyOpponentLeftMatch(string.IsNullOrWhiteSpace(reason)
            ? $"{OpponentDisplayName} đã mất kết nối!"
            : reason);
    }

    public void NotifyOpponentLeftMatch(string reason)
    {
        if (opponentLeftMultiplayerMatch) return;
        opponentLeftMultiplayerMatch = true;
        isNetworkPromptOpen = false;
        isGraveyardSelectionOpen = false;
        isEndPhaseDiscardOpen = false;
        UpdateAfkCountdownBanner(-1, false);

        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
            BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
        }

        if (currentState != TurnState.GameOver)
        {
            ShowMultiplayerGameOver(didLocalWin: true, reason: reason);
        }
        else
        {
            string leaveNotice = $"{reason} (Bấm 'VỀ MENU' để thoát)";
            SetStatus(leaveNotice);
            UpdateGameOverReasonIfVisible(leaveNotice);
        }
    }

    public void UpdateGameOverReasonIfVisible(string msg)
    {
        if (gameOverPanel != null && gameOverPanel.activeSelf && gameOverReasonText != null && !string.IsNullOrEmpty(msg))
        {
            gameOverReasonText.text = msg;
        }
    }

    CardData CreateCardData(string cardName)
    {
        string cleanKey = CardDatabaseSO.ExtractCleanName(cardName).ToLower();
        bool isSpellFolder = spellSpriteCache.ContainsKey(cardName) || cleanSpellSpriteCache.ContainsKey(cleanKey);
        bool isTrapFolder = trapSpriteCache.ContainsKey(cardName) || cleanTrapSpriteCache.ContainsKey(cleanKey);
        bool isSpellOrTrap = isSpellFolder || isTrapFolder;

        Sprite sp = GetResolvedSprite(cardName, isSpellOrTrap);

        CardData built;
        if (cardDatabase != null)
        {
            built = cardDatabase.BuildCardData(cardName, sp, isSpellOrTrap);
        }
        else if (isSpellOrTrap)
        {
            built = CardDatabaseSO.AutoBuildFallbackSpell(cardName, sp);
        }
        else
        {
            built = CardDatabaseSO.AutoBuildFallbackMonster(cardName, sp);
        }

        if (isTrapFolder)
        {
            built.cardType = CardType.Trap;
        }

        return built;
    }

    void InitializeBattle()
    {
        isGraveyardSelectionOpen = false;
        playerDeck.Clear();
        aiDeck.Clear();
        playerHand.Clear();
        aiHand.Clear();
        playerGraveyard.Clear();
        aiGraveyard.Clear();

        Array.Clear(playerSpellZones, 0, playerSpellZones.Length);
        Array.Clear(playerSpellFaceDown, 0, playerSpellFaceDown.Length);
        Array.Clear(aiSpellZones, 0, aiSpellZones.Length);
        Array.Clear(aiSpellFaceDown, 0, aiSpellFaceDown.Length);

        List<CardSaveData> savedCards = DeckManager.LoadSavedDeckFromPrefs();
        foreach (var item in savedCards)
        {
            for (int i = 0; i < item.count; i++)
            {
                playerDeck.Add(CreateCardData(item.cardName));
            }
        }

        if (playerDeck.Count == 0)
        {
            foreach (var kvp in monsterSpriteCache)
            {
                for (int i = 0; i < 2; i++) playerDeck.Add(CreateCardData(kvp.Key));
            }
            foreach (var kvp in spellSpriteCache)
            {
                for (int i = 0; i < 2; i++) playerDeck.Add(CreateCardData(kvp.Key));
            }
            foreach (var kvp in trapSpriteCache)
            {
                for (int i = 0; i < 2; i++) playerDeck.Add(CreateCardData(kvp.Key));
            }
        }

        foreach (var card in playerDeck)
        {
            aiDeck.Add(card.Clone());
        }

        ShuffleDeck(playerDeck);
        ShuffleDeck(aiDeck);

        humanPlayer = new Player("Player") { lifePoints = startingLP };
        aiPlayer = new Player("AI") { lifePoints = startingLP };
        displayedPlayerLP = startingLP;
        displayedAILP = startingLP;

        if (playerLPDamageText != null) playerLPDamageText.gameObject.SetActive(false);
        if (aiLPDamageText != null) aiLPDamageText.gameObject.SetActive(false);

        duel = new Duel(humanPlayer, aiPlayer);

        duel.OnLog += HandleDuelLog;
        duel.OnSummoned += HandleMonsterSummoned;
        duel.OnSpellActivated += _ => PlayBattleSFX("Spell");
        duel.OnAttackDeclared += HandleAttackDeclared;
        duel.OnDamage += HandleDamage;
        duel.OnMonsterShattered += HandleMonsterShattered;
        duel.OnDestroyed += HandleMonsterDestroyed;
        duel.OnRequestDraw += HandleSpellDrawRequest;
        duel.OnGetReviveTarget += HandleSpellReviveRequest;
        duel.OnTriggerFlipGraveyardToDeck += HandleFlipGraveyardToDeck;
        duel.OnReturnMonsterToHand += HandleReturnMonsterToHand;
        duel.OnSearchMonsterFromDeck += HandleSearchMonsterFromDeck;
        duel.OnRequestDeckSpellsTraps += HandleRequestDeckSpellsTraps;
        duel.OnSendCardToGraveyard += HandleSendCardToGraveyard;
        duel.OnSwordsExpired += HandleSwordsExpired;

        UpdateLPUI();
        UpdateDeckCountUI();

        for (int i = 0; i < 5; i++)
        {
            if (!DrawCard(isPlayer: true, playSound: false)) return;
            if (!DrawCard(isPlayer: false, playSound: false)) return;
        }
        PlayBattleSFX("Draw");

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.ForceRefreshHand();
        }

        currentState = TurnState.MainPhase;
        SetStatus("Lượt 1: Lượt của bạn (Chuột trái triệu hồi/kích hoạt | Chuột phải đặt úp)");
    }

    void ShuffleDeck(List<CardData> deck)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int randomIndex = UnityEngine.Random.Range(0, i + 1);
            CardData temp = deck[i];
            deck[i] = deck[randomIndex];
            deck[randomIndex] = temp;
        }
    }

    public bool DrawCard(bool isPlayer, bool playSound = true)
    {
        if (currentState == TurnState.GameOver) return false;

        List<CardData> targetDeck = isPlayer ? playerDeck : aiDeck;
        List<CardData> targetHand = isPlayer ? playerHand : aiHand;

        if (targetDeck.Count == 0)
        {
            string loser = isPlayer ? "Player" : "AI";
            string winner = isPlayer ? "AI" : "Player";
            EndGame(winner, $"{loser} không thể rút bài vì Deck đã hết bài (Deck Out)!");
            return false;
        }

        CardData drawnCard = targetDeck[0];
        targetDeck.RemoveAt(0);
        targetHand.Add(drawnCard);
        SyncPlayerHandCounts();

        if (playSound) PlayBattleSFX("Draw");
        UpdateDeckCountUI();
        return true;
    }

    public int FindEmptySpellZone(bool isPlayer, int preferredZone = -1)
    {
        CardData[] zones = isPlayer ? playerSpellZones : aiSpellZones;
        if (preferredZone >= 0 && preferredZone < Player.ZoneCount && zones[preferredZone] == null)
            return preferredZone;

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (zones[i] == null) return i;
        }
        return -1;
    }

    public bool PlayerActivateSpellFromHand(int handIndex, int preferredSpellZone = -1)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer)
        {
            if (IsMultiplayer && duel != null && duel.Current != humanPlayer)
                SetStatus($"Chưa tới lượt của bạn! Đang là lượt của {OpponentDisplayName}.");
            return false;
        }
        if (handIndex < 0 || handIndex >= playerHand.Count) return false;

        CardData spellCard = playerHand[handIndex];
        if (spellCard.cardType == CardType.Trap)
        {
            SetStatus("Bài Bẫy phải đặt úp xuống sân trước (Chuột phải hoặc kéo thả)!");
            return false;
        }
        if (spellCard.cardType != CardType.Spell) return false;

        int spellZone = FindEmptySpellZone(isPlayer: true, preferredSpellZone);
        if (spellZone < 0)
        {
            SetStatus("Hết ô bài Phép/Bẫy trên sân!");
            return false;
        }

        if (spellCard.spellEffect == SpellEffectType.SwordsOfRevealingLight && aiPlayer != null && aiPlayer.swordsBlockTurnsRemaining > 0)
        {
            SetStatus($"[{spellCard.cardName}] đang còn hiệu lực khóa {aiPlayer.name} (còn {aiPlayer.swordsBlockTurnsRemaining} lượt), không thể kích hoạt chồng thêm!");
            return false;
        }

        if (spellCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            if (humanPlayer.FirstEmptyZone() < 0)
            {
                SetStatus("Sân của bạn đã đầy chỗ, không thể hồi sinh!");
                return false;
            }
            if (!playerGraveyard.Any(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned))
            {
                SetStatus("Không có quái thú nào hợp lệ trong Mộ bài để hồi sinh!");
                return false;
            }

            if (IsMultiplayer)
            {
                isGraveyardSelectionOpen = true;
                SetStatus($"[{spellCard.cardName}] Chọn 1 quái thú trong Phần Mộ để hồi sinh (hoặc bấm Hủy kích hoạt)!");

                if (BattleAutoBoard.Instance != null)
                {
                    BattleAutoBoard.Instance.OpenGraveyardSelection(
                        playerGraveyard,
                        gyIndex =>
                        {
                            isGraveyardSelectionOpen = false;
                            if (NetworkDuelController.Instance != null)
                            {
                                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                                {
                                    actionType = (int)NetDuelActionType.ActivateSpellFromHand,
                                    handIndex = handIndex,
                                    preferredZone = preferredSpellZone,
                                    graveyardTargetIndex = gyIndex
                                });
                            }
                        },
                        customTitle: $"[{spellCard.cardName}] CHỌN QUÁI THÚ TRONG MỘ ĐỂ HỒI SINH",
                        filter: c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned,
                        onCancelled: () =>
                        {
                            isGraveyardSelectionOpen = false;
                            SetStatus($"Đã hủy kích hoạt [{spellCard.cardName}].");
                        },
                        cancelButtonLabel: "HỦY KÍCH HOẠT"
                    );
                }
                return true;
            }

            // Chờ người chơi chọn quái thú trong Mộ xong mới rút lá bài khỏi tay (để có thể bấm Hủy kích hoạt bất cứ lúc nào)
            OpenReviveGraveyardSelector(
                spellZone,
                spellCard,
                onConfirmedBeforeRevive: () =>
                {
                    int curIdx = playerHand.IndexOf(spellCard);
                    if (curIdx >= 0) playerHand.RemoveAt(curIdx);
                    else if (handIndex >= 0 && handIndex < playerHand.Count) playerHand.RemoveAt(handIndex);

                    SyncPlayerHandCounts();
                    playerSpellZones[spellZone] = spellCard;
                    playerSpellFaceDown[spellZone] = false;
                    PlayBattleSFX("Spell");
                    if (BattleAutoBoard.Instance != null)
                    {
                        BattleAutoBoard.Instance.ForceRefreshHand();
                    }
                },
                onCancelled: () =>
                {
                    isGraveyardSelectionOpen = false;
                    SetStatus($"Đã hủy kích hoạt [{spellCard.cardName}].");
                }
            );
            return true;
        }

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.ActivateSpellFromHand,
                    handIndex = handIndex,
                    preferredZone = preferredSpellZone
                });
                return true;
            }
            return false;
        }

        if (duel.ActivateSpell(humanPlayer, spellCard))
        {
            RecordLocalValidGameAction();
            playerHand.RemoveAt(handIndex);
            SyncPlayerHandCounts();
            playerSpellZones[spellZone] = spellCard;
            playerSpellFaceDown[spellZone] = false;

            if (spellCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
            {
                StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, spellZone, spellCard, 0.9f));
            }
            return true;
        }
        return false;
    }

    public bool PlayerSetSpellFromHand(int handIndex, int preferredSpellZone = -1)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer)
        {
            if (IsMultiplayer && duel != null && duel.Current != humanPlayer)
                SetStatus($"Chưa tới lượt của bạn! Đang là lượt của {OpponentDisplayName}.");
            return false;
        }
        if (handIndex < 0 || handIndex >= playerHand.Count) return false;

        CardData spellCard = playerHand[handIndex];
        if (!spellCard.IsSpellOrTrap) return false;

        int spellZone = FindEmptySpellZone(isPlayer: true, preferredSpellZone);
        if (spellZone < 0)
        {
            SetStatus("Hết ô bài Phép/Bẫy trên sân!");
            return false;
        }

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.SetSpellTrapFromHand,
                    handIndex = handIndex,
                    preferredZone = preferredSpellZone
                });
                return true;
            }
            return false;
        }

        RecordLocalValidGameAction();
        playerHand.RemoveAt(handIndex);
        SyncPlayerHandCounts();
        playerSpellZones[spellZone] = spellCard;
        playerSpellFaceDown[spellZone] = true;
        PlayBattleSFX("Summon");
        SetStatus($"Bạn đặt úp [{spellCard.cardName}] vào ô Phép/Bẫy.");
        return true;
    }

    public bool PlayerActivateSetSpell(int spellZone)
    {
        if (isGraveyardSelectionOpen) return false;
        if (spellZone < 0 || spellZone >= Player.ZoneCount) return false;

        CardData spellCard = playerSpellZones[spellZone];
        if (spellCard == null || !playerSpellFaceDown[spellZone]) return false;

        if (spellCard.spellEffect == SpellEffectType.MagicalHats)
        {
            if (duel == null || duel.Current == humanPlayer || currentState != TurnState.BattlePhase)
            {
                SetStatus("Mũ Tơ Ma Thuật chỉ kích hoạt trong Battle Phase của đối thủ!");
                return false;
            }
        }
        else if (spellCard.spellEffect == SpellEffectType.TrapHole)
        {
            SetStatus("Hang Rơi (Trap Hole) sẽ tự hỏi kích hoạt ngay khi đối thủ Triệu hồi Thường / Lật quái thú có ATK >= 1000!");
            return false;
        }
        else if (spellCard.cardType == CardType.Spell)
        {
            if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer) return false;
        }

        if (spellCard.spellEffect == SpellEffectType.SwordsOfRevealingLight && aiPlayer != null && aiPlayer.swordsBlockTurnsRemaining > 0)
        {
            SetStatus($"[{spellCard.cardName}] đang còn hiệu lực khóa {aiPlayer.name} (còn {aiPlayer.swordsBlockTurnsRemaining} lượt), không thể kích hoạt chồng thêm!");
            return false;
        }

        if (spellCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            if (humanPlayer.FirstEmptyZone() < 0)
            {
                SetStatus("Sân của bạn đã đầy chỗ, không thể hồi sinh!");
                return false;
            }
            if (!playerGraveyard.Any(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned))
            {
                SetStatus("Không có quái thú nào hợp lệ trong Mộ bài để hồi sinh!");
                return false;
            }

            if (IsMultiplayer)
            {
                isGraveyardSelectionOpen = true;
                SetStatus($"[{spellCard.cardName}] Chọn 1 quái thú trong Phần Mộ để hồi sinh (hoặc bấm Hủy kích hoạt)!");

                if (BattleAutoBoard.Instance != null)
                {
                    BattleAutoBoard.Instance.OpenGraveyardSelection(
                        playerGraveyard,
                        gyIndex =>
                        {
                            isGraveyardSelectionOpen = false;
                            if (NetworkDuelController.Instance != null)
                            {
                                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                                {
                                    actionType = (int)NetDuelActionType.ActivateSetSpellTrap,
                                    myZoneIndex = spellZone,
                                    graveyardTargetIndex = gyIndex
                                });
                            }
                        },
                        customTitle: $"[{spellCard.cardName}] CHỌN QUÁI THÚ TRONG MỘ ĐỂ HỒI SINH",
                        filter: c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned,
                        onCancelled: () =>
                        {
                            isGraveyardSelectionOpen = false;
                            SetStatus($"Đã hủy kích hoạt [{spellCard.cardName}] (lá bài vẫn giữ trạng thái đặt úp).");
                        },
                        cancelButtonLabel: "HỦY KÍCH HOẠT"
                    );
                }
                return true;
            }

            // Giữ nguyên trạng thái đặt úp cho đến khi người chơi thực sự chọn quái thú trong Mộ (để có thể bấm Hủy kích hoạt)
            OpenReviveGraveyardSelector(
                spellZone,
                spellCard,
                onConfirmedBeforeRevive: () =>
                {
                    playerSpellFaceDown[spellZone] = false;
                    PlayBattleSFX("Spell");
                },
                onCancelled: () =>
                {
                    isGraveyardSelectionOpen = false;
                    SetStatus($"Đã hủy kích hoạt [{spellCard.cardName}] (lá bài vẫn giữ trạng thái đặt úp).");
                }
            );
            return true;
        }

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.ActivateSetSpellTrap,
                    myZoneIndex = spellZone
                });
                return true;
            }
            return false;
        }

        if (duel.ActivateSpell(humanPlayer, spellCard))
        {
            RecordLocalValidGameAction();
            playerSpellFaceDown[spellZone] = false;
            if (spellCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
            {
                StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, spellZone, spellCard, 0.9f));
            }
            return true;
        }
        return false;
    }

    void OpenReviveGraveyardSelector(
        int spellZone,
        CardData spellCard,
        Action onConfirmedBeforeRevive = null,
        Action onCancelled = null,
        string cancelButtonLabel = "HỦY KÍCH HOẠT",
        Action onSecondaryCancelled = null,
        string secondaryCancelButtonLabel = "HỦY LUÔN (KHÔNG KÍCH HOẠT)")
    {
        isGraveyardSelectionOpen = true;
        SetStatus($"[{spellCard.cardName}] Chọn 1 quái thú trong Phần Mộ để hồi sinh!");

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.OpenGraveyardSelection(
                playerGraveyard,
                gyIndex =>
                {
                    onConfirmedBeforeRevive?.Invoke();
                    ConfirmPlayerReviveFromGraveyard(gyIndex, spellZone, spellCard);
                },
                customTitle: $"[{spellCard.cardName}] CHỌN QUÁI THÚ TRONG MỘ ĐỂ HỒI SINH",
                filter: c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned,
                onCancelled: onCancelled,
                cancelButtonLabel: cancelButtonLabel,
                onSecondaryCancelled: onSecondaryCancelled,
                secondaryCancelButtonLabel: secondaryCancelButtonLabel
            );
        }
        else
        {
            onConfirmedBeforeRevive?.Invoke();
            int fallbackIdx = playerGraveyard.FindIndex(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned);
            ConfirmPlayerReviveFromGraveyard(fallbackIdx, spellZone, spellCard);
        }
    }

    public void ConfirmPlayerReviveFromGraveyard(int graveyardIndex, int spellZone, CardData spellCard)
    {
        RecordLocalValidGameAction();
        isGraveyardSelectionOpen = false;
        if (graveyardIndex < 0 || graveyardIndex >= playerGraveyard.Count) return;

        CardData chosenMonster = playerGraveyard[graveyardIndex];
        if (chosenMonster.cardType != CardType.Monster || chosenMonster.CannotBeSpecialSummoned)
        {
            SetStatus($"[{chosenMonster.cardName}] không thể được Triệu hồi Đặc biệt!");
            return;
        }

        playerGraveyard.RemoveAt(graveyardIndex);

        MonsterCard summoned = duel.Summon(humanPlayer, chosenMonster, BattlePosition.FaceUpAttack, null, isSpecial: true);
        if (summoned != null)
        {
            SetStatus($"Bạn kích hoạt [{spellCard.cardName}]: Hồi sinh {chosenMonster.cardName} từ Mộ bài!");
        }

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.ForceRefreshHand();
        }

        StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, spellZone, spellCard, 0.9f));
    }

    IEnumerator ClearSpellZoneAfterDelay(bool isPlayer, int zoneIndex, CardData spellCard, float delay)
    {
        yield return new WaitForSeconds(delay);
        CardData[] zones = isPlayer ? playerSpellZones : aiSpellZones;
        bool[] faceDowns = isPlayer ? playerSpellFaceDown : aiSpellFaceDown;
        List<CardData> gy = isPlayer ? playerGraveyard : aiGraveyard;

        if (zoneIndex >= 0 && zoneIndex < Player.ZoneCount && zones[zoneIndex] == spellCard)
        {
            zones[zoneIndex] = null;
            faceDowns[zoneIndex] = false;
            gy.Add(spellCard);
        }
    }

    public bool PlayerSummonFromHand(int handIndex, BattlePosition pos, List<int> tributeZoneIndices = null, int preferredZone = -1)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer)
        {
            if (IsMultiplayer && duel != null && duel.Current != humanPlayer)
                SetStatus($"Chưa tới lượt của bạn! Đang là lượt của {OpponentDisplayName}.");
            return false;
        }
        if (handIndex < 0 || handIndex >= playerHand.Count) return false;

        CardData cardToSummon = playerHand[handIndex];
        if (cardToSummon.IsSpellOrTrap)
        {
            if (cardToSummon.cardType == CardType.Trap)
            {
                if (pos == BattlePosition.FaceDownDefense)
                    return PlayerSetSpellFromHand(handIndex, preferredZone);

                SetStatus("Bài Bẫy phải đặt úp xuống sân trước (Chuột phải hoặc kéo thả)!");
                return false;
            }

            return pos == BattlePosition.FaceDownDefense
                ? PlayerSetSpellFromHand(handIndex, preferredZone)
                : PlayerActivateSpellFromHand(handIndex, preferredZone);
        }

        if (cardToSummon.IsDivineGod && pos != BattlePosition.FaceUpAttack)
        {
            SetStatus($"[{cardToSummon.cardName}] chỉ có thể Triệu hồi Thường ở Tư thế Tấn công ngửa (không thể Đặt úp)!");
            return false;
        }

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.SummonMonster,
                    handIndex = handIndex,
                    position = (int)pos,
                    preferredZone = preferredZone,
                    tributeZones = tributeZoneIndices != null ? new List<int>(tributeZoneIndices) : new List<int>()
                });
                return true;
            }
            return false;
        }

        List<MonsterCard> tributes = new List<MonsterCard>();

        if (tributeZoneIndices != null)
        {
            foreach (int z in tributeZoneIndices)
            {
                if (z >= 0 && z < Player.ZoneCount && humanPlayer.monsterZones[z] != null)
                {
                    tributes.Add(humanPlayer.monsterZones[z]);
                }
            }
        }

        playerHand.RemoveAt(handIndex);
        SyncPlayerHandCounts();

        MonsterCard summoned = duel.Summon(humanPlayer, cardToSummon, pos, tributes, false, preferredZone);
        if (summoned != null)
        {
            RecordLocalValidGameAction();
            foreach (var t in tributes)
            {
                if (t != null && !t.isMagicalHatDecoy && t.data != null)
                {
                    playerGraveyard.Add(t.data);
                }
            }

            // Kiểm tra nếu AI có đặt úp Hang Rơi (Trap Hole) khi người chơi triệu hồi quái ATK >= 1000
            if (!summoned.IsFaceDown && humanPlayer.monsterZones[summoned.zoneIndex] == summoned)
            {
                TryAITriggerTrapHoleOnSummon(summoned);
            }

            // Nếu là Rồng Cánh Thần Của Ra vừa Triệu hồi Thường còn trên sân và LP > 100 -> chờ hiệu ứng Mặt Trời hạ cánh rồi hỏi trả LP còn 100
            if (!summoned.IsFaceDown && humanPlayer.monsterZones[summoned.zoneIndex] == summoned &&
                summoned.data.monsterEffect == MonsterEffectType.DivineRa && humanPlayer.lifePoints > 100)
            {
                pendingRaSummonZoneIndex = summoned.zoneIndex;
                int payAmount = humanPlayer.lifePoints - 100;
                string raMsg = $"Kích hoạt [{summoned.data.cardName}]: Trả {payAmount} LP (chỉ còn 100 LP) để nhận +{payAmount} ATK/DEF?";
                ShowRaPromptPanel(raMsg, delaySeconds: 1.85f);
            }

            return true;
        }

        // Hoàn lại bài nếu triệu hồi thất bại
        playerHand.Insert(Mathf.Clamp(handIndex, 0, playerHand.Count), cardToSummon);
        SyncPlayerHandCounts();
        return false;
    }

    public bool PlayerChangePosition(int myZoneIndex)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer) return false;
        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.ChangeMonsterPosition,
                    myZoneIndex = myZoneIndex
                });
                return true;
            }
            return false;
        }

        MonsterCard card = humanPlayer.monsterZones[myZoneIndex];
        if (card == null) return false;
        bool wasFaceDown = card.IsFaceDown;

        bool changed = duel.ChangePosition(humanPlayer, card);
        if (changed) RecordLocalValidGameAction();
        if (changed && wasFaceDown && !card.IsFaceDown && humanPlayer.monsterZones[myZoneIndex] == card)
        {
            TryAITriggerTrapHoleOnSummon(card);
        }
        return changed;
    }

    public bool PlayerActivateMonsterFieldEffect(int myZoneIndex, int targetOpponentZone = -1, List<int> tributeZones = null)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer) return false;
        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;

        MonsterCard sourceMonster = humanPlayer.monsterZones[myZoneIndex];
        if (sourceMonster == null || sourceMonster.IsFaceDown || sourceMonster.data == null) return false;

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.ActivateMonsterFieldEffect,
                    myZoneIndex = myZoneIndex,
                    targetOpponentZone = targetOpponentZone,
                    tributeZones = tributeZones != null ? new List<int>(tributeZones) : new List<int>()
                });
                return true;
            }
            return false;
        }

        if (sourceMonster.data.monsterEffect == MonsterEffectType.DivineObelisk)
        {
            List<MonsterCard> tributes = new List<MonsterCard>();
            if (tributeZones != null)
            {
                foreach (int z in tributeZones.Distinct())
                {
                    if (z >= 0 && z < Player.ZoneCount && humanPlayer.monsterZones[z] != null)
                    {
                        tributes.Add(humanPlayer.monsterZones[z]);
                    }
                }
            }

            if (duel.ActivateObeliskSoulEnergyMax(humanPlayer, sourceMonster, tributes))
            {
                RecordLocalValidGameAction();
                PlayBattleSFX("Spell");
                return true;
            }
            return false;
        }

        if (sourceMonster.data.monsterEffect == MonsterEffectType.DivineRa)
        {
            if (targetOpponentZone >= 0 && targetOpponentZone < Player.ZoneCount)
            {
                MonsterCard target = aiPlayer.monsterZones[targetOpponentZone];
                if (duel.ActivateRaPhoenixDestroy(humanPlayer, sourceMonster, target))
                {
                    RecordLocalValidGameAction();
                    PlayBattleSFX("Spell");
                    return true;
                }
                return false;
            }
            else
            {
                if (duel.ActivateRaPointToPointTransfer(humanPlayer, sourceMonster))
                {
                    RecordLocalValidGameAction();
                    PlayBattleSFX("Spell");
                    return true;
                }
                return false;
            }
        }

        return false;
    }

    public void EnterBattlePhase()
    {
        if (isGraveyardSelectionOpen) return;
        if (currentState != TurnState.MainPhase || duel == null || duel.Current != humanPlayer) return;
        if (duel.turnCount == 1)
        {
            SetStatus("Không được tấn công ở lượt đầu tiên!");
            return;
        }
        if (humanPlayer.swordsBlockTurnsRemaining > 0)
        {
            SetStatus($"Lớp Kiếm Bảo Hộ Của Ánh Sáng đang chặn tấn công (còn {humanPlayer.swordsBlockTurnsRemaining} lượt)!");
            return;
        }

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.EnterBattlePhase
                });
            }
            return;
        }

        RecordLocalValidGameAction();
        currentState = TurnState.BattlePhase;
        SetStatus("Lượt của bạn - Battle Phase");
    }

    public bool PlayerAttackMonster(int myZoneIndex, int enemyZoneIndex)
    {
        if (isGraveyardSelectionOpen) return false;
        if (duel == null || duel.Current != humanPlayer) return false;
        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;
        if (enemyZoneIndex < 0 || enemyZoneIndex >= Player.ZoneCount) return false;

        if (IsMultiplayer)
        {
            if (currentState != TurnState.MainPhase && currentState != TurnState.BattlePhase) return false;
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.AttackMonster,
                    myZoneIndex = myZoneIndex,
                    targetOpponentZone = enemyZoneIndex
                });
                return true;
            }
            return false;
        }

        if (currentState == TurnState.MainPhase) EnterBattlePhase();
        if (currentState != TurnState.BattlePhase || duel.Current != humanPlayer) return false;

        TryAITriggerMagicalHats();

        MonsterCard attacker = humanPlayer.monsterZones[myZoneIndex];
        MonsterCard target = aiPlayer.monsterZones[enemyZoneIndex];
        TryAITriggerKuriboh(attacker, target, isDirectAttack: false);

        bool result = duel.Attack(humanPlayer, attacker, target);
        if (result) RecordLocalValidGameAction();
        ResetActiveAttackVfxContext();
        return result;
    }

    public bool PlayerDirectAttack(int myZoneIndex)
    {
        if (isGraveyardSelectionOpen) return false;
        if (duel == null || duel.Current != humanPlayer) return false;
        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;

        if (IsMultiplayer)
        {
            if (currentState != TurnState.MainPhase && currentState != TurnState.BattlePhase) return false;
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.DirectAttack,
                    myZoneIndex = myZoneIndex
                });
                return true;
            }
            return false;
        }

        if (currentState == TurnState.MainPhase) EnterBattlePhase();
        if (currentState != TurnState.BattlePhase || duel.Current != humanPlayer) return false;

        MonsterCard attacker = humanPlayer.monsterZones[myZoneIndex];
        TryAITriggerKuriboh(attacker, null, isDirectAttack: true);

        bool result = duel.DirectAttack(humanPlayer, attacker);
        if (result) RecordLocalValidGameAction();
        ResetActiveAttackVfxContext();
        return result;
    }

    public void EndPlayerTurn()
    {
        if (isGraveyardSelectionOpen && !isEndPhaseDiscardOpen) return;
        if (currentState == TurnState.GameOver || duel == null || duel.IsGameOver || duel.Current != humanPlayer) return;

        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestAction(new NetDuelActionRequest
                {
                    actionType = (int)NetDuelActionType.EndTurn
                });
            }
            return;
        }

        RecordLocalValidGameAction();

        // Luật End Phase: Nếu trên tay có nhiều hơn 6 lá bài -> bắt buộc chọn bỏ xuống Mộ cho đến khi còn đúng 6 lá (hoặc bấm Hủy kết thúc lượt)
        if (playerHand.Count > MAX_HAND_SIZE_END_PHASE)
        {
            if (currentState != TurnState.EndPhase)
            {
                stateBeforeEndPhaseDiscard = (currentState == TurnState.BattlePhase)
                    ? TurnState.BattlePhase
                    : TurnState.MainPhase;
            }
            currentState = TurnState.EndPhase;
            OpenSingleplayerEndPhaseDiscardPrompt();
            return;
        }

        currentState = TurnState.EndPhase;
        isEndPhaseDiscardOpen = false;
        duel.EndTurn();
        StartCoroutine(AITurnRoutine());
    }

    void OpenSingleplayerEndPhaseDiscardPrompt()
    {
        if (playerHand.Count <= MAX_HAND_SIZE_END_PHASE)
        {
            isEndPhaseDiscardOpen = false;
            isGraveyardSelectionOpen = false;
            if (BattleAutoBoard.Instance != null) BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
            duel.EndTurn();
            StartCoroutine(AITurnRoutine());
            return;
        }

        isEndPhaseDiscardOpen = true;
        isGraveyardSelectionOpen = true;
        int excess = playerHand.Count - MAX_HAND_SIZE_END_PHASE;
        string title = $"[END PHASE - GIỚI HẠN {MAX_HAND_SIZE_END_PHASE} LÁ TRÊN TAY] BẠN ĐANG CÓ {playerHand.Count} LÁ — BẮT BUỘC CHỌN BỎ {excess} LÁ XUỐNG MỘ";
        SetStatus(title);

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.OpenGraveyardSelection(
                playerHand,
                SelectHandCardForEndPhaseDiscard,
                customTitle: title,
                filter: null,
                onCancelled: CancelEndPhaseDiscardAndReturnToTurn,
                cancelButtonLabel: "HỦY KẾT THÚC LƯỢT (QUAY LẠI)"
            );
        }
        else
        {
            SelectHandCardForEndPhaseDiscard(0);
        }
    }

    public void CancelEndPhaseDiscardAndReturnToTurn()
    {
        isEndPhaseDiscardOpen = false;
        isGraveyardSelectionOpen = false;
        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
        }

        if (IsMultiplayer)
        {
            SubmitMultiplayerPromptChoice(-1);
            return;
        }

        currentState = (stateBeforeEndPhaseDiscard == TurnState.BattlePhase)
            ? TurnState.BattlePhase
            : TurnState.MainPhase;
        string phaseLabel = currentState == TurnState.BattlePhase ? "Battle Phase" : "Main Phase";
        SetStatus($"Đã hủy Kết thúc lượt — Quay lại {phaseLabel} của bạn!");
    }

    public void SelectHandCardForEndPhaseDiscard(int handIndex)
    {
        if (IsMultiplayer)
        {
            if (isNetworkPromptOpen && lastOpenedPromptType == NetPendingPromptType.EndPhaseDiscardHand)
            {
                SubmitMultiplayerPromptChoice(handIndex);
            }
            return;
        }

        if (!isEndPhaseDiscardOpen || handIndex < 0 || handIndex >= playerHand.Count) return;

        RecordLocalValidGameAction();
        CardData discarded = playerHand[handIndex];
        playerHand.RemoveAt(handIndex);
        playerGraveyard.Add(discarded);
        SyncPlayerHandCounts();
        PlayBattleSFX("Summon");

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.ForceRefreshHand();
        }

        if (playerHand.Count > MAX_HAND_SIZE_END_PHASE)
        {
            int remainExcess = playerHand.Count - MAX_HAND_SIZE_END_PHASE;
            SetStatus($"[End Phase] Đã bỏ [{discarded.cardName}] xuống Mộ. Hãy chọn bỏ thêm {remainExcess} lá nữa (còn {playerHand.Count}/{MAX_HAND_SIZE_END_PHASE} lá)!");
            OpenSingleplayerEndPhaseDiscardPrompt();
        }
        else
        {
            isEndPhaseDiscardOpen = false;
            isGraveyardSelectionOpen = false;
            if (BattleAutoBoard.Instance != null)
            {
                BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
            }
            SetStatus($"[End Phase] Đã bỏ [{discarded.cardName}] xuống Mộ (trên tay còn đúng {playerHand.Count} lá). Kết thúc lượt!");
            duel.EndTurn();
            StartCoroutine(AITurnRoutine());
        }
    }

    int FindSetTrapZone(bool isPlayer, SpellEffectType effectType)
    {
        CardData[] zones = isPlayer ? playerSpellZones : aiSpellZones;
        bool[] faceDowns = isPlayer ? playerSpellFaceDown : aiSpellFaceDown;

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (zones[i] != null && faceDowns[i] && zones[i].spellEffect == effectType)
                return i;
        }
        return -1;
    }

    bool CanActivateMagicalHats(bool isPlayer)
    {
        if (FindSetTrapZone(isPlayer, SpellEffectType.MagicalHats) < 0) return false;

        Player owner = isPlayer ? humanPlayer : aiPlayer;
        List<CardData> deck = isPlayer ? playerDeck : aiDeck;

        if (owner.MonsterCount <= 0 || owner.MonsterCount >= Player.ZoneCount) return false;
        return deck.Any(c => c.IsSpellOrTrap);
    }

    void TryAITriggerMagicalHats()
    {
        if (!CanActivateMagicalHats(isPlayer: false)) return;

        int trapZone = FindSetTrapZone(isPlayer: false, SpellEffectType.MagicalHats);
        if (trapZone < 0) return;

        CardData trapCard = aiSpellZones[trapZone];
        if (duel.ActivateSpell(aiPlayer, trapCard))
        {
            aiSpellFaceDown[trapZone] = false;
            StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: false, trapZone, trapCard, 0.9f));
        }
    }

    void TryAITriggerTrapHoleOnSummon(MonsterCard summonedPlayerMonster)
    {
        if (summonedPlayerMonster == null || summonedPlayerMonster.IsFaceDown) return;
        if (humanPlayer.monsterZones[summonedPlayerMonster.zoneIndex] != summonedPlayerMonster) return;

        int trapZone = FindSetTrapZone(isPlayer: false, SpellEffectType.TrapHole);
        if (trapZone < 0) return;

        CardData trapCard = aiSpellZones[trapZone];
        if (!Duel.CanActivateTrapHoleOnSummon(trapCard, summonedPlayerMonster, out _)) return;

        aiSpellFaceDown[trapZone] = false;
        if (duel.ActivateTrapHoleOnSummon(aiPlayer, trapCard, summonedPlayerMonster))
        {
            StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: false, trapZone, trapCard, 0.9f));
        }
    }

    void TryAITriggerKuriboh(MonsterCard attacker, MonsterCard target, bool isDirectAttack)
    {
        if (attacker == null || aiHand == null || aiPlayer == null) return;

        int expectedDamage = 0;
        if (isDirectAttack)
        {
            expectedDamage = attacker.Atk;
        }
        else if (target != null)
        {
            if (!target.IsFaceDown && target.position == BattlePosition.FaceUpAttack && attacker.Atk > target.Atk)
            {
                expectedDamage = attacker.Atk - target.Atk;
            }
            else if (target.IsDefense && attacker.data.monsterEffect == MonsterEffectType.PiercingDamage && attacker.Atk > target.Def)
            {
                expectedDamage = attacker.Atk - target.Def;
            }
        }

        if (expectedDamage <= 0) return;

        int kuribohIndex = aiHand.FindIndex(c => c != null && c.monsterEffect == MonsterEffectType.KuribohHandGuard);
        if (kuribohIndex < 0) return;

        CardData kuriboh = aiHand[kuribohIndex];
        aiHand.RemoveAt(kuribohIndex);
        aiGraveyard.Add(kuriboh);
        SyncPlayerHandCounts();

        aiPlayer.kuribohShieldActive = true;
        PlayBattleSFX("Spell");
        SetStatus($"[Hiệu ứng nhanh - {kuriboh.cardName}] AI gửi {kuriboh.cardName} từ trên tay xuống Mộ để chặn {expectedDamage} sát thương chiến đấu!");
    }

    private IEnumerator PromptPlayerTrapHoleOnAISummonRoutine(MonsterCard aiSummonedMonster)
    {
        if (aiSummonedMonster == null || aiSummonedMonster.IsFaceDown) yield break;
        if (aiPlayer.monsterZones[aiSummonedMonster.zoneIndex] != aiSummonedMonster) yield break;
        if (!HasActivatablePlayerTrapHoleForSummon(aiSummonedMonster) || BattleAutoBoard.Instance == null) yield break;

        int pickedZone = -1;
        waitingTrapDecision = true;

        BattleAutoBoard.Instance.OpenDefenseReactionModal(
            $"AI vừa triệu hồi [{aiSummonedMonster.data.cardName}] (ATK {aiSummonedMonster.Atk})!\nCHỌN KÍCH HOẠT BÀI BẪY [TRAP HOLE / HANG RƠI] ĐỂ PHÁ HỦY HOẶC BỎ QUA",
            z =>
            {
                pickedZone = z;
                waitingTrapDecision = false;
            },
            isTrapHolePrompt: true,
            summonedTargetForTrapHole: aiSummonedMonster
        );

        yield return new WaitUntil(() => !waitingTrapDecision || currentState == TurnState.GameOver);

        if (pickedZone >= 0 && pickedZone < Player.ZoneCount && currentState != TurnState.GameOver)
        {
            CardData trapCard = playerSpellZones[pickedZone];
            if (trapCard != null && playerSpellFaceDown[pickedZone] &&
                Duel.CanActivateTrapHoleOnSummon(trapCard, aiSummonedMonster, out _))
            {
                playerSpellFaceDown[pickedZone] = false;
                if (duel.ActivateTrapHoleOnSummon(humanPlayer, trapCard, aiSummonedMonster))
                {
                    StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, pickedZone, trapCard, 0.9f));
                    yield return new WaitForSeconds(0.75f);
                }
            }
        }
    }

    void HandleFlipGraveyardToDeck(Player owner, MonsterCard card)
    {
        bool isPlayer = (owner == humanPlayer);
        List<CardData> gy = isPlayer ? playerGraveyard : aiGraveyard;
        List<CardData> deck = isPlayer ? playerDeck : aiDeck;

        if (gy.Count == 0)
        {
            SetStatus($"[FLIP - {card.data.cardName}] Mộ bài đang trống, không có lá nào để trả về Deck.");
            return;
        }

        if (isPlayer)
        {
            isGraveyardSelectionOpen = true;
            pendingFlipCardName = card.data.cardName;
            SetStatus($"[FLIP - {card.data.cardName}] Chọn 1 lá bài trong Mộ để xáo trộn vào Deck!");

            if (BattleAutoBoard.Instance != null)
            {
                BattleAutoBoard.Instance.OpenGraveyardSelection(
                    gy,
                    ConfirmPlayerReturnGraveyardCard,
                    customTitle: $"[FLIP - {card.data.cardName}] CHỌN 1 LÁ TRONG MỘ TRẢ VỀ DECK"
                );
            }
            else
            {
                ConfirmPlayerReturnGraveyardCard(0);
            }
        }
        else
        {
            CardData best = gy.OrderByDescending(c => c.atk).First();
            gy.Remove(best);
            deck.Add(best);
            ShuffleDeck(deck);
            UpdateDeckCountUI();
            SetStatus($"[FLIP - {card.data.cardName}] AI chọn {best.cardName} từ Mộ xáo trộn vào Deck!");
        }
    }

    public void ConfirmPlayerReturnGraveyardCard(int graveyardIndex)
    {
        RecordLocalValidGameAction();
        isGraveyardSelectionOpen = false;
        if (graveyardIndex < 0 || graveyardIndex >= playerGraveyard.Count) return;

        CardData chosen = playerGraveyard[graveyardIndex];
        playerGraveyard.RemoveAt(graveyardIndex);
        playerDeck.Add(chosen);
        ShuffleDeck(playerDeck);
        UpdateDeckCountUI();

        string sourceName = string.IsNullOrEmpty(pendingFlipCardName) ? "Gremlin Tử Thần" : pendingFlipCardName;
        SetStatus($"[FLIP - {sourceName}] Đã đưa {chosen.cardName} từ Mộ xáo trộn vào Deck!");
        pendingFlipCardName = "";
    }

    void StartPlayerTurn()
    {
        if (currentState == TurnState.GameOver || duel.IsGameOver) return;

        RecordLocalValidGameAction();
        currentState = TurnState.DrawPhase;
        if (!DrawCard(isPlayer: true)) return;

        currentState = TurnState.MainPhase;
        SetStatus($"Lượt {duel.turnCount}: Lượt của bạn - Main Phase");
    }

    IEnumerator AITurnRoutine()
    {
        if (currentState == TurnState.GameOver || duel.IsGameOver) yield break;

        currentState = TurnState.DrawPhase;
        SetStatus("Lượt của AI - Rút bài...");
        yield return new WaitForSeconds(0.6f);

        if (!DrawCard(isPlayer: false)) yield break;

        currentState = TurnState.MainPhase;
        SetStatus("Lượt của AI - Đang suy nghĩ...");
        yield return new WaitForSeconds(0.7f);

        for (int i = aiHand.Count - 1; i >= 0; i--)
        {
            if (duel.IsGameOver) yield break;
            if (i >= aiHand.Count) continue;

            CardData card = aiHand[i];
            if (card.cardType == CardType.Trap)
            {
                int aiTrapZone = FindEmptySpellZone(isPlayer: false);
                if (aiTrapZone >= 0)
                {
                    aiHand.RemoveAt(i);
                    SyncPlayerHandCounts();
                    aiSpellZones[aiTrapZone] = card;
                    aiSpellFaceDown[aiTrapZone] = true;
                    PlayBattleSFX("Summon");
                    SetStatus("AI đặt úp 1 lá Bài Bẫy.");
                    yield return new WaitForSeconds(0.4f);
                }
                continue;
            }

            if (card.cardType == CardType.Spell)
            {
                if (card.spellEffect == SpellEffectType.DestroyAllMonsters && aiPlayer.MonsterCount >= humanPlayer.MonsterCount)
                    continue;
                if (card.spellEffect == SpellEffectType.SwordsOfRevealingLight && humanPlayer.swordsBlockTurnsRemaining > 0)
                    continue;

                int aiSpellZone = FindEmptySpellZone(isPlayer: false);
                aiHand.RemoveAt(i);
                SyncPlayerHandCounts();

                if (duel.ActivateSpell(aiPlayer, card))
                {
                    if (aiSpellZone >= 0)
                    {
                        aiSpellZones[aiSpellZone] = card;
                        aiSpellFaceDown[aiSpellZone] = false;
                    }
                    yield return new WaitForSeconds(0.8f);

                    if (card.spellEffect != SpellEffectType.SwordsOfRevealingLight)
                    {
                        if (aiSpellZone >= 0 && aiSpellZones[aiSpellZone] == card)
                        {
                            aiSpellZones[aiSpellZone] = null;
                            aiSpellFaceDown[aiSpellZone] = false;
                        }
                        aiGraveyard.Add(card);
                    }
                }
                else
                {
                    aiHand.Insert(Mathf.Clamp(i, 0, aiHand.Count), card);
                    SyncPlayerHandCounts();
                }
            }
        }

        MonsterCard aiSummoned = AISummonBestMonster();
        if (aiSummoned != null)
        {
            yield return new WaitForSeconds(aiSummoned.data != null && aiSummoned.data.IsDivineGod ? 2.35f : 0.6f);

            // Nếu AI vừa triệu hồi thường quái ATK >= 1000 và người chơi có Hang Rơi (Trap Hole)
            if (!aiSummoned.IsFaceDown && aiPlayer.monsterZones[aiSummoned.zoneIndex] == aiSummoned)
            {
                yield return StartCoroutine(PromptPlayerTrapHoleOnAISummonRoutine(aiSummoned));
            }

            // Nếu là Rồng Cánh Thần Của Ra còn trên sân -> AI trả LP còn 100 để tăng ATK/DEF
            if (!aiSummoned.IsFaceDown && aiPlayer.monsterZones[aiSummoned.zoneIndex] == aiSummoned &&
                aiSummoned.data.monsterEffect == MonsterEffectType.DivineRa && aiPlayer.lifePoints > 100)
            {
                duel.ActivateRaPointToPointTransfer(aiPlayer, aiSummoned);
                PlayBattleSFX("Spell");
                yield return new WaitForSeconds(0.65f);
            }
        }
        else
        {
            yield return new WaitForSeconds(0.4f);
        }

        foreach (var m in aiPlayer.Monsters.ToList())
        {
            if (m.IsDefense && m.CanChangePosition && !m.isMagicalHatDecoy && (m.Atk >= 1500 || (m.IsFaceDown && aiGraveyard.Count > 0)))
            {
                bool wasFd = m.IsFaceDown;
                if (duel.ChangePosition(aiPlayer, m))
                {
                    yield return new WaitForSeconds(0.4f);
                    if (wasFd && !m.IsFaceDown && aiPlayer.monsterZones[m.zoneIndex] == m)
                    {
                        yield return StartCoroutine(PromptPlayerTrapHoleOnAISummonRoutine(m));
                    }
                }
            }
        }

        if (duel.turnCount > 1 && !duel.IsGameOver && aiPlayer.swordsBlockTurnsRemaining <= 0)
        {
            currentState = TurnState.BattlePhase;
            SetStatus("Lượt của AI - Battle Phase!");
            yield return new WaitForSeconds(0.5f);

            foreach (var attacker in aiPlayer.Monsters.ToList())
            {
                if (duel.IsGameOver || aiPlayer.swordsBlockTurnsRemaining > 0) yield break;
                if (attacker == null || aiPlayer.monsterZones[attacker.zoneIndex] != attacker || !attacker.CanAttack) continue;

                MonsterCard previewTarget = FindBestAITargetForAttacker(attacker);
                if (humanPlayer.MonsterCount > 0 && previewTarget == null)
                {
                    // AI không có mục tiêu nào đánh thắng hoặc úp để đánh -> bỏ qua quái này
                    continue;
                }

                // Khi AI tuyên bố tấn công: nếu người chơi có BẤT KỲ lá bài Phép/Bẫy nào đang úp hoặc Kuriboh trên tay, mở Menu chọn lựa!
                if (HasAnyActivatablePlayerSetCardForReaction() && BattleAutoBoard.Instance != null)
                {
                    bool keepAskingReaction = true;
                    while (keepAskingReaction && currentState != TurnState.GameOver && !duel.IsGameOver)
                    {
                        keepAskingReaction = false;
                        int pickedZone = -1;
                        waitingTrapDecision = true;

                        string targetDesc = humanPlayer.MonsterCount == 0
                            ? "trực tiếp vào LP của bạn"
                            : (previewTarget != null && !previewTarget.IsFaceDown ? $"[{previewTarget.data.cardName}]" : "quái thú úp của bạn");

                        BattleAutoBoard.Instance.OpenDefenseReactionModal(
                            $"AI dùng [{attacker.data.cardName}] (ATK {attacker.Atk}) tấn công {targetDesc}!\nCHỌN KÍCH HOẠT BÀI PHÉP / BẪY ĐANG ÚP / KURIBOH HOẶC BỎ QUA",
                            z =>
                            {
                                pickedZone = z;
                                waitingTrapDecision = false;
                            }
                        );

                        yield return new WaitUntil(() => !waitingTrapDecision || currentState == TurnState.GameOver);

                        if ((pickedZone >= 0 || pickedZone == KURIBOH_HAND_REACTION_ZONE) && currentState != TurnState.GameOver)
                        {
                            bool reopenMenu = false;
                            yield return StartCoroutine(ActivatePlayerSetCardInSingleplayerReactionRoutine(
                                pickedZone,
                                onBackToReactionMenu: () => reopenMenu = true
                            ));
                            if (reopenMenu)
                            {
                                keepAskingReaction = true;
                            }
                        }
                    }
                }

                if (duel.IsGameOver || aiPlayer.swordsBlockTurnsRemaining > 0) yield break;
                if (aiPlayer.monsterZones[attacker.zoneIndex] != attacker || !attacker.CanAttack) continue;

                if (humanPlayer.MonsterCount == 0)
                {
                    duel.DirectAttack(aiPlayer, attacker);
                    ResetActiveAttackVfxContext();
                    yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
                    yield return new WaitForSeconds(0.95f);
                }
                else
                {
                    MonsterCard bestTarget = FindBestAITargetForAttacker(attacker);
                    if (bestTarget != null)
                    {
                        duel.Attack(aiPlayer, attacker, bestTarget);
                        ResetActiveAttackVfxContext();
                        yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
                        yield return new WaitForSeconds(0.95f);
                    }
                }
            }
        }
        else if (aiPlayer.swordsBlockTurnsRemaining > 0)
        {
            SetStatus($"Lớp Kiếm Bảo Hộ Của Ánh Sáng chặn AI tấn công (còn {aiPlayer.swordsBlockTurnsRemaining} lượt)!");
            yield return new WaitForSeconds(0.7f);
        }

        if (duel.IsGameOver) yield break;

        currentState = TurnState.EndPhase;

        // Luật End Phase cho AI: Nếu AI cầm nhiều hơn 6 lá trên tay -> bỏ bớt các lá yếu nhất xuống Mộ cho tới khi còn đúng 6 lá
        while (aiHand.Count > MAX_HAND_SIZE_END_PHASE && !duel.IsGameOver)
        {
            CardData toDiscard = aiHand
                .OrderBy(c => c == null ? -1 : (c.cardType == CardType.Monster ? c.atk : 900))
                .First();
            aiHand.Remove(toDiscard);
            if (toDiscard != null)
            {
                aiGraveyard.Add(toDiscard);
                SyncPlayerHandCounts();
                PlayBattleSFX("Summon");
                SetStatus($"[End Phase] AI có quá {MAX_HAND_SIZE_END_PHASE} lá trên tay -> Bỏ [{toDiscard.cardName}] xuống Mộ (còn {aiHand.Count}/{MAX_HAND_SIZE_END_PHASE} lá).");
            }
            yield return new WaitForSeconds(0.45f);
        }

        yield return new WaitForSeconds(0.5f);

        duel.EndTurn();
        StartPlayerTurn();
    }

    private MonsterCard FindBestAITargetForAttacker(MonsterCard attacker)
    {
        if (attacker == null || humanPlayer == null) return null;

        MonsterCard bestTarget = humanPlayer.Monsters
            .Where(t => !t.IsFaceDown && t.position == BattlePosition.FaceUpAttack && attacker.Atk > t.Atk)
            .OrderByDescending(t => t.Atk)
            .FirstOrDefault();

        if (bestTarget == null)
        {
            bestTarget = humanPlayer.Monsters
                .Where(t => !t.IsFaceDown && t.IsDefense && attacker.Atk > t.Def)
                .FirstOrDefault();
        }

        if (bestTarget == null)
        {
            bestTarget = humanPlayer.Monsters.FirstOrDefault(t => t.IsFaceDown);
        }

        return bestTarget;
    }

    private IEnumerator ActivatePlayerSetCardInSingleplayerReactionRoutine(int spellZone, Action onBackToReactionMenu = null)
    {
        if (spellZone == KURIBOH_HAND_REACTION_ZONE)
        {
            int kIdx = playerHand.FindIndex(c => c != null && c.monsterEffect == MonsterEffectType.KuribohHandGuard);
            if (kIdx >= 0)
            {
                RecordLocalValidGameAction();
                CardData kuriboh = playerHand[kIdx];
                playerHand.RemoveAt(kIdx);
                playerGraveyard.Add(kuriboh);
                SyncPlayerHandCounts();
                humanPlayer.kuribohShieldActive = true;
                PlayBattleSFX("Spell");
                SetStatus($"[Hiệu ứng nhanh - {kuriboh.cardName}] Bạn gửi {kuriboh.cardName} từ tay xuống Mộ: Nhận 0 sát thương chiến đấu từ đòn đánh này!");
                if (BattleAutoBoard.Instance != null) BattleAutoBoard.Instance.ForceRefreshHand();
                yield return new WaitForSeconds(0.65f);
            }
            yield break;
        }

        if (spellZone < 0 || spellZone >= Player.ZoneCount) yield break;

        CardData spellCard = playerSpellZones[spellZone];
        if (spellCard == null || !playerSpellFaceDown[spellZone]) yield break;

        if (!CanPlayerSetCardReact(spellZone, out string failReason))
        {
            SetStatus($"Không thể kích hoạt [{spellCard.cardName}]: {failReason}");
            yield break;
        }

        if (spellCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            bool didActivateRevive = false;
            OpenReviveGraveyardSelector(
                spellZone,
                spellCard,
                onConfirmedBeforeRevive: () =>
                {
                    didActivateRevive = true;
                    playerSpellFaceDown[spellZone] = false;
                    PlayBattleSFX("Spell");
                },
                onCancelled: () =>
                {
                    isGraveyardSelectionOpen = false;
                    SetStatus($"Đã hủy kích hoạt [{spellCard.cardName}]. Hãy chọn lá bài úp khác hoặc bấm Không kích hoạt.");
                    onBackToReactionMenu?.Invoke();
                },
                cancelButtonLabel: "QUAY LẠI CHỌN BÀI ÚP KHÁC",
                onSecondaryCancelled: () =>
                {
                    isGraveyardSelectionOpen = false;
                    SetStatus($"Đã bỏ qua kích hoạt [{spellCard.cardName}].");
                },
                secondaryCancelButtonLabel: "HỦY LUÔN (KHÔNG KÍCH HOẠT)"
            );
            yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
            if (didActivateRevive)
            {
                yield return new WaitForSeconds(0.7f);
            }
            yield break;
        }

        if (duel.ActivateSpell(humanPlayer, spellCard, allowOutOfTurnReaction: true))
        {
            RecordLocalValidGameAction();
            playerSpellFaceDown[spellZone] = false;
            if (spellCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
            {
                StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, spellZone, spellCard, 0.9f));
            }
            yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
            yield return new WaitForSeconds(0.75f);
        }
    }

    int EstimateAIMonsterAtk(CardData card)
    {
        if (card == null) return 0;
        if (card.monsterEffect == MonsterEffectType.DivineSlifer)
            return Mathf.Max(0, (aiHand.Count - 1) * 1000);
        if (card.monsterEffect == MonsterEffectType.DivineRa)
            return Mathf.Max(0, aiPlayer.lifePoints - 100);
        return card.atk;
    }

    MonsterCard AISummonBestMonster()
    {
        if (aiHand.Count == 0) return null;

        int currentMonstersOnField = aiPlayer.MonsterCount;

        var playableCards = aiHand
            .Select((card, index) => new
            {
                card,
                index,
                needTribute = Duel.RequiredTributes(card),
                estimatedAtk = EstimateAIMonsterAtk(card)
            })
            .Where(x => x.card.cardType == CardType.Monster &&
                        // Giữ Kuriboh trên tay để chặn sát thương trừ khi không còn lựa chọn nào khác
                        (x.card.monsterEffect != MonsterEffectType.KuribohHandGuard || (currentMonstersOnField == 0 && aiHand.Count == 1)) &&
                        currentMonstersOnField >= x.needTribute &&
                        (currentMonstersOnField - x.needTribute) < Player.ZoneCount)
            .OrderByDescending(x => x.estimatedAtk)
            .FirstOrDefault();

        if (playableCards == null) return null;

        List<MonsterCard> tributes = new List<MonsterCard>();
        if (playableCards.needTribute > 0)
        {
            tributes = aiPlayer.Monsters
                .OrderBy(m => m.Atk)
                .Take(playableCards.needTribute)
                .ToList();

            if (tributes.Max(t => t.Atk) >= playableCards.estimatedAtk) return null;
        }

        bool isFlipCard = playableCards.card.monsterEffect == MonsterEffectType.FlipReturnGraveyardToDeck ||
                          playableCards.card.monsterEffect == MonsterEffectType.FlipDestroyEnemyMonster ||
                          playableCards.card.monsterEffect == MonsterEffectType.FlipReturnEnemyMonsterToHand;

        BattlePosition pos;
        if (playableCards.card.IsDivineGod)
        {
            pos = BattlePosition.FaceUpAttack;
        }
        else if (isFlipCard)
        {
            pos = BattlePosition.FaceDownDefense;
        }
        else
        {
            pos = (playableCards.estimatedAtk >= playableCards.card.def)
                ? BattlePosition.FaceUpAttack
                : BattlePosition.FaceDownDefense;
        }

        CardData chosenCard = playableCards.card;
        aiHand.RemoveAt(playableCards.index);
        SyncPlayerHandCounts();

        MonsterCard summoned = duel.Summon(aiPlayer, chosenCard, pos, tributes);
        if (summoned != null)
        {
            foreach (var t in tributes)
            {
                if (t != null && !t.isMagicalHatDecoy && t.data != null)
                {
                    aiGraveyard.Add(t.data);
                }
            }
            return summoned;
        }

        aiHand.Insert(Mathf.Clamp(playableCards.index, 0, aiHand.Count), chosenCard);
        SyncPlayerHandCounts();
        return null;
    }

    void HandleSpellDrawRequest(Player p, int count)
    {
        bool isPlayer = (p == humanPlayer);
        for (int i = 0; i < count; i++)
        {
            if (!DrawCard(isPlayer)) break;
        }
    }

    CardData HandleSpellReviveRequest(Player p)
    {
        List<CardData> gy = (p == humanPlayer) ? playerGraveyard : aiGraveyard;
        CardData bestMonster = gy
            .Where(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned)
            .OrderByDescending(c => c.monsterEffect == MonsterEffectType.DivineSlifer ? aiHand.Count * 1000 : c.atk)
            .FirstOrDefault();

        if (bestMonster != null) gy.Remove(bestMonster);
        return bestMonster;
    }

    void HandleReturnMonsterToHand(Player owner, CardData data)
    {
        if (data == null) return;
        if (owner == humanPlayer) playerHand.Add(data);
        else aiHand.Add(data);
        SyncPlayerHandCounts();
    }

    CardData HandleSearchMonsterFromDeck(Player p)
    {
        bool isPlayer = (p == humanPlayer);
        List<CardData> deck = isPlayer ? playerDeck : aiDeck;
        List<CardData> hand = isPlayer ? playerHand : aiHand;

        CardData target = deck.FirstOrDefault(c => c.cardType == CardType.Monster && c.atk <= 1500)
                       ?? deck.FirstOrDefault(c => c.cardType == CardType.Monster);

        if (target != null)
        {
            deck.Remove(target);
            hand.Add(target);
            SyncPlayerHandCounts();
            ShuffleDeck(deck);
            UpdateDeckCountUI();
        }
        return target;
    }

    List<CardData> HandleRequestDeckSpellsTraps(Player p, int maxCount)
    {
        bool isPlayer = (p == humanPlayer);
        List<CardData> deck = isPlayer ? playerDeck : aiDeck;
        List<CardData> picked = new List<CardData>();

        for (int i = 0; i < deck.Count && picked.Count < maxCount; i++)
        {
            if (deck[i].IsSpellOrTrap)
            {
                picked.Add(deck[i]);
                deck.RemoveAt(i);
                i--;
            }
        }

        if (picked.Count > 0)
        {
            ShuffleDeck(deck);
            UpdateDeckCountUI();
        }
        return picked;
    }

    void HandleSendCardToGraveyard(Player owner, CardData data)
    {
        if (data == null) return;
        if (owner == humanPlayer) playerGraveyard.Add(data);
        else aiGraveyard.Add(data);
    }

    void HandleSwordsExpired(Player unblockedPlayer)
    {
        bool casterIsPlayer = (unblockedPlayer == aiPlayer);
        CardData[] zones = casterIsPlayer ? playerSpellZones : aiSpellZones;
        bool[] faceDowns = casterIsPlayer ? playerSpellFaceDown : aiSpellFaceDown;
        List<CardData> gy = casterIsPlayer ? playerGraveyard : aiGraveyard;

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (zones[i] != null && !faceDowns[i] && zones[i].spellEffect == SpellEffectType.SwordsOfRevealingLight)
            {
                gy.Add(zones[i]);
                zones[i] = null;
                faceDowns[i] = false;
                break;
            }
        }
    }

    void HandleDuelLog(string msg)
    {
        Debug.Log($"[DUEL] {msg}");
        SetStatus(msg);
    }

    void HandleDamage(Player damagedPlayer, int amount)
    {
        bool isPlayer = (damagedPlayer == humanPlayer);
        int startLP = isPlayer ? displayedPlayerLP : displayedAILP;
        int targetLP = damagedPlayer.lifePoints;

        if (amount > 0)
        {
            PlayBattleSFX("Damage");
            if (isPlayer && playerLPCoroutine != null) StopCoroutine(playerLPCoroutine);
            if (!isPlayer && aiLPCoroutine != null) StopCoroutine(aiLPCoroutine);

            Coroutine routine = StartCoroutine(AnimateLPChange(isPlayer, startLP, targetLP, amount, isDamage: true));
            if (isPlayer) playerLPCoroutine = routine;
            else aiLPCoroutine = routine;
        }
        else if (targetLP > startLP)
        {
            int healAmount = targetLP - startLP;
            if (isPlayer && playerLPCoroutine != null) StopCoroutine(playerLPCoroutine);
            if (!isPlayer && aiLPCoroutine != null) StopCoroutine(aiLPCoroutine);

            Coroutine routine = StartCoroutine(AnimateLPChange(isPlayer, startLP, targetLP, healAmount, isDamage: false));
            if (isPlayer) playerLPCoroutine = routine;
            else aiLPCoroutine = routine;
        }
        else
        {
            UpdateLPUI();
        }
    }

    IEnumerator AnimateLPChange(bool isPlayer, int startLP, int targetLP, int deltaAmount, bool isDamage)
    {
        TextMeshProUGUI lpText = isPlayer ? playerLPText : aiLPText;
        TextMeshProUGUI changeText = isPlayer ? playerLPDamageText : aiLPDamageText;

        if (changeText != null)
        {
            changeText.gameObject.SetActive(true);
            changeText.color = Color.white;
            changeText.text = isDamage ? "-1" : "+1";
        }

        float duration = Mathf.Max(0.1f, lpRollDuration);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            int currentDelta = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, deltaAmount, t)));
            int currentLP = Mathf.RoundToInt(Mathf.Lerp(startLP, targetLP, t));

            if (isPlayer) displayedPlayerLP = currentLP;
            else displayedAILP = currentLP;

            if (changeText != null)
                changeText.text = isDamage ? $"-{currentDelta}" : $"+{currentDelta}";

            if (lpText != null)
                lpText.text = currentLP.ToString();

            yield return null;
        }

        if (isPlayer) displayedPlayerLP = targetLP;
        else displayedAILP = targetLP;

        if (changeText != null)
            changeText.text = isDamage ? $"-{deltaAmount}" : $"+{deltaAmount}";

        if (lpText != null)
            lpText.text = targetLP.ToString();

        yield return new WaitForSeconds(0.4f);

        if (changeText != null)
        {
            changeText.gameObject.SetActive(false);
        }

        if (isPlayer) playerLPCoroutine = null;
        else aiLPCoroutine = null;

        if (!IsMultiplayer && targetLP <= 0 && currentState != TurnState.GameOver)
        {
            string winner = isPlayer ? "AI" : "Player";
            string loserName = isPlayer ? humanPlayer.name : aiPlayer.name;
            EndGame(winner, $"{loserName} đã hết LP!");
        }
    }

    void Update()
    {
        // Tự động giải phóng cờ khóa nếu bảng chọn Mộ / Bỏ bài đã đóng để không bao giờ bị kẹt thao tác trong Main Phase
        if ((isGraveyardSelectionOpen || isEndPhaseDiscardOpen) &&
            BattleAutoBoard.Instance != null &&
            !BattleAutoBoard.Instance.IsGraveyardModalOpen)
        {
            isGraveyardSelectionOpen = false;
            isEndPhaseDiscardOpen = false;
        }

        UpdateSingleplayerAntiAfkTimer();
    }

    void LateUpdate()
    {
        ResetActiveAttackVfxContext();
    }

    public void RecordLocalValidGameAction()
    {
        localAfkIdleElapsed = 0f;
        if (!IsMultiplayer && lastDisplayedLocalAfkSeconds != -1)
        {
            lastDisplayedLocalAfkSeconds = -1;
            UpdateAfkCountdownBanner(-1, true);
        }
    }

    void UpdateSingleplayerAntiAfkTimer()
    {
        if (IsMultiplayer) return;

        if (currentState == TurnState.GameOver || duel == null || duel.IsGameOver)
        {
            if (lastDisplayedLocalAfkSeconds != -1)
            {
                lastDisplayedLocalAfkSeconds = -1;
                UpdateAfkCountdownBanner(-1, true);
            }
            return;
        }

        bool isWaitingForHuman = (duel.Current == humanPlayer &&
                                  (currentState == TurnState.MainPhase || currentState == TurnState.BattlePhase || isEndPhaseDiscardOpen))
                                 || waitingTrapDecision
                                 || isGraveyardSelectionOpen
                                 || (trapPromptPanel != null && trapPromptPanel.activeSelf);

        if (!isWaitingForHuman)
        {
            localAfkIdleElapsed = 0f;
            if (lastDisplayedLocalAfkSeconds != -1)
            {
                lastDisplayedLocalAfkSeconds = -1;
                UpdateAfkCountdownBanner(-1, true);
            }
            return;
        }

        localAfkIdleElapsed += Time.deltaTime;
        if (localAfkIdleElapsed < AFK_IDLE_GRACE_SECONDS)
        {
            if (lastDisplayedLocalAfkSeconds != -1)
            {
                lastDisplayedLocalAfkSeconds = -1;
                UpdateAfkCountdownBanner(-1, true);
            }
            return;
        }

        float countdownElapsed = localAfkIdleElapsed - AFK_IDLE_GRACE_SECONDS;
        if (countdownElapsed >= AFK_COUNTDOWN_SECONDS)
        {
            UpdateAfkCountdownBanner(-1, true);
            EndGame("AI", "Bạn bị xử thua do không có thao tác trong thời gian quy định (Anti-AFK)!");
            return;
        }

        int remainSec = Mathf.CeilToInt(Mathf.Max(1f, AFK_COUNTDOWN_SECONDS - countdownElapsed));
        if (remainSec != lastDisplayedLocalAfkSeconds)
        {
            lastDisplayedLocalAfkSeconds = remainSec;
            UpdateAfkCountdownBanner(remainSec, isForLocalPlayer: true, LocalDisplayName);
        }
    }

    public void UpdateAfkCountdownBanner(int remainingSeconds, bool isForLocalPlayer, string targetPlayerName = "")
    {
        if (afkBannerBoxRT == null || afkBannerText == null) return;

        if (remainingSeconds < 0 || currentState == TurnState.GameOver)
        {
            if (afkBannerBoxRT.gameObject.activeSelf)
            {
                afkBannerBoxRT.gameObject.SetActive(false);
            }
            return;
        }

        if (isForLocalPlayer)
        {
            afkBannerBg.color = new Color(0.80f, 0.12f, 0.10f, 0.95f);
            afkBannerText.color = new Color(1f, 0.96f, 0.72f, 1f);
            afkBannerText.text = $"CẢNH BÁO AFK: Bạn chưa thao tác game! Tự động xử thua sau: {remainingSeconds}s";
        }
        else
        {
            string oppName = string.IsNullOrWhiteSpace(targetPlayerName) ? OpponentDisplayName : targetPlayerName;
            afkBannerBg.color = new Color(0.65f, 0.36f, 0.06f, 0.94f);
            afkBannerText.color = Color.white;
            afkBannerText.text = $"Anti-AFK: {oppName} chưa thao tác — Đếm ngược xử thua còn: {remainingSeconds}s";
        }

        if (!afkBannerBoxRT.gameObject.activeSelf)
        {
            afkBannerBoxRT.gameObject.SetActive(true);
        }
        afkBannerBoxRT.SetAsLastSibling();
        LayoutRebuilder.ForceRebuildLayoutImmediate(afkBannerBoxRT);
    }

    void ResetActiveAttackVfxContext()
    {
        activeAttackDestroyBatch = null;
        activeAttackImpactDelay = 0f;
        activeAttackDestroySfxFlag = null;
    }

    void HandleAttackDeclared(MonsterCard attacker, MonsterCard target)
    {
        PlayBattleSFX("Attack");
        if (BattleAutoBoard.Instance == null || attacker == null) return;

        bool attackerIsPlayer = (attacker.owner == humanPlayer);
        bool isDirect = (target == null);
        bool targetIsPlayer = target != null ? (target.owner == humanPlayer) : !attackerIsPlayer;
        int targetZone = target != null ? target.zoneIndex : -1;

        List<PendingAttackDestroySlot> batch = new List<PendingAttackDestroySlot>();
        bool[] sfxFlag = new bool[1];
        activeAttackDestroyBatch = batch;
        activeAttackDestroySfxFlag = sfxFlag;

        activeAttackImpactDelay = BattleAutoBoard.Instance.PlayAttackFireballVfx(
            attackerIsPlayer,
            attacker.zoneIndex,
            isDirect,
            targetIsPlayer,
            targetZone,
            onImpact: () =>
            {
                if (BattleAutoBoard.Instance == null) return;
                for (int i = 0; i < batch.Count; i++)
                {
                    BattleAutoBoard.Instance.FlushDestroyHoldImmediate(batch[i].isPlayer, batch[i].zoneIndex);
                }
            }
        );
    }

    void HandleMonsterShattered(MonsterCard shatteredCard)
    {
        if (shatteredCard == null || BattleAutoBoard.Instance == null) return;

        bool isPlayer = (shatteredCard.owner == humanPlayer);
        Sprite spr = shatteredCard.isMagicalHatDecoy
            ? (shatteredCard.originalSpellTrapData != null ? shatteredCard.originalSpellTrapData.cardSprite : shatteredCard.data?.cardSprite)
            : shatteredCard.data?.cardSprite;

        if (activeAttackDestroyBatch != null && activeAttackImpactDelay > 0.001f)
        {
            bool[] sfxFlag = activeAttackDestroySfxFlag;
            activeAttackDestroyBatch.Add(new PendingAttackDestroySlot
            {
                isPlayer = isPlayer,
                zoneIndex = shatteredCard.zoneIndex
            });

            BattleAutoBoard.Instance.TriggerMonsterDestroyVfx(
                isPlayer,
                shatteredCard.zoneIndex,
                spr,
                shatteredCard.IsDefense,
                impactDelay: activeAttackImpactDelay + 0.08f,
                onShatter: () =>
                {
                    if (sfxFlag != null && !sfxFlag[0])
                    {
                        sfxFlag[0] = true;
                        PlayBattleSFX("Destroy");
                    }
                }
            );
        }
        else
        {
            BattleAutoBoard.Instance.TriggerMonsterDestroyVfx(
                isPlayer,
                shatteredCard.zoneIndex,
                spr,
                shatteredCard.IsDefense,
                impactDelay: 0f
            );
        }
    }

    void HandleMonsterSummoned(MonsterCard summoned)
    {
        PlayBattleSFX("Summon");
        if (summoned == null || summoned.IsFaceDown || summoned.data == null) return;
        if (!summoned.data.IsDivineGod || BattleAutoBoard.Instance == null) return;

        BattleAutoBoard.Instance.PlayGodSummonVfx(
            summoned.data.monsterEffect,
            summoned.data.cardSprite,
            summoned.owner == humanPlayer,
            summoned.zoneIndex
        );
    }

    void CancelDelayedRaPrompt()
    {
        if (delayedRaPromptCoroutine != null)
        {
            StopCoroutine(delayedRaPromptCoroutine);
            delayedRaPromptCoroutine = null;
        }
    }

    void ShowRaPromptPanel(string message, float delaySeconds)
    {
        if (trapPromptPanel == null) return;
        CancelDelayedRaPrompt();

        if (delaySeconds <= 0.01f)
        {
            if (trapPromptText != null) trapPromptText.text = message;
            trapPromptPanel.transform.SetAsLastSibling();
            trapPromptPanel.SetActive(true);
            return;
        }

        delayedRaPromptCoroutine = StartCoroutine(ShowRaPromptAfterDelayRoutine(message, delaySeconds));
    }

    IEnumerator ShowRaPromptAfterDelayRoutine(string message, float delaySeconds)
    {
        yield return new WaitForSecondsRealtime(delaySeconds);
        delayedRaPromptCoroutine = null;
        if (currentState == TurnState.GameOver || trapPromptPanel == null) yield break;
        if (IsMultiplayer && (!isNetworkPromptOpen || lastOpenedPromptType != NetPendingPromptType.RaSummonPayLP)) yield break;
        if (!IsMultiplayer && pendingRaSummonZoneIndex < 0) yield break;

        if (trapPromptText != null) trapPromptText.text = message;
        trapPromptPanel.transform.SetAsLastSibling();
        trapPromptPanel.SetActive(true);
    }

    bool TriggerNetworkSnapshotVfx(MatchStateSnapshot snap, Sprite[] prevMySprites, Sprite[] prevOppSprites)
    {
        if (snap == null || BattleAutoBoard.Instance == null) return false;

        // Hiệu ứng triệu hồi riêng biệt của 3 Vị Thần Ai Cập (Obelisk / Slifer / Ra)
        if (snap.vfxGodSummon != null && snap.vfxGodSummon.hasGodSummon)
        {
            MonsterEffectType godType = (MonsterEffectType)snap.vfxGodSummon.godEffectType;
            Sprite godSprite = !string.IsNullOrEmpty(snap.vfxGodSummon.cardName)
                ? CreateCardData(snap.vfxGodSummon.cardName)?.cardSprite
                : null;
            BattleAutoBoard.Instance.PlayGodSummonVfx(
                godType,
                godSprite,
                snap.vfxGodSummon.isMe,
                snap.vfxGodSummon.zoneIndex
            );
        }

        bool hasAttack = snap.vfxAttack != null && snap.vfxAttack.hasAttack;
        bool hasDestroys = snap.vfxDestroys != null && snap.vfxDestroys.Count > 0;

        if (hasAttack)
        {
            List<PendingAttackDestroySlot> netBatch = new List<PendingAttackDestroySlot>();
            bool[] netSfxFlag = new bool[1];

            float impactDelay = BattleAutoBoard.Instance.PlayAttackFireballVfx(
                snap.vfxAttack.attackerIsMe,
                snap.vfxAttack.attackerZone,
                snap.vfxAttack.isDirectAttack,
                snap.vfxAttack.targetIsMe,
                snap.vfxAttack.targetZone,
                onImpact: () =>
                {
                    if (BattleAutoBoard.Instance == null) return;
                    for (int i = 0; i < netBatch.Count; i++)
                    {
                        BattleAutoBoard.Instance.FlushDestroyHoldImmediate(netBatch[i].isPlayer, netBatch[i].zoneIndex);
                    }
                }
            );

            if (hasDestroys)
            {
                foreach (var d in snap.vfxDestroys)
                {
                    if (d == null || d.zoneIndex < 0 || d.zoneIndex >= Player.ZoneCount) continue;

                    Sprite spr = ResolveNetworkDestroySprite(d, prevMySprites, prevOppSprites);
                    netBatch.Add(new PendingAttackDestroySlot { isPlayer = d.isMe, zoneIndex = d.zoneIndex });
                    BattleAutoBoard.Instance.TriggerMonsterDestroyVfx(
                        d.isMe,
                        d.zoneIndex,
                        spr,
                        d.isDefense,
                        impactDelay: impactDelay + 0.08f,
                        onShatter: () =>
                        {
                            if (!netSfxFlag[0])
                            {
                                netSfxFlag[0] = true;
                                PlayBattleSFX("Destroy");
                            }
                        }
                    );
                }
                return true;
            }
            return false;
        }

        if (hasDestroys)
        {
            foreach (var d in snap.vfxDestroys)
            {
                if (d == null || d.zoneIndex < 0 || d.zoneIndex >= Player.ZoneCount) continue;
                Sprite spr = ResolveNetworkDestroySprite(d, prevMySprites, prevOppSprites);
                BattleAutoBoard.Instance.TriggerMonsterDestroyVfx(
                    d.isMe,
                    d.zoneIndex,
                    spr,
                    d.isDefense,
                    impactDelay: 0f
                );
            }
        }

        return false;
    }

    Sprite ResolveNetworkDestroySprite(NetVfxDestroyDto d, Sprite[] prevMySprites, Sprite[] prevOppSprites)
    {
        if (d != null && !string.IsNullOrEmpty(d.cardName))
        {
            CardData cd = CreateCardData(d.cardName);
            if (cd != null && cd.cardSprite != null) return cd.cardSprite;
        }
        if (d != null && d.zoneIndex >= 0 && d.zoneIndex < Player.ZoneCount)
        {
            return d.isMe ? prevMySprites[d.zoneIndex] : prevOppSprites[d.zoneIndex];
        }
        return null;
    }

    void HandleMonsterDestroyed(MonsterCard destroyedCard)
    {
        if (activeAttackDestroyBatch == null)
        {
            PlayBattleSFX("Destroy");
        }
        if (destroyedCard.owner == humanPlayer)
            playerGraveyard.Add(destroyedCard.data);
        else
            aiGraveyard.Add(destroyedCard.data);
    }

    void EndGame(string winner, string reason)
    {
        if (currentState == TurnState.GameOver) return;
        currentState = TurnState.GameOver;
        isGraveyardSelectionOpen = false;
        isEndPhaseDiscardOpen = false;
        UpdateAfkCountdownBanner(-1, false);
        StopAllCoroutines();

        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        if (humanPlayer != null) displayedPlayerLP = humanPlayer.lifePoints;
        if (aiPlayer != null) displayedAILP = aiPlayer.lifePoints;
        UpdateLPUI();

        bool playerWon = winner == "Player";
        PlayBattleSFX(playerWon ? "Win" : "Lose");

        string title = playerWon ? "CHIẾN THẮNG!" : "THẤT BẠI!";
        SetStatus($"{title} ({reason})");

        if (gameOverPanel != null)
        {
            gameOverPanel.SetActive(true);
            if (gameOverTitleText != null) gameOverTitleText.text = title;
            if (gameOverReasonText != null) gameOverReasonText.text = reason;
        }
    }

    public void RestartBattle()
    {
        if (IsMultiplayer)
        {
            if (opponentLeftMultiplayerMatch)
            {
                string msg = "Đối thủ đã rời trận. Hãy bấm 'VỀ MENU' để tạo hoặc vào phòng mới!";
                SetStatus(msg);
                UpdateGameOverReasonIfVisible(msg);
                return;
            }

            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.RequestRematch();
            }
            return;
        }
        Time.timeScale = 1f;
        SceneManager.LoadScene("BattleScene");
    }

    public void ReturnToMainMenu()
    {
        Time.timeScale = 1f;
        if (IsMultiplayer)
        {
            if (NetworkDuelController.Instance != null)
            {
                NetworkDuelController.Instance.NotifyLeavingMatch();
            }
            if (MultiplayerLobbyManager.Instance != null)
            {
                MultiplayerLobbyManager.Instance.LeaveRoom("Đã rời trận đấu 1v1.");
            }
            else
            {
                MatchSessionConfig.ResetToSingleplayer();
            }
        }
        else
        {
            MatchSessionConfig.ResetToSingleplayer();
        }
        SceneManager.LoadScene("SampleScene");
    }

    void PlayBattleSFX(string id)
    {
        if (SoundManager.Instance != null && SoundManager.Instance.sfxList != null)
        {
            if (SoundManager.Instance.sfxList.Exists(x => x != null && x.id == id))
            {
                SoundManager.Instance.PlaySFX(id);
            }
        }
    }

    void UpdateLPUI()
    {
        if (playerLPText != null && humanPlayer != null) playerLPText.text = displayedPlayerLP.ToString();
        if (aiLPText != null && aiPlayer != null) aiLPText.text = displayedAILP.ToString();
    }

    void UpdateDeckCountUI()
    {
        if (playerDeckCountText != null) playerDeckCountText.text = LocalDeckCount.ToString();
        if (aiDeckCountText != null) aiDeckCountText.text = OpponentDeckCount.ToString();
    }

    public void SetStatus(string msg)
    {
        if (statusText != null)
        {
            statusText.color = Color.white;
            statusText.text = msg;
        }
        if (statusBoxRT != null)
        {
            statusBoxRT.gameObject.SetActive(!string.IsNullOrEmpty(msg));
            LayoutRebuilder.ForceRebuildLayoutImmediate(statusBoxRT);
        }
    }
}