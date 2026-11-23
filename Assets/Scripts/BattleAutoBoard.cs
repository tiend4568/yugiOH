using System;
using System.Collections.Generic;
using System.Linq;
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
    private Button activateEffectButton;
    private TextMeshProUGUI activateEffectButtonText;
    private Button directAttackButton;
    private Button cancelSelectButton;

    // Thông tin Mộ bài & Bộ bài/Tay bài
    private TextMeshProUGUI playerGyText;
    private TextMeshProUGUI aiGyText;
    private TextMeshProUGUI playerDeckHandInfoText;
    private TextMeshProUGUI aiDeckHandInfoText;

    // Popup chọn bài từ Mộ (Hồi sinh / Hiệu ứng FLIP / Xem Mộ)
    private GameObject graveyardModal;
    private TextMeshProUGUI graveyardModalTitle;
    private RectTransform graveyardGridContent;
    private Button graveyardCloseButton;
    private TextMeshProUGUI graveyardCloseButtonText;
    private Button graveyardSecondaryCancelButton;
    private TextMeshProUGUI graveyardSecondaryCancelButtonText;
    private Action<int> onGraveyardCardPicked;
    private Action onGraveyardCancelled;
    private Action onGraveyardSecondaryCancelled;
    private Func<CardData, bool> graveyardCardFilter;

    // Menu chọn lá bài đang úp (Phép/Bẫy) để kích hoạt ngay khi bị tấn công
    private GameObject defenseReactionModal;
    private TextMeshProUGUI defenseReactionTitle;
    private RectTransform defenseReactionGridContent;
    private Button defenseReactionSkipButton;
    private Action<int> onDefenseReactionZonePicked;
    private string lastDefenseReactionTitle = "";
    private Action<int> lastDefenseReactionCallback = null;
    private bool lastDefenseReactionIsTrapHole = false;
    private MonsterCard lastDefenseReactionSummonedTarget = null;

    // Trạng thái tương tác của người chơi
    private int selectedAttackerZone = -1;
    private bool isSelectingTributes = false;
    private bool isSelectingEffectTributes = false;
    private bool isSelectingEffectEnemyTarget = false;
    private int pendingEffectSourceZone = -1;
    private int pendingSummonHandIndex = -1;
    private BattlePosition pendingSummonPosition = BattlePosition.FaceUpAttack;
    private int pendingPreferredZone = -1;
    private int requiredTributeCount = 0;
    private readonly List<int> chosenTributeZones = new List<int>();

    private Sprite cardBackSprite;
    private bool isInitialized = false;

    // Giữ hiển thị tạm thời lá bài bị tiêu diệt trong lúc quả cầu lửa đang bay tới, rồi vỡ đa giác vàng đúng lúc va chạm
    private struct PendingDestroyHold
    {
        public bool active;
        public float remainingTime;
        public Sprite sprite;
        public bool isDefense;
        public Action onShatter;
    }

    private readonly PendingDestroyHold[] playerDestroyHolds = new PendingDestroyHold[Player.ZoneCount];
    private readonly PendingDestroyHold[] aiDestroyHolds = new PendingDestroyHold[Player.ZoneCount];

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

        cardBackSprite = LoadCardBackSprite();

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

        // Đặt lớp VFX ngay trên các lá bài & HUD nhưng nằm dưới các popup/modal
        BattleVfxManager.EnsureCreated(boardRoot);
        GodSummonVfxController.EnsureCreated(boardRoot);

        BuildGraveyardModal();
        BuildDefenseReactionModal();
    }

    void Update()
    {
        if (!isInitialized) return;
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel == null) return;

        bm.SyncPlayerHandCounts();
        UpdateDestroyHolds(bm, Mathf.Min(Time.deltaTime, 0.05f));
        RefreshFieldZones(bm);
        RefreshHandIfChanged(bm);
        RefreshActionBar(bm);
        RefreshGraveyardCounters(bm);
    }

    // ==================== HỖ TRỢ VFX TẤN CÔNG (QUẢ CẦU LỬA) & PHÁ HỦY (ĐA GIÁC VÀNG) ====================

    public float PlayAttackFireballVfx(
        bool attackerIsPlayer,
        int attackerZone,
        bool isDirectAttack,
        bool targetIsPlayer,
        int targetZone,
        Action onImpact = null)
    {
        if (!isInitialized || BattleVfxManager.Instance == null)
        {
            onImpact?.Invoke();
            return 0f;
        }
        if (attackerZone < 0 || attackerZone >= Player.ZoneCount)
        {
            onImpact?.Invoke();
            return 0f;
        }

        ZoneSlotUI attackerSlot = attackerIsPlayer ? playerZoneSlots[attackerZone] : aiZoneSlots[attackerZone];
        if (attackerSlot == null || attackerSlot.slotRT == null)
        {
            onImpact?.Invoke();
            return 0f;
        }

        BattleVfxManager vfx = BattleVfxManager.Instance;
        Vector2 startLocal = vfx.GetRectCenterLocal(attackerSlot.slotRT);
        Vector2 targetLocal;

        if (!isDirectAttack && targetZone >= 0 && targetZone < Player.ZoneCount)
        {
            ZoneSlotUI targetSlot = targetIsPlayer ? playerZoneSlots[targetZone] : aiZoneSlots[targetZone];
            targetLocal = (targetSlot != null && targetSlot.slotRT != null)
                ? vfx.GetRectCenterLocal(targetSlot.slotRT)
                : new Vector2(startLocal.x, targetIsPlayer ? -10f : 165f);
        }
        else
        {
            // Đánh trực tiếp: bắn quả cầu lửa thẳng về phía khung điểm gốc (LP) đối thủ
            BattleManager bm = BattleManager.Instance;
            TextMeshProUGUI lpText = bm != null ? (targetIsPlayer ? bm.playerLPText : bm.aiLPText) : null;
            targetLocal = (lpText != null)
                ? vfx.GetRectCenterLocal(lpText.rectTransform)
                : new Vector2(startLocal.x, targetIsPlayer ? -385f : 395f);
        }

        return vfx.PlayFireballAttack(attackerSlot.slotRT, attackerSlot.cardVisualRT, targetLocal, onImpact);
    }

    public void TriggerMonsterDestroyVfx(
        bool isPlayer,
        int zoneIndex,
        Sprite cardSprite,
        bool isDefense,
        float impactDelay = 0f,
        Action onShatter = null)
    {
        if (!isInitialized || zoneIndex < 0 || zoneIndex >= Player.ZoneCount)
        {
            onShatter?.Invoke();
            return;
        }

        ZoneSlotUI slot = isPlayer ? playerZoneSlots[zoneIndex] : aiZoneSlots[zoneIndex];
        PendingDestroyHold[] holds = isPlayer ? playerDestroyHolds : aiDestroyHolds;

        if (impactDelay <= 0.001f || BattleVfxManager.Instance == null)
        {
            holds[zoneIndex].active = false;
            holds[zoneIndex].onShatter = null;
            if (slot != null && slot.slotRT != null && BattleVfxManager.Instance != null)
            {
                BattleVfxManager.Instance.PlayDestroyShatter(slot.slotRT, isDefense);
            }
            onShatter?.Invoke();
            return;
        }

        // Nếu đang có hold cũ trên cùng ô -> vỡ ngay hold cũ trước khi giữ lá mới
        if (holds[zoneIndex].active)
        {
            FlushDestroyHoldImmediate(isPlayer, zoneIndex);
        }

        Sprite resolvedSprite = cardSprite;
        if (resolvedSprite == null && slot != null && slot.cardArtImage != null)
        {
            resolvedSprite = slot.cardArtImage.sprite;
        }
        if (resolvedSprite == null) resolvedSprite = cardBackSprite;

        holds[zoneIndex] = new PendingDestroyHold
        {
            active = true,
            remainingTime = impactDelay,
            sprite = resolvedSprite,
            isDefense = isDefense,
            onShatter = onShatter
        };
    }

    public void FlushDestroyHoldImmediate(bool isPlayer, int zoneIndex)
    {
        if (zoneIndex < 0 || zoneIndex >= Player.ZoneCount) return;

        PendingDestroyHold[] holds = isPlayer ? playerDestroyHolds : aiDestroyHolds;
        if (!holds[zoneIndex].active) return;

        PendingDestroyHold h = holds[zoneIndex];
        holds[zoneIndex].active = false;
        holds[zoneIndex].onShatter = null;

        ZoneSlotUI slot = isPlayer ? playerZoneSlots[zoneIndex] : aiZoneSlots[zoneIndex];
        if (slot != null && slot.slotRT != null && BattleVfxManager.Instance != null)
        {
            BattleVfxManager.Instance.PlayDestroyShatter(slot.slotRT, h.isDefense);
        }
        h.onShatter?.Invoke();
    }

    public void PlayGodSummonVfx(MonsterEffectType godEffect, Sprite cardSprite, bool isPlayer, int zoneIndex)
    {
        if (!isInitialized) return;
        GodSummonVfxController ctrl = GodSummonVfxController.EnsureCreated(boardRoot);
        if (ctrl == null) return;

        RectTransform targetSlotRT = null;
        if (zoneIndex >= 0 && zoneIndex < Player.ZoneCount)
        {
            ZoneSlotUI slot = isPlayer ? playerZoneSlots[zoneIndex] : aiZoneSlots[zoneIndex];
            if (slot != null) targetSlotRT = slot.slotRT;
        }

        ctrl.PlayGodSummon(godEffect, cardSprite != null ? cardSprite : cardBackSprite, targetSlotRT);
    }

    public void ClearVfxAndHolds()
    {
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            playerDestroyHolds[i].active = false;
            playerDestroyHolds[i].onShatter = null;
            aiDestroyHolds[i].active = false;
            aiDestroyHolds[i].onShatter = null;
        }
        if (BattleVfxManager.Instance != null)
        {
            BattleVfxManager.Instance.ClearAllVfx();
        }
        if (GodSummonVfxController.Instance != null)
        {
            GodSummonVfxController.Instance.StopImmediately();
        }
    }

    private void UpdateDestroyHolds(BattleManager bm, float dt)
    {
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (playerDestroyHolds[i].active)
            {
                playerDestroyHolds[i].remainingTime -= dt;
                bool occupiedByNewCard = bm.humanPlayer != null && bm.humanPlayer.monsterZones[i] != null;
                if (occupiedByNewCard || playerDestroyHolds[i].remainingTime <= 0f)
                {
                    FlushDestroyHoldImmediate(isPlayer: true, i);
                }
            }

            if (aiDestroyHolds[i].active)
            {
                aiDestroyHolds[i].remainingTime -= dt;
                bool occupiedByNewCard = bm.aiPlayer != null && bm.aiPlayer.monsterZones[i] != null;
                if (occupiedByNewCard || aiDestroyHolds[i].remainingTime <= 0f)
                {
                    FlushDestroyHoldImmediate(isPlayer: false, i);
                }
            }
        }
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
        rt.sizeDelta = new Vector2(780f, 40f);

        changePosButton = CreateActionButton(actionBarObj.transform, "ĐỔI THẾ (CÔNG/THỦ)", new Vector2(-275f, 0f), new Vector2(180f, 36f), new Color(0.2f, 0.5f, 0.8f, 0.95f), () =>
        {
            if (selectedAttackerZone >= 0 && BattleManager.Instance != null)
            {
                BattleManager.Instance.PlayerChangePosition(selectedAttackerZone);
                ClearSelectionState();
            }
        });

        activateEffectButton = CreateActionButton(actionBarObj.transform, "KÍCH HOẠT HIỆU ỨNG", new Vector2(-72f, 0f), new Vector2(210f, 36f), new Color(0.62f, 0.25f, 0.78f, 0.96f), OnClickActivateFieldMonsterEffect);
        activateEffectButtonText = activateEffectButton.GetComponentInChildren<TextMeshProUGUI>();

        directAttackButton = CreateActionButton(actionBarObj.transform, "ĐÁNH TRỰC TIẾP", new Vector2(126f, 0f), new Vector2(165f, 36f), new Color(0.85f, 0.25f, 0.2f, 0.95f), () =>
        {
            if (selectedAttackerZone >= 0 && BattleManager.Instance != null)
            {
                BattleManager.Instance.PlayerDirectAttack(selectedAttackerZone);
                ClearSelectionState();
            }
        });

        cancelSelectButton = CreateActionButton(actionBarObj.transform, "HỦY CHỌN", new Vector2(282f, 0f), new Vector2(128f, 36f), new Color(0.35f, 0.35f, 0.4f, 0.95f), ClearSelectionState);

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

        playerDeckHandInfoText = CreateBoxedLabel(
            boardRoot,
            "PlayerDeckHandInfo",
            anchorMin: new Vector2(0.5f, 0.5f),
            anchorMax: new Vector2(0.5f, 0.5f),
            pivot: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(455f, -138f),
            size: new Vector2(135f, 30f),
            fontSize: 12f,
            style: FontStyles.Bold,
            textColor: Color.white,
            align: TextAlignmentOptions.Center,
            boxColor: new Color(0f, 0f, 0f, 0.72f),
            out _
        );

        Button aGyBtn = CreateActionButton(boardRoot, "MỘ AI (0)", new Vector2(455f, 248f), new Vector2(135f, 50f), new Color(0.32f, 0.18f, 0.22f, 0.92f), () =>
        {
            BattleManager bm = BattleManager.Instance;
            if (bm != null) OpenGraveyardViewOnly("MỘ BÀI ĐỐI PHƯƠNG", bm.aiGraveyard);
        });
        aiGyText = aGyBtn.GetComponentInChildren<TextMeshProUGUI>();

        aiDeckHandInfoText = CreateBoxedLabel(
            boardRoot,
            "AIDeckHandInfo",
            anchorMin: new Vector2(0.5f, 0.5f),
            anchorMax: new Vector2(0.5f, 0.5f),
            pivot: new Vector2(0.5f, 0.5f),
            anchoredPos: new Vector2(455f, 202f),
            size: new Vector2(135f, 30f),
            fontSize: 12f,
            style: FontStyles.Bold,
            textColor: Color.white,
            align: TextAlignmentOptions.Center,
            boxColor: new Color(0f, 0f, 0f, 0.72f),
            out _
        );
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

        graveyardCloseButton = CreateActionButton(graveyardModal.transform, "ĐÓNG", new Vector2(0f, -196f), new Vector2(240f, 42f), new Color(0.7f, 0.22f, 0.22f, 1f), () =>
        {
            Action cancelCb = onGraveyardCancelled;
            onGraveyardCardPicked = null;
            onGraveyardCancelled = null;
            onGraveyardSecondaryCancelled = null;
            graveyardCardFilter = null;
            graveyardModal.SetActive(false);
            cancelCb?.Invoke();
        });
        graveyardCloseButtonText = graveyardCloseButton.GetComponentInChildren<TextMeshProUGUI>();

        graveyardSecondaryCancelButton = CreateActionButton(graveyardModal.transform, "HỦY LUÔN (KHÔNG KÍCH HOẠT)", new Vector2(150f, -196f), new Vector2(260f, 42f), new Color(0.35f, 0.35f, 0.42f, 1f), () =>
        {
            Action secCb = onGraveyardSecondaryCancelled;
            onGraveyardCardPicked = null;
            onGraveyardCancelled = null;
            onGraveyardSecondaryCancelled = null;
            graveyardCardFilter = null;
            graveyardModal.SetActive(false);
            secCb?.Invoke();
        });
        graveyardSecondaryCancelButtonText = graveyardSecondaryCancelButton.GetComponentInChildren<TextMeshProUGUI>();
        graveyardSecondaryCancelButton.gameObject.SetActive(false);

        graveyardModal.SetActive(false);
    }

    void BuildDefenseReactionModal()
    {
        defenseReactionModal = new GameObject("DefenseReactionModal", typeof(RectTransform), typeof(Image));
        defenseReactionModal.transform.SetParent(boardCanvas.transform, false);
        RectTransform mRT = defenseReactionModal.GetComponent<RectTransform>();
        mRT.anchorMin = new Vector2(0.5f, 0.5f);
        mRT.anchorMax = new Vector2(0.5f, 0.5f);
        mRT.sizeDelta = new Vector2(820f, 380f);
        defenseReactionModal.GetComponent<Image>().color = new Color(0.11f, 0.05f, 0.16f, 0.96f);

        defenseReactionTitle = CreateBoxedLabel(
            defenseReactionModal.transform,
            "Title",
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0f, -14f),
            new Vector2(770f, 54f),
            18f,
            FontStyles.Bold,
            Color.white,
            TextAlignmentOptions.Center,
            new Color(0f, 0f, 0f, 0.78f),
            out _
        );
        defenseReactionTitle.enableWordWrapping = true;

        GameObject gridObj = new GameObject("SetCardsGrid", typeof(RectTransform), typeof(GridLayoutGroup));
        gridObj.transform.SetParent(defenseReactionModal.transform, false);
        defenseReactionGridContent = gridObj.GetComponent<RectTransform>();
        defenseReactionGridContent.anchorMin = new Vector2(0.5f, 0.5f);
        defenseReactionGridContent.anchorMax = new Vector2(0.5f, 0.5f);
        defenseReactionGridContent.anchoredPosition = new Vector2(0f, -6f);
        defenseReactionGridContent.sizeDelta = new Vector2(760f, 210f);

        GridLayoutGroup grid = gridObj.GetComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(132f, 186f);
        grid.spacing = new Vector2(16f, 12f);
        grid.childAlignment = TextAnchor.MiddleCenter;

        defenseReactionSkipButton = CreateActionButton(
            defenseReactionModal.transform,
            "KHÔNG KÍCH HOẠT (BỎ QUA)",
            new Vector2(0f, -152f),
            new Vector2(280f, 44f),
            new Color(0.35f, 0.35f, 0.42f, 1f),
            () =>
            {
                Action<int> cb = onDefenseReactionZonePicked;
                onDefenseReactionZonePicked = null;
                defenseReactionModal.SetActive(false);
                cb?.Invoke(-1);
            }
        );

        defenseReactionModal.SetActive(false);
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
        if (bm == null || bm.duel == null) return;
        if (bm.IsSelectingEndPhaseDiscard)
        {
            bm.SelectHandCardForEndPhaseDiscard(handIndex);
            return;
        }
        if (bm.duel.Current != bm.humanPlayer)
        {
            if (bm.IsMultiplayer)
            {
                bm.SetStatus($"Chưa tới lượt của bạn! Đang là lượt của {bm.OpponentDisplayName}.");
            }
            return;
        }
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

    public void OpenGraveyardSelection(
        List<CardData> graveyard,
        Action<int> onPicked,
        string customTitle = null,
        Func<CardData, bool> filter = null,
        Action onCancelled = null,
        string cancelButtonLabel = "HỦY KÍCH HOẠT",
        Action onSecondaryCancelled = null,
        string secondaryCancelButtonLabel = "HỦY LUÔN (KHÔNG KÍCH HOẠT)")
    {
        if (graveyardModal == null) return;
        onGraveyardCardPicked = onPicked;
        onGraveyardCancelled = onCancelled;
        onGraveyardSecondaryCancelled = onSecondaryCancelled;
        graveyardCardFilter = filter;
        graveyardModalTitle.text = string.IsNullOrEmpty(customTitle)
            ? "CHỌN 1 LÁ BÀI TRONG MỘ ĐỂ XÁO TRỘN VÀO DECK"
            : customTitle;

        bool canCancel = (onCancelled != null);
        bool hasSecondaryCancel = (onSecondaryCancelled != null);

        if (graveyardCloseButton != null)
        {
            graveyardCloseButton.gameObject.SetActive(canCancel);
            if (canCancel)
            {
                if (graveyardCloseButtonText != null)
                {
                    graveyardCloseButtonText.text = string.IsNullOrEmpty(cancelButtonLabel) ? "HỦY KÍCH HOẠT" : cancelButtonLabel;
                }
                RectTransform closeRT = graveyardCloseButton.GetComponent<RectTransform>();
                if (closeRT != null)
                {
                    closeRT.anchoredPosition = hasSecondaryCancel ? new Vector2(-145f, -196f) : new Vector2(0f, -196f);
                    closeRT.sizeDelta = new Vector2(hasSecondaryCancel ? 260f : 240f, 42f);
                }
            }
        }

        if (graveyardSecondaryCancelButton != null)
        {
            graveyardSecondaryCancelButton.gameObject.SetActive(hasSecondaryCancel);
            if (hasSecondaryCancel)
            {
                if (graveyardSecondaryCancelButtonText != null)
                {
                    graveyardSecondaryCancelButtonText.text = string.IsNullOrEmpty(secondaryCancelButtonLabel)
                        ? "HỦY LUÔN (KHÔNG KÍCH HOẠT)"
                        : secondaryCancelButtonLabel;
                }
                RectTransform secRT = graveyardSecondaryCancelButton.GetComponent<RectTransform>();
                if (secRT != null)
                {
                    secRT.anchoredPosition = new Vector2(145f, -196f);
                    secRT.sizeDelta = new Vector2(260f, 42f);
                }
            }
        }

        PopulateGraveyardGrid(graveyard, selectable: true);
        graveyardModal.transform.SetAsLastSibling();
        graveyardModal.SetActive(true);
    }

    public bool IsGraveyardModalOpen => graveyardModal != null && graveyardModal.activeSelf;

    public void CloseGraveyardModalIfOpen()
    {
        if (graveyardModal != null && graveyardModal.activeSelf)
        {
            onGraveyardCardPicked = null;
            onGraveyardCancelled = null;
            onGraveyardSecondaryCancelled = null;
            graveyardCardFilter = null;
            graveyardModal.SetActive(false);
        }
    }

    public void ReopenLastDefenseReactionModal()
    {
        if (lastDefenseReactionCallback != null)
        {
            OpenDefenseReactionModal(
                lastDefenseReactionTitle,
                lastDefenseReactionCallback,
                lastDefenseReactionIsTrapHole,
                lastDefenseReactionSummonedTarget
            );
        }
    }

    public void OpenDefenseReactionModal(
        string customTitle,
        Action<int> onZonePickedOrSkipped,
        bool isTrapHolePrompt = false,
        MonsterCard summonedTargetForTrapHole = null)
    {
        if (defenseReactionModal == null) return;
        BattleManager bm = BattleManager.Instance;
        if (bm == null) return;

        lastDefenseReactionTitle = customTitle;
        lastDefenseReactionCallback = onZonePickedOrSkipped;
        lastDefenseReactionIsTrapHole = isTrapHolePrompt;
        lastDefenseReactionSummonedTarget = summonedTargetForTrapHole;
        onDefenseReactionZonePicked = onZonePickedOrSkipped;
        defenseReactionTitle.text = string.IsNullOrEmpty(customTitle)
            ? (isTrapHolePrompt
                ? "ĐỐI THỦ VỪA TRIỆU HỒI QUÁI THÚ (ATK >= 1000)! CHỌN KÍCH HOẠT [HANG RƠI] HOẶC BỎ QUA"
                : "ĐỐI THỦ TẤN CÔNG! CHỌN KÍCH HOẠT BÀI PHÉP / BẪY ĐANG ÚP / KURIBOH HOẶC BỎ QUA")
            : customTitle;

        for (int i = defenseReactionGridContent.childCount - 1; i >= 0; i--)
        {
            Destroy(defenseReactionGridContent.GetChild(i).gameObject);
        }

        // 1. Hiển thị Kuriboh trên tay nếu đang bị tấn công (!isTrapHolePrompt)
        if (!isTrapHolePrompt && bm.HasKuribohInPlayerHand())
        {
            CardData kuriboh = bm.GetFirstKuribohInPlayerHand();
            if (kuriboh != null)
            {
                GameObject kItem = new GameObject($"HandReaction_Kuriboh", typeof(RectTransform), typeof(Image), typeof(Button));
                kItem.transform.SetParent(defenseReactionGridContent, false);

                Image kImg = kItem.GetComponent<Image>();
                kImg.sprite = kuriboh.cardSprite != null ? kuriboh.cardSprite : cardBackSprite;
                kImg.preserveAspect = true;
                kImg.color = Color.white;

                TextMeshProUGUI kTop = CreateBoxedLabel(
                    kItem.transform,
                    "TopBadge",
                    anchorMin: new Vector2(0.5f, 1f),
                    anchorMax: new Vector2(0.5f, 1f),
                    pivot: new Vector2(0.5f, 1f),
                    anchoredPos: new Vector2(0f, -2f),
                    size: new Vector2(128f, 22f),
                    fontSize: 10.5f,
                    style: FontStyles.Bold,
                    textColor: Color.white,
                    align: TextAlignmentOptions.Center,
                    boxColor: new Color(0.75f, 0.42f, 0.12f, 0.94f),
                    out _
                );
                kTop.text = "TRÊN TAY [KÍCH HOẠT]";

                TextMeshProUGUI kBottom = CreateBoxedLabel(
                    kItem.transform,
                    "BottomBadge",
                    anchorMin: new Vector2(0.5f, 0f),
                    anchorMax: new Vector2(0.5f, 0f),
                    pivot: new Vector2(0.5f, 0f),
                    anchoredPos: new Vector2(0f, 2f),
                    size: new Vector2(128f, 36f),
                    fontSize: 10.5f,
                    style: FontStyles.Bold,
                    textColor: Color.white,
                    align: TextAlignmentOptions.Center,
                    boxColor: new Color(0f, 0f, 0f, 0.84f),
                    out _
                );
                kBottom.enableWordWrapping = true;
                kBottom.text = $"{kuriboh.cardName}\n[0 SÁT THƯƠNG]";

                AddHoverTrigger(kItem, () => ShowInspectorCard(kuriboh, null));

                Button kBtn = kItem.GetComponent<Button>();
                kBtn.onClick.AddListener(() =>
                {
                    ShowInspectorCard(kuriboh, null);
                    Action<int> cb = onDefenseReactionZonePicked;
                    onDefenseReactionZonePicked = null;
                    defenseReactionModal.SetActive(false);
                    cb?.Invoke(BattleManager.KURIBOH_HAND_REACTION_ZONE);
                });
            }
        }

        // 2. Hiển thị các lá bài Phép/Bẫy đang đặt úp trên sân
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            CardData sp = bm.playerSpellZones[i];
            if (sp == null || !bm.playerSpellFaceDown[i]) continue;

            int zoneIdx = i;
            string failReason;
            bool canActivate = isTrapHolePrompt
                ? bm.CanPlayerTrapHoleReactOnSummon(zoneIdx, summonedTargetForTrapHole, out failReason)
                : bm.CanPlayerSetCardReact(zoneIdx, out failReason);

            GameObject item = new GameObject($"SetCard_{zoneIdx}_{sp.cardName}", typeof(RectTransform), typeof(Image), typeof(Button));
            item.transform.SetParent(defenseReactionGridContent, false);

            Image img = item.GetComponent<Image>();
            img.sprite = sp.cardSprite != null ? sp.cardSprite : cardBackSprite;
            img.preserveAspect = true;
            img.color = canActivate ? Color.white : new Color(0.45f, 0.45f, 0.52f, 0.85f);

            TextMeshProUGUI topBadge = CreateBoxedLabel(
                item.transform,
                "TopBadge",
                anchorMin: new Vector2(0.5f, 1f),
                anchorMax: new Vector2(0.5f, 1f),
                pivot: new Vector2(0.5f, 1f),
                anchoredPos: new Vector2(0f, -2f),
                size: new Vector2(128f, 22f),
                fontSize: 11f,
                style: FontStyles.Bold,
                textColor: Color.white,
                align: TextAlignmentOptions.Center,
                boxColor: canActivate
                    ? new Color(0.14f, 0.62f, 0.32f, 0.92f)
                    : new Color(0.45f, 0.25f, 0.15f, 0.88f),
                out _
            );
            topBadge.text = canActivate ? $"Ô #{zoneIdx + 1} [KÍCH HOẠT]" : $"Ô #{zoneIdx + 1} [CHƯA ĐỦ ĐK]";

            TextMeshProUGUI bottomBadge = CreateBoxedLabel(
                item.transform,
                "BottomBadge",
                anchorMin: new Vector2(0.5f, 0f),
                anchorMax: new Vector2(0.5f, 0f),
                pivot: new Vector2(0.5f, 0f),
                anchoredPos: new Vector2(0f, 2f),
                size: new Vector2(128f, 36f),
                fontSize: 11f,
                style: FontStyles.Bold,
                textColor: Color.white,
                align: TextAlignmentOptions.Center,
                boxColor: new Color(0f, 0f, 0f, 0.82f),
                out _
            );
            bottomBadge.enableWordWrapping = true;
            bottomBadge.text = $"{sp.cardName}\n[{sp.CardTag}]";

            AddHoverTrigger(item, () => ShowInspectorCard(sp, null));

            Button btn = item.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                ShowInspectorCard(sp, null);
                if (!canActivate)
                {
                    bm.SetStatus($"[{sp.cardName}] chưa thể kích hoạt: {failReason}");
                    return;
                }

                Action<int> cb = onDefenseReactionZonePicked;
                onDefenseReactionZonePicked = null;
                defenseReactionModal.SetActive(false);
                cb?.Invoke(zoneIdx);
            });
        }

        defenseReactionModal.transform.SetAsLastSibling();
        defenseReactionModal.SetActive(true);
    }

    public void CloseDefenseReactionModalIfOpen()
    {
        if (defenseReactionModal != null && defenseReactionModal.activeSelf)
        {
            onDefenseReactionZonePicked = null;
            defenseReactionModal.SetActive(false);
        }
    }

    void OpenGraveyardViewOnly(string title, List<CardData> graveyard)
    {
        if (graveyardModal == null) return;
        onGraveyardCardPicked = null;
        onGraveyardCancelled = null;
        onGraveyardSecondaryCancelled = null;
        graveyardCardFilter = null;
        graveyardModalTitle.text = $"{title} ({graveyard.Count} lá)";
        if (graveyardCloseButton != null)
        {
            graveyardCloseButton.gameObject.SetActive(true);
            if (graveyardCloseButtonText != null) graveyardCloseButtonText.text = "ĐÓNG";
            RectTransform closeRT = graveyardCloseButton.GetComponent<RectTransform>();
            if (closeRT != null)
            {
                closeRT.anchoredPosition = new Vector2(0f, -196f);
                closeRT.sizeDelta = new Vector2(180f, 42f);
            }
        }
        if (graveyardSecondaryCancelButton != null)
        {
            graveyardSecondaryCancelButton.gameObject.SetActive(false);
        }

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
                ? $"{card.cardName}\n{card.StatDisplay}"
                : card.cardName;

            Button btn = item.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                ShowInspectorCard(card, null);
                if (selectable && onGraveyardCardPicked != null)
                {
                    Action<int> cb = onGraveyardCardPicked;
                    onGraveyardCardPicked = null;
                    onGraveyardCancelled = null;
                    onGraveyardSecondaryCancelled = null;
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
                if (playerDestroyHolds[i].active)
                {
                    slot.cardArtImage.enabled = true;
                    slot.cardArtImage.sprite = playerDestroyHolds[i].sprite != null ? playerDestroyHolds[i].sprite : cardBackSprite;
                    slot.cardArtImage.color = new Color(1f, 0.92f, 0.78f, 1f);
                    slot.cardVisualRT.localEulerAngles = playerDestroyHolds[i].isDefense ? new Vector3(0f, 0f, 90f) : Vector3.zero;
                    SetSlotBadges(slot, "", "");
                    slot.frameImage.color = new Color(0.92f, 0.45f, 0.15f, 0.72f);
                }
                else
                {
                    slot.cardArtImage.enabled = false;
                    SetSlotBadges(slot, "", "");
                    slot.frameImage.color = new Color(0.18f, 0.42f, 0.65f, 0.45f);
                }
            }
            else
            {
                slot.cardArtImage.enabled = true;
                if (mc.IsFaceDown)
                {
                    slot.cardArtImage.sprite = cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                }
                else
                {
                    slot.cardArtImage.sprite = mc.data.cardSprite != null ? mc.data.cardSprite : cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                }
                slot.cardVisualRT.localEulerAngles = mc.IsDefense ? new Vector3(0f, 0f, 90f) : Vector3.zero;

                string statStr = $"{mc.Atk}/{mc.Def}";
                string stateStr;

                if ((isSelectingTributes || isSelectingEffectTributes) && chosenTributeZones.Contains(i))
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
                if (aiDestroyHolds[i].active)
                {
                    slot.cardArtImage.enabled = true;
                    slot.cardArtImage.sprite = aiDestroyHolds[i].sprite != null ? aiDestroyHolds[i].sprite : cardBackSprite;
                    slot.cardArtImage.color = new Color(1f, 0.92f, 0.78f, 1f);
                    slot.cardVisualRT.localEulerAngles = aiDestroyHolds[i].isDefense ? new Vector3(0f, 0f, 90f) : Vector3.zero;
                    SetSlotBadges(slot, "", "");
                    slot.frameImage.color = new Color(0.95f, 0.35f, 0.12f, 0.78f);
                }
                else
                {
                    slot.cardArtImage.enabled = false;
                    SetSlotBadges(slot, "", "");
                    slot.frameImage.color = (selectedAttackerZone >= 0 && bm.aiPlayer.MonsterCount == 0)
                        ? new Color(0.9f, 0.25f, 0.25f, 0.55f)
                        : new Color(0.55f, 0.18f, 0.18f, 0.45f);
                }
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

                slot.frameImage.color = (selectedAttackerZone >= 0 || isSelectingEffectEnemyTarget)
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

                if (bm.playerSpellFaceDown[i])
                {
                    slot.cardArtImage.sprite = cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                    SetSlotBadges(slot, "[ÚP - BẤM DÙNG]", sp.cardName);
                    slot.frameImage.color = new Color(0.18f, 0.62f, 0.48f, 0.78f);
                }
                else
                {
                    slot.cardArtImage.sprite = sp.cardSprite != null ? sp.cardSprite : cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                    string topState = "[KÍCH HOẠT]";
                    if (sp.spellEffect == SpellEffectType.SwordsOfRevealingLight && bm.aiPlayer != null && bm.aiPlayer.swordsBlockTurnsRemaining > 0)
                    {
                        topState = $"[KHÓA {bm.aiPlayer.swordsBlockTurnsRemaining} LƯỢT]";
                    }
                    SetSlotBadges(slot, topState, sp.cardName);
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
                    slot.cardArtImage.color = Color.white;
                    SetSlotBadges(slot, "[ÚP]", "");
                }
                else
                {
                    slot.cardArtImage.sprite = sp.cardSprite != null ? sp.cardSprite : cardBackSprite;
                    slot.cardArtImage.color = Color.white;
                    string topState = "[KÍCH HOẠT]";
                    if (sp.spellEffect == SpellEffectType.SwordsOfRevealingLight && bm.humanPlayer != null && bm.humanPlayer.swordsBlockTurnsRemaining > 0)
                    {
                        topState = $"[KHÓA {bm.humanPlayer.swordsBlockTurnsRemaining} LƯỢT]";
                    }
                    SetSlotBadges(slot, topState, sp.cardName);
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
                ? $"LV{card.level} {card.StatDisplay}"
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
        if (!isPlayerTurn)
        {
            if (isSelectingTributes || isSelectingEffectTributes || isSelectingEffectEnemyTarget || selectedAttackerZone >= 0)
            {
                ClearSelectionState();
            }
            actionBarObj.SetActive(false);
            return;
        }

        if (bm.currentState != BattleManager.TurnState.MainPhase &&
            (isSelectingTributes || isSelectingEffectTributes || isSelectingEffectEnemyTarget))
        {
            ClearSelectionState();
        }

        if (isSelectingTributes || isSelectingEffectTributes || isSelectingEffectEnemyTarget)
        {
            actionBarObj.SetActive(true);
            changePosButton.gameObject.SetActive(false);
            if (activateEffectButton != null) activateEffectButton.gameObject.SetActive(false);
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

        bool canShowEffectBtn = false;
        string effectLabel = "KÍCH HOẠT HIỆU ỨNG";

        if (!selected.IsFaceDown && !selected.isMagicalHatDecoy && selected.data != null && bm.currentState == BattleManager.TurnState.MainPhase)
        {
            if (selected.data.monsterEffect == MonsterEffectType.DivineObelisk && !selected.hasAttacked)
            {
                canShowEffectBtn = true;
                effectLabel = "HIẾN TẾ 2 QUÁI (DIỆT SÂN)";
            }
            else if (selected.data.monsterEffect == MonsterEffectType.DivineRa)
            {
                if (bm.humanPlayer.lifePoints > 1000 && bm.aiPlayer.MonsterCount > 0)
                {
                    canShowEffectBtn = true;
                    effectLabel = "TRẢ 1000 LP (DIỆT 1 QUÁI)";
                }
                else if (selected.Atk == 0 && bm.humanPlayer.lifePoints > 100)
                {
                    canShowEffectBtn = true;
                    effectLabel = "HIẾN LP CÒN 100 (+ATK/DEF)";
                }
            }
        }

        if (activateEffectButton != null)
        {
            activateEffectButton.gameObject.SetActive(canShowEffectBtn);
            if (activateEffectButtonText != null) activateEffectButtonText.text = effectLabel;
        }

        directAttackButton.gameObject.SetActive(selected.CanAttack && bm.duel.turnCount > 1 && bm.aiPlayer.MonsterCount == 0);
        cancelSelectButton.gameObject.SetActive(true);
    }

    void OnClickActivateFieldMonsterEffect()
    {
        BattleManager bm = BattleManager.Instance;
        if (bm == null || bm.duel == null || bm.duel.Current != bm.humanPlayer) return;
        if (bm.currentState != BattleManager.TurnState.MainPhase) return;
        if (selectedAttackerZone < 0 || selectedAttackerZone >= Player.ZoneCount) return;

        MonsterCard selected = bm.humanPlayer.monsterZones[selectedAttackerZone];
        if (selected == null || selected.IsFaceDown || selected.data == null) return;

        if (selected.data.monsterEffect == MonsterEffectType.DivineObelisk)
        {
            if (selected.hasAttacked)
            {
                bm.SetStatus($"[{selected.data.cardName}] đã tấn công hoặc đã dùng hiệu ứng trong lượt này!");
                return;
            }
            if (bm.aiPlayer.MonsterCount == 0)
            {
                bm.SetStatus("Đối phương chưa có quái thú nào trên sân để phá hủy!");
                return;
            }
            if (bm.humanPlayer.MonsterCount < 2)
            {
                bm.SetStatus($"Cần ít nhất 2 quái thú trên sân của bạn để hiến tế cho [{selected.data.cardName}]!");
                return;
            }

            isSelectingEffectTributes = true;
            pendingEffectSourceZone = selectedAttackerZone;
            requiredTributeCount = 2;
            chosenTributeZones.Clear();
            bm.SetStatus($"[{selected.data.cardName}] Chọn 2 quái thú của bạn trên sân để hiến tế (phá hủy toàn bộ quái đối thủ)!");
            return;
        }

        if (selected.data.monsterEffect == MonsterEffectType.DivineRa)
        {
            if (bm.humanPlayer.lifePoints > 1000 && bm.aiPlayer.MonsterCount > 0)
            {
                if (!bm.aiPlayer.Monsters.Any(m => !m.CannotBeTargetedByEffects))
                {
                    bm.SetStatus("Đối phương chỉ có quái thú không thể bị chọn làm mục tiêu (Obelisk)!");
                    return;
                }

                isSelectingEffectEnemyTarget = true;
                pendingEffectSourceZone = selectedAttackerZone;
                bm.SetStatus($"[{selected.data.cardName}] Chọn 1 quái thú trên sân đối phương để trả 1000 LP và phá hủy!");
                return;
            }
            else if (selected.Atk == 0 && bm.humanPlayer.lifePoints > 100)
            {
                int srcZone = selectedAttackerZone;
                ClearSelectionState();
                bm.PlayerActivateMonsterFieldEffect(srcZone, -1, null);
                return;
            }
        }
    }

    void RefreshGraveyardCounters(BattleManager bm)
    {
        if (playerGyText != null) playerGyText.text = $"MỘ BÀI ({bm.playerGraveyard.Count})";
        if (aiGyText != null) aiGyText.text = bm.IsMultiplayer ? $"MỘ ĐỐI THỦ ({bm.aiGraveyard.Count})" : $"MỘ AI ({bm.aiGraveyard.Count})";

        if (playerDeckHandInfoText != null)
        {
            playerDeckHandInfoText.text = $"DECK: {bm.LocalDeckCount} | TAY: {bm.playerHand.Count}";
        }
        if (aiDeckHandInfoText != null)
        {
            aiDeckHandInfoText.text = $"DECK: {bm.OpponentDeckCount} | TAY: {bm.OpponentHandCount}";
        }
    }

    void BeginSummonFlow(int handIndex, BattlePosition pos, int preferredZone = -1)
    {
        BattleManager bm = BattleManager.Instance;
        CardData card = bm.playerHand[handIndex];

        if (card.IsDivineGod && pos != BattlePosition.FaceUpAttack)
        {
            bm.SetStatus($"[{card.cardName}] chỉ được Triệu hồi Thường ở Tư thế Tấn công ngửa (không thể Đặt úp)!");
            return;
        }

        int needTributes = Duel.RequiredTributes(card);

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

        if (isSelectingEffectTributes)
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
                int srcZone = pendingEffectSourceZone;
                List<int> tributes = new List<int>(chosenTributeZones);
                ClearSelectionState();
                bm.PlayerActivateMonsterFieldEffect(srcZone, -1, tributes);
            }
            else
            {
                bm.SetStatus($"Đã chọn {chosenTributeZones.Count}/{requiredTributeCount} quái hiến tế cho hiệu ứng Obelisk...");
            }
            return;
        }

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
        if (bm == null || bm.duel == null) return;

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
        if (isSelectingTributes || isSelectingEffectTributes || isSelectingEffectEnemyTarget) return;

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

        if (isSelectingEffectEnemyTarget)
        {
            MonsterCard target = bm.aiPlayer.monsterZones[aiZoneIndex];
            if (target == null) return;
            if (target.CannotBeTargetedByEffects)
            {
                bm.SetStatus($"Không thể chọn [{target.data.cardName}] làm mục tiêu của hiệu ứng!");
                return;
            }
            int srcZone = pendingEffectSourceZone;
            ClearSelectionState();
            bm.PlayerActivateMonsterFieldEffect(srcZone, aiZoneIndex, null);
            return;
        }

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
        isSelectingEffectTributes = false;
        isSelectingEffectEnemyTarget = false;
        pendingEffectSourceZone = -1;
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
            string statStr = fieldInstance != null
                ? $"{fieldInstance.Atk}/{fieldInstance.Def}"
                : data.StatDisplay;
            inspectorStatsText.text = $"[{data.CardTag}] LV{data.level} | {statStr}";
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

    Sprite LoadCardBackSprite()
    {
        string[] candidatePaths =
        {
            "Cards/Card_Back",
            "Cards/card_back",
            "Cards/CardBack",
            "Card_Back",
            "CardBack"
        };

        foreach (string path in candidatePaths)
        {
            Sprite sp = Resources.Load<Sprite>(path);
            if (sp != null) return sp;

            Texture2D tex = Resources.Load<Texture2D>(path);
            if (tex != null)
            {
                return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            }
        }

        return CreateProceduralCardBack();
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