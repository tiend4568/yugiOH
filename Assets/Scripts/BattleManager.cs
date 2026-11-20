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
    public int startingLP = 4000;
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

    private string pendingFlipCardName = "";
    private bool isGraveyardSelectionOpen = false;

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
                trapPromptPanel.SetActive(false);
                if (IsMultiplayer)
                {
                    SubmitMultiplayerPromptChoice(1);
                }
                else
                {
                    playerAcceptedTrap = true;
                    waitingTrapDecision = false;
                }
            });

            CreatePopupButton(trapPromptPanel.transform, "BỎ QUA", new Vector2(105f, -45f), new Color(0.32f, 0.32f, 0.38f, 1f), () =>
            {
                trapPromptPanel.SetActive(false);
                if (IsMultiplayer)
                {
                    SubmitMultiplayerPromptChoice(0);
                }
                else
                {
                    playerAcceptedTrap = false;
                    waitingTrapDecision = false;
                }
            });

            trapPromptPanel.SetActive(false);
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
        currentState = (TurnState)snap.turnState;

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

        // 3. 5 ô Quái thú của mình & đối thủ
        ApplySnapshotMonsterZones(humanPlayer, snap.myMonsters, isOpponent: false);
        ApplySnapshotMonsterZones(aiPlayer, snap.opponentMonsters, isOpponent: true);

        // 4. 5 ô Phép/Bẫy của mình & đối thủ
        ApplySnapshotSpellZones(playerSpellZones, playerSpellFaceDown, snap.mySpells, isOpponent: false);
        ApplySnapshotSpellZones(aiSpellZones, aiSpellFaceDown, snap.opponentSpells, isOpponent: true);

        hasAppliedInitialSnapshot = true;

        UpdateDeckCountUI();
        RefreshEndTurnButtonState();

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
                    PlayBattleSFX(sfx);
                }
            }
        }
        else if (!string.IsNullOrEmpty(snap.sfxToPlay) && snap.sfxToPlay != "Damage")
        {
            PlayBattleSFX(snap.sfxToPlay);
        }

        if (!string.IsNullOrEmpty(snap.statusMessage))
        {
            SetStatus(snap.statusMessage);
        }

        // Xử lý Prompt bất đồng bộ (Bước 5: FLIP chọn Mộ hoặc Hỏi kích hoạt Bẫy Magical Hats)
        ApplySnapshotPendingPrompt(snap);

        if (snap.isGameOver)
        {
            ShowMultiplayerGameOver(snap.didLocalWin, snap.gameOverReason);
        }
    }

    private void ApplySnapshotPendingPrompt(MatchStateSnapshot snap)
    {
        activeNetworkPromptId = snap.pendingPromptId;
        NetPendingPromptType pType = (NetPendingPromptType)snap.pendingPromptType;

        if (pType == NetPendingPromptType.None || snap.isGameOver)
        {
            if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
            if (isNetworkPromptOpen)
            {
                isNetworkPromptOpen = false;
                isGraveyardSelectionOpen = false;
                if (BattleAutoBoard.Instance != null)
                {
                    BattleAutoBoard.Instance.CloseGraveyardModalIfOpen();
                    BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
                }
            }
            return;
        }

        if (!snap.isPromptForMe)
        {
            if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
            if (BattleAutoBoard.Instance != null)
            {
                BattleAutoBoard.Instance.CloseDefenseReactionModalIfOpen();
            }
            return;
        }

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
        else if (pType == NetPendingPromptType.TrapMagicalHats)
        {
            if (!isNetworkPromptOpen || lastOpenedPromptId != snap.pendingPromptId)
            {
                isNetworkPromptOpen = true;
                lastOpenedPromptId = snap.pendingPromptId;
                if (trapPromptPanel != null) trapPromptPanel.SetActive(false);

                if (BattleAutoBoard.Instance != null)
                {
                    string modalTitle = string.IsNullOrEmpty(snap.promptTitle)
                        ? "ĐỐI THỦ TẤN CÔNG! CHỌN 1 LÁ BÀI PHÉP / BẪY ĐANG ÚP ĐỂ KÍCH HOẠT HOẶC BỎ QUA"
                        : snap.promptTitle;

                    BattleAutoBoard.Instance.OpenDefenseReactionModal(
                        modalTitle,
                        chosenSpellZone => HandleMultiplayerDefenseReactionZonePicked(chosenSpellZone)
                    );
                }
                else
                {
                    SubmitMultiplayerDefenseReaction(-1, -1);
                }
            }
        }
    }

    private void HandleMultiplayerDefenseReactionZonePicked(int chosenSpellZone)
    {
        if (chosenSpellZone < 0 || chosenSpellZone >= Player.ZoneCount)
        {
            SubmitMultiplayerDefenseReaction(-1, -1);
            return;
        }

        CardData chosenCard = playerSpellZones[chosenSpellZone];
        if (chosenCard != null && chosenCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            if (BattleAutoBoard.Instance != null && playerGraveyard.Any(c => c.cardType == CardType.Monster))
            {
                isGraveyardSelectionOpen = true;
                SetStatus($"[{chosenCard.cardName}] Chọn 1 quái thú trong Mộ bài để hồi sinh ngay!");
                BattleAutoBoard.Instance.OpenGraveyardSelection(
                    playerGraveyard,
                    gyIndex =>
                    {
                        isGraveyardSelectionOpen = false;
                        SubmitMultiplayerDefenseReaction(chosenSpellZone, gyIndex);
                    },
                    customTitle: $"[{chosenCard.cardName}] CHỌN QUÁI THÚ TRONG MỘ ĐỂ HỒI SINH",
                    filter: c => c.cardType == CardType.Monster
                );
                return;
            }
        }

        SubmitMultiplayerDefenseReaction(chosenSpellZone, -1);
    }

    public void SubmitMultiplayerDefenseReaction(int chosenSpellZone, int graveyardTargetIndex = -1)
    {
        isNetworkPromptOpen = false;
        isGraveyardSelectionOpen = false;
        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        if (BattleAutoBoard.Instance != null)
        {
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
        isGraveyardSelectionOpen = false;
        if (trapPromptPanel != null) trapPromptPanel.SetActive(false);
        if (BattleAutoBoard.Instance != null)
        {
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
        bool gyHasMonster = playerGraveyard.Any(c => c != null && c.cardType == CardType.Monster);

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
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (CanPlayerSetCardReact(i, out _)) return true;
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
        duel.OnSummoned += _ => PlayBattleSFX("Summon");
        duel.OnSpellActivated += _ => PlayBattleSFX("Spell");
        duel.OnAttackDeclared += (_, __) => PlayBattleSFX("Attack");
        duel.OnDamage += HandleDamage;
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
            EndGame(winner, $"{loser} đã hết bài để rút (Deck Out)!");
            return false;
        }

        CardData drawnCard = targetDeck[0];
        targetDeck.RemoveAt(0);
        targetHand.Add(drawnCard);

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
            if (!playerGraveyard.Any(c => c.cardType == CardType.Monster))
            {
                SetStatus("Không có quái thú nào trong Mộ bài để hồi sinh!");
                return false;
            }

            if (IsMultiplayer)
            {
                isGraveyardSelectionOpen = true;
                SetStatus($"[{spellCard.cardName}] Chọn 1 quái thú trong Phần Mộ để hồi sinh!");

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
                        filter: c => c.cardType == CardType.Monster
                    );
                }
                return true;
            }

            playerHand.RemoveAt(handIndex);
            playerSpellZones[spellZone] = spellCard;
            playerSpellFaceDown[spellZone] = false;
            PlayBattleSFX("Spell");

            if (BattleAutoBoard.Instance != null)
            {
                BattleAutoBoard.Instance.ForceRefreshHand();
            }

            OpenReviveGraveyardSelector(spellZone, spellCard);
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
            playerHand.RemoveAt(handIndex);
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

        playerHand.RemoveAt(handIndex);
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
            if (!playerGraveyard.Any(c => c.cardType == CardType.Monster))
            {
                SetStatus("Không có quái thú nào trong Mộ bài để hồi sinh!");
                return false;
            }

            if (IsMultiplayer)
            {
                isGraveyardSelectionOpen = true;
                SetStatus($"[{spellCard.cardName}] Chọn 1 quái thú trong Phần Mộ để hồi sinh!");

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
                        filter: c => c.cardType == CardType.Monster
                    );
                }
                return true;
            }

            playerSpellFaceDown[spellZone] = false;
            PlayBattleSFX("Spell");
            OpenReviveGraveyardSelector(spellZone, spellCard);
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
            playerSpellFaceDown[spellZone] = false;
            if (spellCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
            {
                StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, spellZone, spellCard, 0.9f));
            }
            return true;
        }
        return false;
    }

    void OpenReviveGraveyardSelector(int spellZone, CardData spellCard)
    {
        isGraveyardSelectionOpen = true;
        SetStatus($"[{spellCard.cardName}] Chọn 1 quái thú trong Phần Mộ để hồi sinh!");

        if (BattleAutoBoard.Instance != null)
        {
            BattleAutoBoard.Instance.OpenGraveyardSelection(
                playerGraveyard,
                gyIndex => ConfirmPlayerReviveFromGraveyard(gyIndex, spellZone, spellCard),
                customTitle: $"[{spellCard.cardName}] CHỌN QUÁI THÚ TRONG MỘ ĐỂ HỒI SINH",
                filter: c => c.cardType == CardType.Monster
            );
        }
        else
        {
            int fallbackIdx = playerGraveyard.FindIndex(c => c.cardType == CardType.Monster);
            ConfirmPlayerReviveFromGraveyard(fallbackIdx, spellZone, spellCard);
        }
    }

    public void ConfirmPlayerReviveFromGraveyard(int graveyardIndex, int spellZone, CardData spellCard)
    {
        isGraveyardSelectionOpen = false;
        if (graveyardIndex < 0 || graveyardIndex >= playerGraveyard.Count) return;

        CardData chosenMonster = playerGraveyard[graveyardIndex];
        if (chosenMonster.cardType != CardType.Monster) return;

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

        MonsterCard summoned = duel.Summon(humanPlayer, cardToSummon, pos, tributes, false, preferredZone);
        if (summoned != null)
        {
            playerHand.RemoveAt(handIndex);
            foreach (var t in tributes) playerGraveyard.Add(t.data);
            return true;
        }
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

        return duel.ChangePosition(humanPlayer, humanPlayer.monsterZones[myZoneIndex]);
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

        return duel.Attack(humanPlayer, humanPlayer.monsterZones[myZoneIndex], aiPlayer.monsterZones[enemyZoneIndex]);
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

        return duel.DirectAttack(humanPlayer, humanPlayer.monsterZones[myZoneIndex]);
    }

    public void EndPlayerTurn()
    {
        if (isGraveyardSelectionOpen) return;
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

        currentState = TurnState.EndPhase;
        duel.EndTurn();
        StartCoroutine(AITurnRoutine());
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
                if (duel.ActivateSpell(aiPlayer, card))
                {
                    aiHand.RemoveAt(i);
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
            }
        }

        AISummonBestMonster();
        yield return new WaitForSeconds(0.7f);

        foreach (var m in aiPlayer.Monsters.ToList())
        {
            if (m.IsDefense && m.CanChangePosition && !m.isMagicalHatDecoy && (m.Atk >= 1500 || (m.IsFaceDown && aiGraveyard.Count > 0)))
            {
                duel.ChangePosition(aiPlayer, m);
                yield return new WaitForSeconds(0.4f);
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

                // Khi AI tuyên bố tấn công: nếu người chơi có BẤT KỲ lá bài Phép/Bẫy nào đang úp có thể kích hoạt, mở Menu chọn lựa!
                if (HasAnyActivatablePlayerSetCardForReaction() && BattleAutoBoard.Instance != null)
                {
                    int pickedZone = -1;
                    waitingTrapDecision = true;

                    string targetDesc = humanPlayer.MonsterCount == 0
                        ? "trực tiếp vào LP của bạn"
                        : (previewTarget != null && !previewTarget.IsFaceDown ? $"[{previewTarget.data.cardName}]" : "quái thú úp của bạn");

                    BattleAutoBoard.Instance.OpenDefenseReactionModal(
                        $"AI dùng [{attacker.data.cardName}] (ATK {attacker.Atk}) tấn công {targetDesc}!\nCHỌN 1 LÁ BÀI PHÉP / BẪY ĐANG ÚP ĐỂ KÍCH HOẠT HOẶC BỎ QUA",
                        z =>
                        {
                            pickedZone = z;
                            waitingTrapDecision = false;
                        }
                    );

                    yield return new WaitUntil(() => !waitingTrapDecision || currentState == TurnState.GameOver);

                    if (pickedZone >= 0 && currentState != TurnState.GameOver)
                    {
                        yield return StartCoroutine(ActivatePlayerSetCardInSingleplayerReactionRoutine(pickedZone));
                    }
                }

                if (duel.IsGameOver || aiPlayer.swordsBlockTurnsRemaining > 0) yield break;
                if (aiPlayer.monsterZones[attacker.zoneIndex] != attacker || !attacker.CanAttack) continue;

                if (humanPlayer.MonsterCount == 0)
                {
                    duel.DirectAttack(aiPlayer, attacker);
                    yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
                    yield return new WaitForSeconds(0.7f);
                }
                else
                {
                    MonsterCard bestTarget = FindBestAITargetForAttacker(attacker);
                    if (bestTarget != null)
                    {
                        duel.Attack(aiPlayer, attacker, bestTarget);
                        yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
                        yield return new WaitForSeconds(0.7f);
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

    private IEnumerator ActivatePlayerSetCardInSingleplayerReactionRoutine(int spellZone)
    {
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
            playerSpellFaceDown[spellZone] = false;
            PlayBattleSFX("Spell");
            OpenReviveGraveyardSelector(spellZone, spellCard);
            yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
            yield return new WaitForSeconds(0.7f);
            yield break;
        }

        if (duel.ActivateSpell(humanPlayer, spellCard, allowOutOfTurnReaction: true))
        {
            playerSpellFaceDown[spellZone] = false;
            if (spellCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
            {
                StartCoroutine(ClearSpellZoneAfterDelay(isPlayer: true, spellZone, spellCard, 0.9f));
            }
            yield return new WaitUntil(() => !isGraveyardSelectionOpen || currentState == TurnState.GameOver);
            yield return new WaitForSeconds(0.75f);
        }
    }

    void AISummonBestMonster()
    {
        if (aiHand.Count == 0) return;

        int currentMonstersOnField = aiPlayer.MonsterCount;

        var playableCards = aiHand
            .Select((card, index) => new { card, index, needTribute = Duel.RequiredTributes(card.level) })
            .Where(x => x.card.cardType == CardType.Monster &&
                        currentMonstersOnField >= x.needTribute &&
                        (currentMonstersOnField - x.needTribute) < Player.ZoneCount)
            .OrderByDescending(x => x.card.atk)
            .FirstOrDefault();

        if (playableCards == null) return;

        List<MonsterCard> tributes = new List<MonsterCard>();
        if (playableCards.needTribute > 0)
        {
            tributes = aiPlayer.Monsters
                .OrderBy(m => m.Atk)
                .Take(playableCards.needTribute)
                .ToList();

            if (tributes.Max(t => t.Atk) >= playableCards.card.atk) return;
        }

        bool isFlipCard = playableCards.card.monsterEffect == MonsterEffectType.FlipReturnGraveyardToDeck ||
                          playableCards.card.monsterEffect == MonsterEffectType.FlipDestroyEnemyMonster ||
                          playableCards.card.monsterEffect == MonsterEffectType.FlipReturnEnemyMonsterToHand;

        BattlePosition pos = isFlipCard
            ? BattlePosition.FaceDownDefense
            : (playableCards.card.atk >= playableCards.card.def ? BattlePosition.FaceUpAttack : BattlePosition.FaceDownDefense);

        MonsterCard summoned = duel.Summon(aiPlayer, playableCards.card, pos, tributes);
        if (summoned != null)
        {
            aiHand.RemoveAt(playableCards.index);
            foreach (var t in tributes) aiGraveyard.Add(t.data);
        }
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
            .Where(c => c.cardType == CardType.Monster)
            .OrderByDescending(c => c.atk)
            .FirstOrDefault();

        if (bestMonster != null) gy.Remove(bestMonster);
        return bestMonster;
    }

    void HandleReturnMonsterToHand(Player owner, CardData data)
    {
        if (data == null) return;
        if (owner == humanPlayer) playerHand.Add(data);
        else aiHand.Add(data);
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

    void HandleMonsterDestroyed(MonsterCard destroyedCard)
    {
        PlayBattleSFX("Destroy");
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