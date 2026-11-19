using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class BattleAutoBoard : MonoBehaviour
{
    public static BattleAutoBoard Instance;

    private Canvas boardCanvas;
    private RectTransform boardRoot;

    // 5 ô quái & 5 ô phép của AI và Player
    private ZoneSlotUI[] aiSpellSlots = new ZoneSlotUI[Player.ZoneCount];
    private ZoneSlotUI[] aiZoneSlots = new ZoneSlotUI[Player.ZoneCount];
    private ZoneSlotUI[] playerZoneSlots = new ZoneSlotUI[Player.ZoneCount];
    private ZoneSlotUI[] playerSpellSlots = new ZoneSlotUI[Player.ZoneCount];

    // Khu vực bài trên tay Player
    private RectTransform handContainer;
    private readonly List<GameObject> handCardObjects = new List<GameObject>();
    private int lastHandCount = -1;
    private int lastHandHash = 0;

    // Khung soi thông tin lá bài bên trái
    private Image inspectorImage;
    private TextMeshProUGUI inspectorNameText;
    private TextMeshProUGUI inspectorStatsText;
    private TextMeshProUGUI inspectorDescText;

    // Nút thao tác nhanh khi chọn quái trên sân
    private GameObject actionBarObj;
    private Button changePosButton;
    private Button directAttackButton;
    private Button cancelSelectButton;

    // Thông tin Mộ bài
    private TextMeshProUGUI playerGyText;
    private TextMeshProUGUI aiGyText;

    // Popup chọn bài từ Mộ (Hồi sinh / Hiệu ứng FLIP / Xem Mộ)
    private GameObject graveyardModal;
    private TextMeshProUGUI graveyardModalTitle;
    private RectTransform graveyardGridContent;
    private Button graveyardCloseButton;
    private Action<int> onGraveyardCardPicked;
    private Func<CardData, bool> graveyardCardFilter;

    // Trạng thái tương tác của người chơi
    private int selectedAttackerZone = -1;
    private bool isSelectingTributes = false;
    private int pendingSummonHandIndex = -1;
    private BattlePosition pendingSummonPosition = BattlePosition.FaceUpAttack;
    private int pendingPreferredZone = -1;
    private int requiredTributeCount = 0;
    private readonly List<int> chosenTributeZones = new List<int>();

    private Sprite cardBackSprite;
    private bool isInitialized = false;

    private class ZoneSlotUI
    {
        public GameObject root;
        public RectTransform slotRT;
        public Button button;
        public Image frameImage;
        public RectTransform cardVisualRT;
        public Image cardArtImage;
        public GameObject statBadgeBox;
        public TextMeshProUGUI statBadgeText;
        public GameObject stateBadgeBox;
        public TextMeshProUGUI stateBadgeText;
    }

    private class GraveyardGroupEntry
    {
        public CardData card;
        public int firstIndex;
        public int count;
    }

    void Awake()
    {
        Instance = this;
    }

    public void InitializeBoard()
    {
        if (isInitialized) return;
        isInitialized = true;

        cardBackSprite = Resources.Load<Sprite>("Cards/CardBack");
        if (cardBackSprite == null) cardBackSprite = Resources.Load<Sprite>("CardBack");
        if (cardBackSprite == null) cardBackSprite = CreateProceduralCardBack();

        BattleManager bm = BattleManager.Instance;
        boardCanvas = bm != null ? bm.GetBattleCanvas() : FindObjectOfType<Canvas>();
        if (boardCanvas == null)
        {
            GameObject cObj = new GameObject("BattleCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            boardCanvas = cObj.GetComponent<Canvas>();
            boardCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            boardCanvas.sortingOrder = 10;
            CanvasScaler scaler = cObj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
        }

        GameObject rootObj = new GameObject("BattleAutoBoardRoot", typeof(RectTransform));
        rootObj.transform.SetParent(boardCanvas.transform, false);
        boardRoot = rootObj.GetComponent<RectTransform>();
        boardRoot.anchorMin = Vector2.zero;
        boardRoot.anchorMax = Vector2.one;
        boardRoot.offsetMin = Vector2.zero;
        boardRoot.offsetMax = Vector2.zero;

        BuildFieldZones();
        BuildHandArea();
        BuildCardInspector();
        BuildFieldActionBar();
        BuildGraveyardHUD();
        BuildGraveyardModal();
    }

    void Update()
    {
        if (!isInitialized) return;
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel == null) return;

        RefreshFieldZones(bm);
        RefreshHandIfChanged(bm);
        RefreshActionBar(bm);
        RefreshGraveyardCounters(bm);
    }

    void BuildFieldZones()
    {
        float monsterW = 116f;
        float monsterH = 154f;
        float spellW = 112f;
        float spellH = 148f;
        float spacingX = 142f;

        // Hàng 1 (Trên cùng): 5 ô Bài Phép/Bẫy của AI
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            int zoneIdx = i;
            float x = (i - 2) * spacingX;
            aiSpellSlots[i] = CreateZoneSlot($"AISpellZone_{i}", new Vector2(x, 330f), new Vector2(spellW, spellH), new Color(0.14f, 0.44f, 0.36f, 0.45f));

            AddHoverTrigger(aiSpellSlots[i].root, () =>
            {
                BattleManager bm = BattleManager.Instance;
                if (bm == null) return;
                CardData sp = bm.aiSpellZones[zoneIdx];
                if (sp != null && !bm.aiSpellFaceDown[zoneIdx])
                {
                    ShowInspectorCard(sp, null);
                }
            });
        }

        // Hàng 2: 5 ô Quái Thú của AI
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            int zoneIdx = i;
            float x = (i - 2) * spacingX;
            aiZoneSlots[i] = CreateZoneSlot($"AIZone_{i}", new Vector2(x, 165f), new Vector2(monsterW, monsterH), new Color(0.55f, 0.18f, 0.18f, 0.48f));
            aiZoneSlots[i].button.onClick.AddListener(() => OnClickAIZone(zoneIdx));

            AddHoverTrigger(aiZoneSlots[i].root, () =>
            {
                BattleManager bm = BattleManager.Instance;
                if (bm == null || bm.aiPlayer == null) return;
                MonsterCard mc = bm.aiPlayer.monsterZones[zoneIdx];
                if (mc != null && !mc.IsFaceDown)
                {
                    ShowInspectorCard(mc.data, mc);
                }
            });
        }

        // Hàng 3: 5 ô Quái Thú của Player
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            int zoneIdx = i;
            float x = (i - 2) * spacingX;
            playerZoneSlots[i] = CreateZoneSlot($"PlayerZone_{i}", new Vector2(x, -10f), new Vector2(monsterW, monsterH), new Color(0.18f, 0.42f, 0.65f, 0.48f));
            playerZoneSlots[i].button.onClick.AddListener(() => OnClickPlayerZone(zoneIdx));

            AddRightClickTrigger(playerZoneSlots[i].root, () => OnRightClickPlayerZone(zoneIdx));
            AddHoverTrigger(playerZoneSlots[i].root, () =>
            {
                BattleManager bm = BattleManager.Instance;
                if (bm == null || bm.humanPlayer == null) return;
                MonsterCard mc = bm.humanPlayer.monsterZones[zoneIdx];
                if (mc != null)
                {
                    ShowInspectorCard(mc.data, mc);
                }
            });
        }

        // Hàng 4 (Dưới): 5 ô Bài Phép/Bẫy của Player
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            int zoneIdx = i;
            float x = (i - 2) * spacingX;
            playerSpellSlots[i] = CreateZoneSlot($"PlayerSpellZone_{i}", new Vector2(x, -175f), new Vector2(spellW, spellH), new Color(0.14f, 0.48f, 0.38f, 0.48f));
            playerSpellSlots[i].button.onClick.AddListener(() => OnClickPlayerSpellZone(zoneIdx));

            AddHoverTrigger(playerSpellSlots[i].root, () =>
            {
                BattleManager bm = BattleManager.Instance;
                if (bm == null) return;
                CardData sp = bm.playerSpellZones[zoneIdx];
                if (sp != null)
                {
                    ShowInspectorCard(sp, null);
                }
            });
        }
    }

    ZoneSlotUI CreateZoneSlot(string objName, Vector2 anchoredPos, Vector2 size, Color frameColor)
    {
        GameObject slotObj = new GameObject(objName, typeof(RectTransform), typeof(Image), typeof(Button));
        slotObj.transform.SetParent(boardRoot, false);
        RectTransform rt = slotObj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        Image frameImg = slotObj.GetComponent<Image>();
        frameImg.color = frameColor;

        GameObject visObj = new GameObject("CardVisual", typeof(RectTransform), typeof(Image));
        visObj.transform.SetParent(slotObj.transform, false);
        RectTransform visRT = visObj.GetComponent<RectTransform>();
        visRT.anchorMin = new Vector2(0.5f, 0.5f);
        visRT.anchorMax = new Vector2(0.5f, 0.5f);
        visRT.sizeDelta = new Vector2(size.x - 14f, size.y - 24f);

        Image artImg = visObj.GetComponent<Image>();
        artImg.preserveAspect = true;
        artImg.raycastTarget = false;
        artImg.enabled = false;

        // Nhãn chỉ số dưới lá bài: chữ trắng + khung chữ nhật nhỏ màu đen bán trong suốt
        TextMeshProUGUI statTxt = CreateBoxedLabel(
            slotObj.transform,
            "StatBadge",
            anchorMin: new Vector2(0.5f, 0f),
            anchorMax: new Vector2(0.5f, 0f),
            pivot: new Vector2(0.5f, 0f),
            anchoredPos: new Vector2(0f, 2f),
            size: new Vector2(size.x + 8f, 21f),
            fontSize: 12f,
            style: FontStyles.Bold,
            textColor: Color.white,
            align: TextAlignmentOptions.Center,
            boxColor: new Color(0f, 0f, 0f, 0.74f),
            out GameObject statBox
        );

        // Nhãn trạng thái trên lá bài: chữ trắng + khung chữ nhật nhỏ màu đen bán trong suốt
        TextMeshProUGUI stateTxt = CreateBoxedLabel(
            slotObj.transform,
            "StateBadge",
            anchorMin: new Vector2(0.5f, 1f),
            anchorMax: new Vector2(0.5f, 1f),
            pivot: new Vector2(0.5f, 1f),
            anchoredPos: new Vector2(0f, -2f),
            size: new Vector2(size.x + 8f, 20f),
            fontSize: 11f,
            style: FontStyles.Bold,
            textColor: Color.white,
            align: TextAlignmentOptions.Center,
            boxColor: new Color(0f, 0f, 0f, 0.74f),
            out GameObject stateBox
        );

        return new ZoneSlotUI
        {
            root = slotObj,
            slotRT = rt,
            button = slotObj.GetComponent<Button>(),
            frameImage = frameImg,
            cardVisualRT = visRT,
            cardArtImage = artImg,
            statBadgeBox = statBox,
            statBadgeText = statTxt,
            stateBadgeBox = stateBox,
            stateBadgeText = stateTxt
        };
    }

    void BuildHandArea()
    {
        GameObject handObj = new GameObject("PlayerHandContainer", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        handObj.transform.SetParent(boardRoot, false);
        handContainer = handObj.GetComponent<RectTransform>();
        handContainer.anchorMin = new Vector2(0.5f, 0f);
        handContainer.anchorMax = new Vector2(0.5f, 0f);
        handContainer.pivot = new Vector2(0.5f, 0f);
        handContainer.anchoredPosition = new Vector2(0f, 10f);
        handContainer.sizeDelta = new Vector2(1080f, 162f);

        HorizontalLayoutGroup layout = handObj.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childAlignment = TextAnchor.LowerCenter;
        layout.childControlWidth = false;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
    }

    void BuildCardInspector()
    {
        GameObject panel = new GameObject("CardInspectorPanel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(boardRoot, false);
        RectTransform rt = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0.5f);
        rt.anchorMax = new Vector2(0f, 0.5f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(18f, 10f);
        rt.sizeDelta = new Vector2(275f, 520f);
        panel.GetComponent<Image>().color = new Color(0.06f, 0.08f, 0.14f, 0.88f);

        GameObject imgObj = new GameObject("PreviewArt", typeof(RectTransform), typeof(Image));
        imgObj.transform.SetParent(panel.transform, false);
        RectTransform imgRT = imgObj.GetComponent<RectTransform>();
        imgRT.anchorMin = new Vector2(0.5f, 1f);
        imgRT.anchorMax = new Vector2(0.5f, 1f);
        imgRT.pivot = new Vector2(0.5f, 1f);
        imgRT.anchoredPosition = new Vector2(0f, -14f);
        imgRT.sizeDelta = new Vector2(180f, 255f);
        inspectorImage = imgObj.GetComponent<Image>();
        inspectorImage.preserveAspect = true;
        inspectorImage.color = new Color(1f, 1f, 1f, 0f);

        inspectorNameText = CreateBoxedLabel(
            panel.transform,
            "NameText",
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -276f),
            new Vector2(252f, 32f),
            17f,
            FontStyles.Bold,
            Color.white,
            TextAlignmentOptions.Center,
            new Color(0f, 0f, 0f, 0.72f),
            out _
        );

        inspectorStatsText = CreateLabel(panel.transform, "StatsText", new Vector2(0f, -312f), new Vector2(252f, 28f), 15, FontStyles.Bold, new Color(0.75f, 0.92f, 1f, 1f), TextAlignmentOptions.Center);
        inspectorDescText = CreateLabel(panel.transform, "DescText", new Vector2(0f, -345f), new Vector2(248f, 160f), 14, FontStyles.Normal, Color.white, TextAlignmentOptions.TopLeft);
        inspectorDescText.enableWordWrapping = true;
    }

    void BuildFieldActionBar()
    {
        actionBarObj = new GameObject("FieldActionBar", typeof(RectTransform));
        actionBarObj.transform.SetParent(boardRoot, false);
        RectTransform rt = actionBarObj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(0f, -305f);
        rt.sizeDelta = new Vector2(540f, 40f);

        changePosButton = CreateActionButton(actionBarObj.transform, "ĐỔI THẾ (CÔNG/THỦ)", new Vector2(-175f, 0f), new Vector2(185f, 36f), new Color(0.2f, 0.5f, 0.8f, 0.95f), () =>
        {
            if (selectedAttackerZone >= 0 && BattleManager.Instance != null)
            {
                BattleManager.Instance.PlayerChangePosition(selectedAttackerZone);
                ClearSelectionState();
            }
        });

        directAttackButton = CreateActionButton(actionBarObj.transform, "ĐÁNH TRỰC TIẾP", new Vector2(15f, 0f), new Vector2(170f, 36f), new Color(0.85f, 0.25f, 0.2f, 0.95f), () =>
        {
            if (selectedAttackerZone >= 0 && BattleManager.Instance != null)
            {
                BattleManager.Instance.PlayerDirectAttack(selectedAttackerZone);
                ClearSelectionState();
            }
        });

        cancelSelectButton = CreateActionButton(actionBarObj.transform, "HỦY CHỌN", new Vector2(180f, 0f), new Vector2(130f, 36f), new Color(0.35f, 0.35f, 0.4f, 0.95f), ClearSelectionState);

        actionBarObj.SetActive(false);
    }

    void BuildGraveyardHUD()
    {
        Button pGyBtn = CreateActionButton(boardRoot, "MỘ BÀI (0)", new Vector2(455f, -92f), new Vector2(135f, 50f), new Color(0.22f, 0.2f, 0.32f, 0.92f), () =>
        {
            BattleManager bm = BattleManager.Instance;
            if (bm != null) OpenGraveyardViewOnly("MỘ BÀI CỦA BẠN", bm.playerGraveyard);
        });
        playerGyText = pGyBtn.GetComponentInChildren<TextMeshProUGUI>();

        Button aGyBtn = CreateActionButton(boardRoot, "MỘ AI (0)", new Vector2(455f, 248f), new Vector2(135f, 50f), new Color(0.32f, 0.18f, 0.22f, 0.92f), () =>
        {
            BattleManager bm = BattleManager.Instance;
            if (bm != null) OpenGraveyardViewOnly("MỘ BÀI ĐỐI PHƯƠNG", bm.aiGraveyard);
        });
        aiGyText = aGyBtn.GetComponentInChildren<TextMeshProUGUI>();
    }

    void BuildGraveyardModal()
    {
        graveyardModal = new GameObject("GraveyardModal", typeof(RectTransform), typeof(Image));
        graveyardModal.transform.SetParent(boardCanvas.transform, false);
        RectTransform mRT = graveyardModal.GetComponent<RectTransform>();
        mRT.anchorMin = new Vector2(0.5f, 0.5f);
        mRT.anchorMax = new Vector2(0.5f, 0.5f);
        mRT.sizeDelta = new Vector2(780f, 460f);
        graveyardModal.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.12f, 0.96f);

        graveyardModalTitle = CreateBoxedLabel(
            graveyardModal.transform,
            "Title",
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -14f),
            new Vector2(720f, 40f),
            20f,
            FontStyles.Bold,
            Color.white,
            TextAlignmentOptions.Center,
            new Color(0f, 0f, 0f, 0.78f),
            out _
        );

        GameObject gridObj = new GameObject("Grid", typeof(RectTransform), typeof(GridLayoutGroup));
        gridObj.transform.SetParent(graveyardModal.transform, false);
        graveyardGridContent = gridObj.GetComponent<RectTransform>();
        graveyardGridContent.anchorMin = new Vector2(0.5f, 0.5f);
        graveyardGridContent.anchorMax = new Vector2(0.5f, 0.5f);
        graveyardGridContent.anchoredPosition = new Vector2(0f, -8f);
        graveyardGridContent.sizeDelta = new Vector2(730f, 320f);

        GridLayoutGroup grid = gridObj.GetComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(112f, 156f);
        grid.spacing = new Vector2(14f, 14f);
        grid.childAlignment = TextAnchor.UpperCenter;

        graveyardCloseButton = CreateActionButton(graveyardModal.transform, "ĐÓNG", new Vector2(0f, -196f), new Vector2(150f, 42f), new Color(0.7f, 0.22f, 0.22f, 1f), () =>
        {
            graveyardModal.SetActive(false);
        });

        graveyardModal.SetActive(false);
    }

    // ==================== CÁC HÀM CÔNG KHAI CHO BattleCardDragUI ====================

    public void ShowCardPreview(CardData data)
    {
        ShowInspectorCard(data, null);
    }

    public int GetZoneIndexAtScreenPoint(Vector2 screenPos, Camera eventCamera)
    {
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (playerZoneSlots[i] != null && playerZoneSlots[i].slotRT != null &&
                RectTransformUtility.RectangleContainsScreenPoint(playerZoneSlots[i].slotRT, screenPos, eventCamera))
            {
                return i;
            }
            if (playerSpellSlots[i] != null && playerSpellSlots[i].slotRT != null &&
                RectTransformUtility.RectangleContainsScreenPoint(playerSpellSlots[i].slotRT, screenPos, eventCamera))
            {
                return i;
            }
        }
        return -1;
    }

    public void RequestSummonFromHand(int handIndex, BattlePosition pos, int preferredZone = -1)
    {
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel == null || bm.duel.Current != bm.humanPlayer) return;
        if (bm.currentState != BattleManager.TurnState.MainPhase) return;
        if (handIndex < 0 || handIndex >= bm.playerHand.Count) return;

        ClearSelectionState();
        CardData card = bm.playerHand[handIndex];
        ShowInspectorCard(card, null);

        if (card.IsSpellOrTrap)
        {
            if (pos == BattlePosition.FaceDownDefense)
                bm.PlayerSetSpellFromHand(handIndex, preferredZone);
            else
                bm.PlayerActivateSpellFromHand(handIndex, preferredZone);

            ForceRefreshHand();
            return;
        }

        BeginSummonFlow(handIndex, pos, preferredZone);
    }

    // ================================================================================

    public void OpenGraveyardSelection(List<CardData> graveyard, Action<int> onPicked, string customTitle = null, Func<CardData, bool> filter = null)
    {
        if (graveyardModal == null) return;
        onGraveyardCardPicked = onPicked;
        graveyardCardFilter = filter;
        graveyardModalTitle.text = string.IsNullOrEmpty(customTitle)
            ? "CHỌN 1 LÁ BÀI TRONG MỘ ĐỂ XÁO TRỘN VÀO DECK"
            : customTitle;
        if (graveyardCloseButton != null) graveyardCloseButton.gameObject.SetActive(false);

        PopulateGraveyardGrid(graveyard, selectable: true);
        graveyardModal.transform.SetAsLastSibling();
        graveyardModal.SetActive(true);
    }

    void OpenGraveyardViewOnly(string title, List<CardData> graveyard)
    {
        if (graveyardModal == null) return;
        onGraveyardCardPicked = null;
        graveyardCardFilter = null;
        graveyardModalTitle.text = $"{title} ({graveyard.Count} lá)";
        if (graveyardCloseButton != null) graveyardCloseButton.gameObject.SetActive(true);

        PopulateGraveyardGrid(graveyard, selectable: false);
        graveyardModal.transform.SetAsLastSibling();
        graveyardModal.SetActive(true);
    }

    void PopulateGraveyardGrid(List<CardData> cards, bool selectable)
    {
        for (int i = graveyardGridContent.childCount - 1; i >= 0; i--)
        {
            Destroy(graveyardGridContent.GetChild(i).gameObject);
        }

        // Gộp các lá bài trùng tên trong Mộ thành 1 ô kèm số lượng x1, x2, x3
        List<GraveyardGroupEntry> grouped = new List<GraveyardGroupEntry>();
        for (int i = 0; i < cards.Count; i++)
        {
            CardData c = cards[i];
            if (c == null) continue;
            if (graveyardCardFilter != null && !graveyardCardFilter(c)) continue;

            GraveyardGroupEntry existing = grouped.Find(g => g.card.cardName.Equals(c.cardName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.count++;
            }
            else
            {
                grouped.Add(new GraveyardGroupEntry
                {
                    card = c,
                    firstIndex = i,
                    count = 1
                });
            }
        }

        foreach (var entry in grouped)
        {
            int idx = entry.firstIndex;
            CardData card = entry.card;

            GameObject item = new GameObject($"GYCard_{card.cardName}", typeof(RectTransform), typeof(Image), typeof(Button));
            item.transform.SetParent(graveyardGridContent, false);

            Image img = item.GetComponent<Image>();
            img.sprite = card.cardSprite;
            img.preserveAspect = true;
            img.color = card.cardSprite != null ? Color.white : Color.gray;

            // Huy hiệu số lượng x1, x2, x3 (chữ trắng + khung chữ nhật đen bán trong suốt)
            TextMeshProUGUI countLbl = CreateBoxedLabel(
                item.transform,
                "CountBadge",
                anchorMin: new Vector2(1f, 1f),
                anchorMax: new Vector2(1f, 1f),
                pivot: new Vector2(1f, 1f),
                anchoredPos: new Vector2(-3f, -3f),
                size: new Vector2(40f, 24f),
                fontSize: 14f,
                style: FontStyles.Bold,
                textColor: Color.white,
                align: TextAlignmentOptions.Center,
                boxColor: new Color(0f, 0f, 0f, 0.78f),
                out _
            );
            countLbl.text = $"x{entry.count}";

            // Nhãn tên + chỉ số ATK/DEF phía dưới (chữ trắng + khung chữ nhật đen bán trong suốt)
            TextMeshProUGUI lbl = CreateBoxedLabel(
                item.transform,
                "Label",
                anchorMin: new Vector2(0.5f, 0f),
                anchorMax: new Vector2(0.5f, 0f),
                pivot: new Vector2(0.5f, 0f),
                anchoredPos: new Vector2(0f, 2f),
                size: new Vector2(108f, 34f),
                fontSize: 11f,
                style: FontStyles.Bold,
                textColor: Color.white,
                align: TextAlignmentOptions.Center,
                boxColor: new Color(0f, 0f, 0f, 0.78f),
                out _
            );
            lbl.enableWordWrapping = true;
            lbl.text = card.cardType == CardType.Monster
                ? $"{card.cardName}\n{card.atk}/{card.def}"
                : card.cardName;

            Button btn = item.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                ShowInspectorCard(card, null);
                if (selectable && onGraveyardCardPicked != null)
                {
                    Action<int> cb = onGraveyardCardPicked;
                    onGraveyardCardPicked = null;
                    graveyardCardFilter = null;
                    graveyardModal.SetActive(false);
                    cb.Invoke(idx);
                }
            });
        }
    }

    void SetSlotBadges(ZoneSlotUI slot, string topState, string bottomStat)
    {
        if (slot.stateBadgeText != null)
        {
            slot.stateBadgeText.color = Color.white;
            slot.stateBadgeText.text = topState;
        }
        if (slot.stateBadgeBox != null)
        {
            slot.stateBadgeBox.SetActive(!string.IsNullOrEmpty(topState));
        }

        if (slot.statBadgeText != null)
        {
            slot.statBadgeText.color = Color.white;
            slot.statBadgeText.text = bottomStat;
        }
        if (slot.statBadgeBox != null)
        {
            slot.statBadgeBox.SetActive(!string.IsNullOrEmpty(bottomStat));
        }
    }

    void RefreshFieldZones(BattleManager bm)
    {
        // 1. 5 ô Quái Thú của Player
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            MonsterCard mc = bm.humanPlayer.monsterZones[i];
            ZoneSlotUI slot = playerZoneSlots[i];

            if (mc == null)
            {
                slot.cardArtImage.enabled = false;
                SetSlotBadges(slot, "", "");
                slot.frameImage.color = new Color(0.18f, 0.42f, 0.65f, 0.45f);
            }
            else
            {
                slot.cardArtImage.enabled = true;
                slot.cardArtImage.sprite = mc.data.cardSprite != null ? mc.data.cardSprite : cardBackSprite;
                slot.cardArtImage.color = mc.IsFaceDown ? new Color(0.65f, 0.72f, 0.9f, 0.95f) : Color.white;
                slot.cardVisualRT.localEulerAngles = mc.IsDefense ? new Vector3(0f, 0f, 90f) : Vector3.zero;

                string statStr = $"{mc.Atk}/{mc.Def}";
                string stateStr;

                if (isSelectingTributes && chosenTributeZones.Contains(i))
                {
                    slot.frameImage.color = new Color(0.95f, 0.55f, 0.1f, 0.9f);
                    stateStr = "[HIẾN TẾ]";
                }
                else if (selectedAttackerZone == i)
                {
                    slot.frameImage.color = new Color(0.2f, 0.9f, 0.35f, 0.85f);
                    stateStr = "[ĐANG CHỌN]";
                }
                else
                {
                    slot.frameImage.color = new Color(0.2f, 0.5f, 0.8f, 0.68f);
                    if (mc.IsFaceDown) stateStr = "[ÚP THỦ]";
                    else if (mc.hasAttacked) stateStr = "[ĐÃ ĐÁNH]";
                    else stateStr = mc.IsDefense ? "[THỦ]" : "[CÔNG]";
                }

                SetSlotBadges(slot, stateStr, statStr);
            }
        }

        // 2. 5 ô Quái Thú của AI
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            MonsterCard mc = bm.aiPlayer.monsterZones[i];
            ZoneSlotUI slot = aiZoneSlots[i];

            if (mc == null)
            {
                slot.cardArtImage.enabled = false;
                SetSlotBadges(slot, "", "");
                slot.frameImage.color = (selectedAttackerZone >= 0 && bm.aiPlayer.MonsterCount == 0)
                    ? new Color(0.9f, 0.25f, 0.25f, 0.55f)
                    : new Color(0.55f, 0.18f, 0.18f, 0.45f);
            }
            else
            {
                slot.cardArtImage.enabled = true;
                slot.cardVisualRT.localEulerAngles = mc.IsDefense ? new Vector3(0f, 0f, 90f) : Vector3.zero;

                if (mc.IsFaceDown)
                {
                    slot.cardArtImage.sprite = cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                    SetSlotBadges(slot, "[ÚP]", "? / ?");
                }
                else
                {
                    slot.cardArtImage.sprite = mc.data.cardSprite != null ? mc.data.cardSprite : cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                    SetSlotBadges(slot, mc.IsDefense ? "[THỦ]" : "[CÔNG]", $"{mc.Atk}/{mc.Def}");
                }

                slot.frameImage.color = selectedAttackerZone >= 0
                    ? new Color(0.95f, 0.25f, 0.2f, 0.85f)
                    : new Color(0.65f, 0.22f, 0.22f, 0.68f);
            }
        }

        // 3. 5 ô Bài Phép/Bẫy của Player
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            CardData sp = bm.playerSpellZones[i];
            ZoneSlotUI slot = playerSpellSlots[i];

            if (sp == null)
            {
                slot.cardArtImage.enabled = false;
                SetSlotBadges(slot, "PHÉP/BẪY", "");
                slot.frameImage.color = new Color(0.14f, 0.48f, 0.38f, 0.45f);
            }
            else
            {
                slot.cardArtImage.enabled = true;
                slot.cardArtImage.sprite = sp.cardSprite != null ? sp.cardSprite : cardBackSprite;

                if (bm.playerSpellFaceDown[i])
                {
                    slot.cardArtImage.color = new Color(0.65f, 0.78f, 0.9f, 0.95f);
                    SetSlotBadges(slot, "[ÚP - BẤM DÙNG]", sp.cardName);
                    slot.frameImage.color = new Color(0.18f, 0.62f, 0.48f, 0.78f);
                }
                else
                {
                    slot.cardArtImage.color = Color.white;
                    SetSlotBadges(slot, "[KÍCH HOẠT]", sp.cardName);
                    slot.frameImage.color = new Color(0.2f, 0.85f, 0.65f, 0.9f);
                }
            }
        }

        // 4. 5 ô Bài Phép/Bẫy của AI
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            CardData sp = bm.aiSpellZones[i];
            ZoneSlotUI slot = aiSpellSlots[i];

            if (sp == null)
            {
                slot.cardArtImage.enabled = false;
                SetSlotBadges(slot, "PHÉP/BẪY", "");
                slot.frameImage.color = new Color(0.14f, 0.44f, 0.36f, 0.45f);
            }
            else
            {
                slot.cardArtImage.enabled = true;
                if (bm.aiSpellFaceDown[i])
                {
                    slot.cardArtImage.sprite = cardBackSprite;
                    SetSlotBadges(slot, "[ÚP]", "");
                }
                else
                {
                    slot.cardArtImage.sprite = sp.cardSprite != null ? sp.cardSprite : cardBackSprite;
                    SetSlotBadges(slot, "[KÍCH HOẠT]", sp.cardName);
                    slot.frameImage.color = new Color(0.2f, 0.85f, 0.65f, 0.9f);
                }
            }
        }
    }

    public void ForceRefreshHand()
    {
        lastHandCount = -1;
        BattleManager bm = BattleManager.Instance;
        if (bm != null) RefreshHandIfChanged(bm);
    }

    void RefreshHandIfChanged(BattleManager bm)
    {
        int hash = 17;
        for (int i = 0; i < bm.playerHand.Count; i++)
        {
            hash = hash * 31 + bm.playerHand[i].cardName.GetHashCode();
        }

        if (bm.playerHand.Count == lastHandCount && hash == lastHandHash) return;

        lastHandCount = bm.playerHand.Count;
        lastHandHash = hash;

        for (int i = 0; i < handCardObjects.Count; i++)
        {
            if (handCardObjects[i] != null) Destroy(handCardObjects[i]);
        }
        handCardObjects.Clear();

        for (int i = 0; i < bm.playerHand.Count; i++)
        {
            int handIdx = i;
            CardData card = bm.playerHand[i];

            GameObject cardObj = new GameObject($"HandCard_{i}_{card.cardName}", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            cardObj.transform.SetParent(handContainer, false);
            RectTransform rt = cardObj.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(110f, 156f);

            Image img = cardObj.GetComponent<Image>();
            img.sprite = card.cardSprite != null ? card.cardSprite : cardBackSprite;
            img.preserveAspect = true;
            img.color = Color.white;

            // Nhãn chỉ số dưới lá bài trên tay: chữ trắng + khung chữ nhật đen bán trong suốt
            TextMeshProUGUI badge = CreateBoxedLabel(
                cardObj.transform,
                "Stat",
                anchorMin: new Vector2(0.5f, 0f),
                anchorMax: new Vector2(0.5f, 0f),
                pivot: new Vector2(0.5f, 0f),
                anchoredPos: new Vector2(0f, 2f),
                size: new Vector2(106f, 20f),
                fontSize: 11f,
                style: FontStyles.Bold,
                textColor: Color.white,
                align: TextAlignmentOptions.Center,
                boxColor: new Color(0f, 0f, 0f, 0.76f),
                out _
            );
            badge.text = card.cardType == CardType.Monster
                ? $"LV{card.level} {card.atk}/{card.def}"
                : (card.cardType == CardType.Trap ? "BÀI BẪY" : "BÀI PHÉP");

            BattleCardDragUI dragUI = cardObj.AddComponent<BattleCardDragUI>();
            dragUI.Setup(handIdx, card);

            handCardObjects.Add(cardObj);
        }
    }

    void RefreshActionBar(BattleManager bm)
    {
        if (actionBarObj == null) return;

        bool isPlayerTurn = bm.duel.Current == bm.humanPlayer && bm.currentState != BattleManager.TurnState.GameOver;

        if (isSelectingTributes)
        {
            actionBarObj.SetActive(true);
            changePosButton.gameObject.SetActive(false);
            directAttackButton.gameObject.SetActive(false);
            cancelSelectButton.gameObject.SetActive(true);
            return;
        }

        if (!isPlayerTurn || selectedAttackerZone < 0 || selectedAttackerZone >= Player.ZoneCount || bm.humanPlayer.monsterZones[selectedAttackerZone] == null)
        {
            selectedAttackerZone = -1;
            actionBarObj.SetActive(false);
            return;
        }

        MonsterCard selected = bm.humanPlayer.monsterZones[selectedAttackerZone];
        actionBarObj.SetActive(true);

        changePosButton.gameObject.SetActive(selected.CanChangePosition && bm.currentState == BattleManager.TurnState.MainPhase);
        directAttackButton.gameObject.SetActive(selected.CanAttack && bm.duel.turnCount > 1 && bm.aiPlayer.MonsterCount == 0);
        cancelSelectButton.gameObject.SetActive(true);
    }

    void RefreshGraveyardCounters(BattleManager bm)
    {
        if (playerGyText != null) playerGyText.text = $"MỘ BÀI ({bm.playerGraveyard.Count})";
        if (aiGyText != null) aiGyText.text = $"MỘ AI ({bm.aiGraveyard.Count})";
    }

    void BeginSummonFlow(int handIndex, BattlePosition pos, int preferredZone = -1)
    {
        BattleManager bm = BattleManager.Instance;
        CardData card = bm.playerHand[handIndex];
        int needTributes = Duel.RequiredTributes(card.level);

        if (needTributes == 0)
        {
            if (bm.PlayerSummonFromHand(handIndex, pos, null, preferredZone))
            {
                ForceRefreshHand();
            }
            return;
        }

        if (bm.humanPlayer.MonsterCount < needTributes)
        {
            bm.SetStatus($"Cần {needTributes} quái thú trên sân để hiến tế triệu hồi {card.cardName}!");
            return;
        }

        isSelectingTributes = true;
        pendingSummonHandIndex = handIndex;
        pendingSummonPosition = pos;
        pendingPreferredZone = preferredZone;
        requiredTributeCount = needTributes;
        chosenTributeZones.Clear();
        bm.SetStatus($"Chọn {needTributes} quái thú của bạn trên sân để hiến tế cho {card.cardName}!");
    }

    void OnClickPlayerZone(int zoneIndex)
    {
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel.Current != bm.humanPlayer) return;

        MonsterCard mc = bm.humanPlayer.monsterZones[zoneIndex];

        if (isSelectingTributes)
        {
            if (mc == null) return;

            if (chosenTributeZones.Contains(zoneIndex))
            {
                chosenTributeZones.Remove(zoneIndex);
            }
            else
            {
                chosenTributeZones.Add(zoneIndex);
            }

            if (chosenTributeZones.Count >= requiredTributeCount)
            {
                int handIdx = pendingSummonHandIndex;
                BattlePosition pos = pendingSummonPosition;
                int prefZone = pendingPreferredZone;
                List<int> tributes = new List<int>(chosenTributeZones);
                ClearSelectionState();
                if (bm.PlayerSummonFromHand(handIdx, pos, tributes, prefZone))
                {
                    ForceRefreshHand();
                }
            }
            else
            {
                bm.SetStatus($"Đã chọn {chosenTributeZones.Count}/{requiredTributeCount} quái hiến tế...");
            }
            return;
        }

        if (mc == null)
        {
            ClearSelectionState();
            return;
        }

        ShowInspectorCard(mc.data, mc);
        selectedAttackerZone = (selectedAttackerZone == zoneIndex) ? -1 : zoneIndex;

        if (selectedAttackerZone >= 0)
        {
            if (mc.CanAttack && bm.duel.turnCount > 1)
            {
                bm.SetStatus(bm.aiPlayer.MonsterCount > 0
                    ? $"Đã chọn {mc.data.cardName}: Nhấn vào quái đối phương để tấn công!"
                    : $"Đã chọn {mc.data.cardName}: Nhấn 'ĐÁNH TRỰC TIẾP' hoặc nhấn vào sân đối phương!");
            }
            else if (mc.CanChangePosition && bm.currentState == BattleManager.TurnState.MainPhase)
            {
                bm.SetStatus($"Đã chọn {mc.data.cardName}: Nhấn 'ĐỔI THẾ' hoặc chuột phải để lật/đổi thế!");
            }
        }
    }

    void OnClickPlayerSpellZone(int spellZoneIndex)
    {
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel.Current != bm.humanPlayer) return;

        CardData sp = bm.playerSpellZones[spellZoneIndex];
        if (sp == null) return;

        ShowInspectorCard(sp, null);
        if (bm.playerSpellFaceDown[spellZoneIndex])
        {
            bm.PlayerActivateSetSpell(spellZoneIndex);
        }
    }

    void OnRightClickPlayerZone(int zoneIndex)
    {
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel.Current != bm.humanPlayer) return;
        if (isSelectingTributes) return;

        if (bm.humanPlayer.monsterZones[zoneIndex] != null)
        {
            bm.PlayerChangePosition(zoneIndex);
            ClearSelectionState();
        }
    }

    void OnClickAIZone(int aiZoneIndex)
    {
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel.Current != bm.humanPlayer) return;
        if (selectedAttackerZone < 0) return;

        if (bm.aiPlayer.MonsterCount == 0)
        {
            bm.PlayerDirectAttack(selectedAttackerZone);
            ClearSelectionState();
            return;
        }

        if (bm.aiPlayer.monsterZones[aiZoneIndex] != null)
        {
            bm.PlayerAttackMonster(selectedAttackerZone, aiZoneIndex);
            ClearSelectionState();
        }
    }

    void ClearSelectionState()
    {
        selectedAttackerZone = -1;
        isSelectingTributes = false;
        pendingSummonHandIndex = -1;
        pendingPreferredZone = -1;
        requiredTributeCount = 0;
        chosenTributeZones.Clear();
    }

    void ShowInspectorCard(CardData data, MonsterCard fieldInstance)
    {
        if (data == null || inspectorImage == null) return;

        inspectorImage.sprite = data.cardSprite != null ? data.cardSprite : cardBackSprite;
        inspectorImage.color = Color.white;
        inspectorNameText.text = data.cardName;

        if (data.cardType == CardType.Monster)
        {
            int atk = fieldInstance != null ? fieldInstance.Atk : data.atk;
            int def = fieldInstance != null ? fieldInstance.Def : data.def;
            inspectorStatsText.text = $"[{data.CardTag}] LV{data.level} | {atk}/{def}";
        }
        else
        {
            inspectorStatsText.text = $"[{data.CardTag}]";
        }

        inspectorDescText.text = data.description;
    }

    TextMeshProUGUI CreateBoxedLabel(
        Transform parent,
        string name,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPos,
        Vector2 size,
        float fontSize,
        FontStyles style,
        Color textColor,
        TextAlignmentOptions align,
        Color boxColor,
        out GameObject boxObj)
    {
        boxObj = new GameObject(name + "_Box", typeof(RectTransform), typeof(Image));
        boxObj.transform.SetParent(parent, false);
        RectTransform boxRT = boxObj.GetComponent<RectTransform>();
        boxRT.anchorMin = anchorMin;
        boxRT.anchorMax = anchorMax;
        boxRT.pivot = pivot;
        boxRT.anchoredPosition = anchoredPos;
        boxRT.sizeDelta = size;

        Image bg = boxObj.GetComponent<Image>();
        bg.color = boxColor;
        bg.raycastTarget = false;

        GameObject txtObj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        txtObj.transform.SetParent(boxObj.transform, false);
        RectTransform txtRT = txtObj.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = new Vector2(3f, 1f);
        txtRT.offsetMax = new Vector2(-3f, -1f);

        TextMeshProUGUI tmp = txtObj.GetComponent<TextMeshProUGUI>();
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = textColor;
        tmp.alignment = align;
        tmp.raycastTarget = false;
        return tmp;
    }

    TextMeshProUGUI CreateLabel(Transform parent, string name, Vector2 topOffset, Vector2 size, float fontSize, FontStyles style, Color color, TextAlignmentOptions align)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = topOffset;
        rt.sizeDelta = size;

        TextMeshProUGUI tmp = obj.GetComponent<TextMeshProUGUI>();
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.color = color;
        tmp.alignment = align;
        tmp.raycastTarget = false;
        return tmp;
    }

    Button CreateActionButton(Transform parent, string label, Vector2 anchoredPos, Vector2 size, Color bgColor, UnityEngine.Events.UnityAction onClick)
    {
        GameObject btnObj = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        btnObj.transform.SetParent(parent, false);
        RectTransform rt = btnObj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        btnObj.GetComponent<Image>().color = bgColor;
        Button btn = btnObj.GetComponent<Button>();
        btn.onClick.AddListener(onClick);

        GameObject txtObj = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        txtObj.transform.SetParent(btnObj.transform, false);
        RectTransform txtRT = txtObj.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = Vector2.zero;
        txtRT.offsetMax = Vector2.zero;

        TextMeshProUGUI txt = txtObj.GetComponent<TextMeshProUGUI>();
        txt.text = label;
        txt.fontSize = 14;
        txt.fontStyle = FontStyles.Bold;
        txt.alignment = TextAlignmentOptions.Center;
        txt.color = Color.white;
        txt.raycastTarget = false;

        return btn;
    }

    void AddHoverTrigger(GameObject target, Action onEnter)
    {
        EventTrigger trigger = target.GetComponent<EventTrigger>() ?? target.AddComponent<EventTrigger>();
        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        entry.callback.AddListener(_ => onEnter?.Invoke());
        trigger.triggers.Add(entry);
    }

    void AddRightClickTrigger(GameObject target, Action onRightClick)
    {
        EventTrigger trigger = target.GetComponent<EventTrigger>() ?? target.AddComponent<EventTrigger>();
        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
        entry.callback.AddListener(data =>
        {
            if (data is PointerEventData ped && ped.button == PointerEventData.InputButton.Right)
            {
                onRightClick?.Invoke();
            }
        });
        trigger.triggers.Add(entry);
    }

    Sprite CreateProceduralCardBack()
    {
        int w = 64;
        int h = 92;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        Color border = new Color(0.75f, 0.58f, 0.25f, 1f);
        Color body = new Color(0.28f, 0.14f, 0.08f, 1f);
        Color vortex = new Color(0.12f, 0.06f, 0.18f, 1f);

        Vector2 center = new Vector2(w * 0.5f, h * 0.5f);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x < 4 || x >= w - 4 || y < 4 || y >= h - 4)
                {
                    tex.SetPixel(x, y, border);
                }
                else
                {
                    float dx = (x - center.x) / 18f;
                    float dy = (y - center.y) / 26f;
                    float dist = dx * dx + dy * dy;
                    tex.SetPixel(x, y, dist < 1f ? vortex : body);
                }
            }
        }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
    }
}