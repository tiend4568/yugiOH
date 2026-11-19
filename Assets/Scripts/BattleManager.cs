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

        if (GameObject.Find("EndTurnButton") == null)
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
            btnObj.GetComponent<Button>().onClick.AddListener(EndPlayerTurn);

            GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            txtObj.transform.SetParent(btnObj.transform, false);
            RectTransform txtRT = txtObj.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;

            TextMeshProUGUI btnText = txtObj.GetComponent<TextMeshProUGUI>();
            btnText.text = "END TURN";
            btnText.fontSize = 20;
            btnText.fontStyle = FontStyles.Bold;
            btnText.alignment = TextAlignmentOptions.Center;
            btnText.color = Color.white;
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
                playerAcceptedTrap = true;
                waitingTrapDecision = false;
                trapPromptPanel.SetActive(false);
            });

            CreatePopupButton(trapPromptPanel.transform, "BỎ QUA", new Vector2(105f, -45f), new Color(0.32f, 0.32f, 0.38f, 1f), () =>
            {
                playerAcceptedTrap = false;
                waitingTrapDecision = false;
                trapPromptPanel.SetActive(false);
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
        InitializeBattle();
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
        if (currentState != TurnState.MainPhase || duel.Current != humanPlayer) return false;
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
        if (currentState != TurnState.MainPhase || duel.Current != humanPlayer) return false;
        if (handIndex < 0 || handIndex >= playerHand.Count) return false;

        CardData spellCard = playerHand[handIndex];
        if (!spellCard.IsSpellOrTrap) return false;

        int spellZone = FindEmptySpellZone(isPlayer: true, preferredSpellZone);
        if (spellZone < 0)
        {
            SetStatus("Hết ô bài Phép/Bẫy trên sân!");
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
            if (duel.Current == humanPlayer || currentState != TurnState.BattlePhase)
            {
                SetStatus("Mũ Tơ Ma Thuật chỉ kích hoạt trong Battle Phase của đối thủ!");
                return false;
            }
        }
        else if (spellCard.cardType == CardType.Spell)
        {
            if (currentState != TurnState.MainPhase || duel.Current != humanPlayer) return false;
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

            playerSpellFaceDown[spellZone] = false;
            PlayBattleSFX("Spell");
            OpenReviveGraveyardSelector(spellZone, spellCard);
            return true;
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
        if (currentState != TurnState.MainPhase || duel.Current != humanPlayer) return false;
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
        if (currentState != TurnState.MainPhase || duel.Current != humanPlayer) return false;
        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;

        return duel.ChangePosition(humanPlayer, humanPlayer.monsterZones[myZoneIndex]);
    }

    public void EnterBattlePhase()
    {
        if (isGraveyardSelectionOpen) return;
        if (currentState != TurnState.MainPhase || duel.Current != humanPlayer) return;
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

        currentState = TurnState.BattlePhase;
        SetStatus("Lượt của bạn - Battle Phase");
    }

    public bool PlayerAttackMonster(int myZoneIndex, int enemyZoneIndex)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState == TurnState.MainPhase) EnterBattlePhase();
        if (currentState != TurnState.BattlePhase || duel.Current != humanPlayer) return false;

        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;
        if (enemyZoneIndex < 0 || enemyZoneIndex >= Player.ZoneCount) return false;

        TryAITriggerMagicalHats();

        return duel.Attack(humanPlayer, humanPlayer.monsterZones[myZoneIndex], aiPlayer.monsterZones[enemyZoneIndex]);
    }

    public bool PlayerDirectAttack(int myZoneIndex)
    {
        if (isGraveyardSelectionOpen) return false;
        if (currentState == TurnState.MainPhase) EnterBattlePhase();
        if (currentState != TurnState.BattlePhase || duel.Current != humanPlayer) return false;

        if (myZoneIndex < 0 || myZoneIndex >= Player.ZoneCount) return false;

        return duel.DirectAttack(humanPlayer, humanPlayer.monsterZones[myZoneIndex]);
    }

    public void EndPlayerTurn()
    {
        if (isGraveyardSelectionOpen) return;
        if (currentState == TurnState.GameOver || duel.IsGameOver || duel.Current != humanPlayer) return;

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
                if (card.spellEffect == SpellEffectType.SwordsOfRevealingLight && aiPlayer.swordsBlockTurnsRemaining > 0)
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

            bool aiHasAttacker = aiPlayer.Monsters.Any(m => m.CanAttack);
            if (aiHasAttacker && CanActivateMagicalHats(isPlayer: true) && trapPromptPanel != null)
            {
                int trapZone = FindSetTrapZone(isPlayer: true, SpellEffectType.MagicalHats);
                if (trapZone >= 0)
                {
                    CardData trapCard = playerSpellZones[trapZone];
                    waitingTrapDecision = true;
                    playerAcceptedTrap = false;
                    trapPromptText.text = $"AI bước vào Battle Phase!\nBạn có muốn kích hoạt [{trapCard.cardName}] không?";
                    trapPromptPanel.SetActive(true);

                    yield return new WaitUntil(() => !waitingTrapDecision || currentState == TurnState.GameOver);

                    if (playerAcceptedTrap && currentState != TurnState.GameOver)
                    {
                        PlayerActivateSetSpell(trapZone);
                        yield return new WaitForSeconds(0.7f);
                    }
                }
            }

            foreach (var attacker in aiPlayer.Monsters.ToList())
            {
                if (duel.IsGameOver || aiPlayer.swordsBlockTurnsRemaining > 0) yield break;
                if (!attacker.CanAttack) continue;

                if (humanPlayer.MonsterCount == 0)
                {
                    duel.DirectAttack(aiPlayer, attacker);
                    yield return new WaitForSeconds(0.7f);
                }
                else
                {
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

                    if (bestTarget != null)
                    {
                        duel.Attack(aiPlayer, attacker, bestTarget);
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
            if (zones[i] != null && zones[i].spellEffect == SpellEffectType.SwordsOfRevealingLight)
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

        if (targetLP <= 0 && currentState != TurnState.GameOver)
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
        Time.timeScale = 1f;
        SceneManager.LoadScene("BattleScene");
    }

    public void ReturnToMainMenu()
    {
        Time.timeScale = 1f;
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
        if (playerDeckCountText != null) playerDeckCountText.text = playerDeck.Count.ToString();
        if (aiDeckCountText != null) aiDeckCountText.text = aiDeck.Count.ToString();
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