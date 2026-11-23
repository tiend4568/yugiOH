using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GodSummonVfxController : MonoBehaviour
{
    public static GodSummonVfxController Instance { get; private set; }

    [Header("Cấu hình chung Triệu Hồi 3 Thần Ai Cập")]
    [Tooltip("Tổng thời lượng toàn bộ chuỗi hiệu ứng triệu hồi (giây).")]
    public float totalDuration = 2.45f;

    [Tooltip("Hệ số cường độ rung bàn đấu khi Thần giáng thế.")]
    public float shakeMultiplier = 1f;

    [Tooltip("Phát âm thanh tổng hợp (Synthesized SFX) đặc trưng cho từng Vị Thần.")]
    public bool playDivineSynthesizedAudio = true;

    private Canvas vfxCanvas;
    private RectTransform rootRT;
    private RectTransform boardShakeTarget;
    private Vector2 boardShakeRestPos;

    private Image backdropImage;
    private Image flashOverlayImage;

    private GodMeshLayer backAlphaLayer;
    private GodMeshLayer backAdditiveLayer;
    private GodSpriteLayer frontSpriteLayer;

    // Khung trưng bày lá bài Thần Ai Cập ở giữa màn hình rồi bay xuống đúng ô bài
    private RectTransform cardShowcaseRT;
    private Image cardGlowFrameImage;
    private Image cardArtImage;

    // Bảng danh hiệu Vị Thần Ai Cập
    private RectTransform bannerRT;
    private Image bannerBgImage;
    private Image bannerTopAccent;
    private Image bannerBottomAccent;
    private TextMeshProUGUI bannerTitleText;
    private TextMeshProUGUI bannerSubText;

    private Texture2D atlasTex;
    private Material additiveMat;

    private AudioSource divineAudioSource;
    private AudioClip obeliskClip;
    private AudioClip sliferClip;
    private AudioClip raClip;

    private bool isInitialized = false;

    // ==================== TRẠNG THÁI CHẠY HIỆU ỨNG ====================

    private bool isPlaying = false;
    private MonsterEffectType activeGod = MonsterEffectType.None;
    private float elapsed = 0f;
    private float duration = 2.45f;
    private float climaxTime = 0.58f;
    private float descendStartTime = 1.50f;
    private float landTime = 1.92f;
    private bool climaxTriggered = false;
    private bool landTriggered = false;
    private Vector2 targetZoneLocalPos = Vector2.zero;
    private float lightningRefreshTimer = 0f;

    private float shakeTimer = 0f;
    private float shakeDuration = 0.35f;
    private float shakeStrength = 0f;

    // ==================== POOLED GEOMETRY & PARTICLES ====================

    private struct CrackBranch
    {
        public Vector2[] points;
        public float baseWidth;
        public float delay;
    }

    private struct LightningBolt
    {
        public bool active;
        public Vector2[] points;
        public float age;
        public float lifetime;
        public float coreWidth;
        public Color outerColor;
        public Color coreColor;
    }

    private struct RockDebris
    {
        public bool active;
        public Vector2 pos;
        public Vector2 vel;
        public float rot;
        public float rotSpeed;
        public float size;
        public int sides;
        public float age;
        public float lifetime;
        public Color darkColor;
        public Color rimColor;
    }

    private struct GodRing
    {
        public bool active;
        public Vector2 pos;
        public float age;
        public float lifetime;
        public float startR;
        public float endR;
        public float thickness;
        public Color startCol;
        public Color endCol;
        public int segments;
    }

    private struct GodParticle
    {
        public bool active;
        public Vector2 pos;
        public Vector2 vel;
        public float drag;
        public float gravityY;
        public float age;
        public float lifetime;
        public float startSize;
        public float endSize;
        public float aspectX;
        public float rot;
        public float rotSpeed;
        public bool alignToVel;
        public int tileIndex; // 0 = soft glow, 1 = swirling flame, 2 = elongated spark, 3 = 4-pt star
        public Color startCol;
        public Color endCol;
    }

    private readonly List<CrackBranch> activeCracks = new List<CrackBranch>(24);
    private readonly List<LightningBolt> boltPool = new List<LightningBolt>(28);
    private readonly List<RockDebris> debrisPool = new List<RockDebris>(96);
    private readonly List<GodRing> ringPool = new List<GodRing>(24);
    private readonly List<GodParticle> particlePool = new List<GodParticle>(320);

    // ==================== BẢNG MÀU CHUẨN 3 VỊ THẦN ====================

    // 1. OBELISK: Xanh Lapis / Cobalt / Điện Cyan / Đá đen
    private static readonly Color ObeliskBackdrop = new Color(0.015f, 0.045f, 0.12f, 0.80f);
    private static readonly Color ObeliskBlue = new Color(0.10f, 0.38f, 0.96f, 1f);
    private static readonly Color ObeliskCyan = new Color(0.24f, 0.82f, 1.00f, 1f);
    private static readonly Color ObeliskCore = new Color(0.88f, 0.98f, 1.00f, 1f);
    private static readonly Color ObeliskStone = new Color(0.08f, 0.14f, 0.26f, 0.96f);

    // 2. SLIFER: Đỏ Thẫm Crimson / Bão Đỏ / Sấm Vàng-Trắng
    private static readonly Color SliferBackdrop = new Color(0.11f, 0.012f, 0.024f, 0.82f);
    private static readonly Color SliferCrimson = new Color(0.88f, 0.07f, 0.14f, 1f);
    private static readonly Color SliferScarlet = new Color(1.00f, 0.24f, 0.22f, 1f);
    private static readonly Color SliferBoltGold = new Color(1.00f, 0.88f, 0.26f, 1f);
    private static readonly Color SliferBoltWhite = new Color(1.00f, 0.99f, 0.92f, 1f);

    // 3. RA: Mặt Trời Trắng-Vàng / Hoàng Kim / Lửa Phượng Hoàng Cam-Vàng
    private static readonly Color RaBackdrop = new Color(0.12f, 0.065f, 0.008f, 0.80f);
    private static readonly Color RaSunWhite = new Color(1.00f, 0.99f, 0.92f, 1f);
    private static readonly Color RaRoyalGold = new Color(1.00f, 0.85f, 0.16f, 1f);
    private static readonly Color RaPhoenixOrange = new Color(1.00f, 0.52f, 0.06f, 1f);
    private static readonly Color RaDeepAmber = new Color(0.82f, 0.34f, 0.02f, 1f);

    public bool IsPlaying => isPlaying;
    public MonsterEffectType ActiveGodEffect => activeGod;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (atlasTex != null) Destroy(atlasTex);
        if (additiveMat != null) Destroy(additiveMat);
        if (obeliskClip != null) Destroy(obeliskClip);
        if (sliferClip != null) Destroy(sliferClip);
        if (raClip != null) Destroy(raClip);
    }

    public static GodSummonVfxController EnsureCreated(RectTransform shakeTarget = null)
    {
        if (Instance != null && Instance.isInitialized)
        {
            if (shakeTarget != null)
            {
                Instance.boardShakeTarget = shakeTarget;
                Instance.boardShakeRestPos = shakeTarget.anchoredPosition;
            }
            return Instance;
        }

        GameObject go = new GameObject("GodSummonVfxCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GodSummonVfxController));
        GodSummonVfxController ctrl = go.GetComponent<GodSummonVfxController>();
        ctrl.Initialize(shakeTarget);
        return ctrl;
    }

    private void Initialize(RectTransform shakeTarget)
    {
        if (isInitialized) return;
        isInitialized = true;

        boardShakeTarget = shakeTarget;
        boardShakeRestPos = shakeTarget != null ? shakeTarget.anchoredPosition : Vector2.zero;

        vfxCanvas = GetComponent<Canvas>();
        vfxCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        vfxCanvas.sortingOrder = 22; // Nằm trên bàn đấu (10) nhưng không chặn bất kỳ tương tác chuột nào

        CanvasScaler scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        rootRT = GetComponent<RectTransform>();

        atlasTex = CreateProceduralAtlas();
        Shader addShader = Resources.Load<Shader>("BattleVfxAdditive");
        if (addShader != null)
        {
            additiveMat = new Material(addShader);
        }

        BuildVisualHierarchy();
        SetupAudio();

        // Pre-allocate pools
        for (int i = 0; i < 24; i++) boltPool.Add(default);
        for (int i = 0; i < 72; i++) debrisPool.Add(default);
        for (int i = 0; i < 20; i++) ringPool.Add(default);
        for (int i = 0; i < 260; i++) particlePool.Add(default);

        SetVisualsActive(false);
    }

    private void BuildVisualHierarchy()
    {
        // 1. Nền tối tạo bầu không khí Thần Giáng Thế
        GameObject bgObj = CreateFullRectObj("DivineBackdrop", transform);
        backdropImage = bgObj.AddComponent<Image>();
        backdropImage.color = Color.clear;
        backdropImage.raycastTarget = false;

        // 2. Lớp hình học Alpha phía sau lá bài (Khe nứt đá tối, mảnh đá, cánh phượng hoàng nền)
        backAlphaLayer = CreateMeshLayer<GodMeshLayer>("BackAlphaLayer", null);
        backAlphaLayer.onPopulate = PopulateBackAlphaMesh;

        // 3. Lớp năng lượng Additive phía sau lá bài (Tia nắng God Rays, Vòng cổ ngữ Ra, Cột năng lượng Obelisk, Sét Slifer)
        backAdditiveLayer = CreateMeshLayer<GodMeshLayer>("BackAdditiveLayer", additiveMat);
        backAdditiveLayer.onPopulate = PopulateBackAdditiveMesh;

        // 4. Khung hiển thị lá bài Thần Ai Cập cỡ lớn
        GameObject cardRoot = new GameObject("GodCardShowcase", typeof(RectTransform));
        cardRoot.transform.SetParent(transform, false);
        cardShowcaseRT = cardRoot.GetComponent<RectTransform>();
        cardShowcaseRT.anchorMin = new Vector2(0.5f, 0.5f);
        cardShowcaseRT.anchorMax = new Vector2(0.5f, 0.5f);
        cardShowcaseRT.sizeDelta = new Vector2(284f, 404f);

        GameObject glowObj = CreateFullRectObj("CardGlowFrame", cardRoot.transform);
        RectTransform glowRT = glowObj.GetComponent<RectTransform>();
        glowRT.offsetMin = new Vector2(-14f, -14f);
        glowRT.offsetMax = new Vector2(14f, 14f);
        cardGlowFrameImage = glowObj.AddComponent<Image>();
        cardGlowFrameImage.raycastTarget = false;
        if (additiveMat != null) cardGlowFrameImage.material = additiveMat;

        GameObject artObj = CreateFullRectObj("CardArtwork", cardRoot.transform);
        cardArtImage = artObj.AddComponent<Image>();
        cardArtImage.preserveAspect = true;
        cardArtImage.raycastTarget = false;

        // 5. Lớp hạt sáng & Quả Cầu Ánh Sáng / Tia lửa phía trước lá bài
        frontSpriteLayer = CreateMeshLayer<GodSpriteLayer>("FrontSpriteLayer", additiveMat);
        frontSpriteLayer.SetAtlas(atlasTex);
        frontSpriteLayer.onPopulate = PopulateFrontSpriteMesh;

        // 6. Bảng danh hiệu Thần Ai Cập sắc nét phía dưới lá bài
        GameObject banObj = new GameObject("DivineBanner", typeof(RectTransform), typeof(Image));
        banObj.transform.SetParent(transform, false);
        bannerRT = banObj.GetComponent<RectTransform>();
        bannerRT.anchorMin = new Vector2(0.5f, 0.5f);
        bannerRT.anchorMax = new Vector2(0.5f, 0.5f);
        bannerRT.anchoredPosition = new Vector2(0f, -268f);
        bannerRT.sizeDelta = new Vector2(820f, 102f);

        bannerBgImage = banObj.GetComponent<Image>();
        bannerBgImage.color = new Color(0.03f, 0.03f, 0.06f, 0.88f);
        bannerBgImage.raycastTarget = false;

        bannerTopAccent = CreateAccentBar(banObj.transform, "TopAccent", new Vector2(0f, 49f), new Vector2(820f, 4f));
        bannerBottomAccent = CreateAccentBar(banObj.transform, "BottomAccent", new Vector2(0f, -49f), new Vector2(820f, 4f));

        GameObject titleObj = new GameObject("TitleText", typeof(RectTransform), typeof(TextMeshProUGUI));
        titleObj.transform.SetParent(banObj.transform, false);
        RectTransform tRT = titleObj.GetComponent<RectTransform>();
        tRT.anchorMin = new Vector2(0.5f, 0.5f);
        tRT.anchorMax = new Vector2(0.5f, 0.5f);
        tRT.anchoredPosition = new Vector2(0f, 14f);
        tRT.sizeDelta = new Vector2(790f, 46f);
        bannerTitleText = titleObj.GetComponent<TextMeshProUGUI>();
        bannerTitleText.fontSize = 34f;
        bannerTitleText.fontStyle = FontStyles.Bold;
        bannerTitleText.alignment = TextAlignmentOptions.Center;
        bannerTitleText.enableWordWrapping = false;
        bannerTitleText.raycastTarget = false;

        GameObject subObj = new GameObject("SubText", typeof(RectTransform), typeof(TextMeshProUGUI));
        subObj.transform.SetParent(banObj.transform, false);
        RectTransform sRT = subObj.GetComponent<RectTransform>();
        sRT.anchorMin = new Vector2(0.5f, 0.5f);
        sRT.anchorMax = new Vector2(0.5f, 0.5f);
        sRT.anchoredPosition = new Vector2(0f, -22f);
        sRT.sizeDelta = new Vector2(790f, 30f);
        bannerSubText = subObj.GetComponent<TextMeshProUGUI>();
        bannerSubText.fontSize = 18f;
        bannerSubText.fontStyle = FontStyles.Bold;
        bannerSubText.alignment = TextAlignmentOptions.Center;
        bannerSubText.enableWordWrapping = false;
        bannerSubText.raycastTarget = false;

        // 7. Chớp toàn màn hình tại thời điểm Vị Thần bùng nổ xuất hiện
        GameObject flashObj = CreateFullRectObj("DivineFlashOverlay", transform);
        flashOverlayImage = flashObj.AddComponent<Image>();
        flashOverlayImage.color = Color.clear;
        flashOverlayImage.raycastTarget = false;
        if (additiveMat != null) flashOverlayImage.material = additiveMat;
    }

    private GameObject CreateFullRectObj(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return obj;
    }

    private Image CreateAccentBar(Transform parent, string name, Vector2 anchoredPos, Vector2 size)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image));
        obj.transform.SetParent(parent, false);
        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;
        Image img = obj.GetComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    private T CreateMeshLayer<T>(string name, Material mat) where T : MaskableGraphic
    {
        GameObject obj = CreateFullRectObj(name, transform);
        obj.AddComponent<CanvasRenderer>();
        T graphic = obj.AddComponent<T>();
        graphic.raycastTarget = false;
        graphic.maskable = false;
        if (mat != null) graphic.material = mat;
        return graphic;
    }

    private void SetVisualsActive(bool active)
    {
        if (backdropImage != null) backdropImage.gameObject.SetActive(active);
        if (backAlphaLayer != null) backAlphaLayer.gameObject.SetActive(active);
        if (backAdditiveLayer != null) backAdditiveLayer.gameObject.SetActive(active);
        if (cardShowcaseRT != null) cardShowcaseRT.gameObject.SetActive(active);
        if (frontSpriteLayer != null) frontSpriteLayer.gameObject.SetActive(active);
        if (bannerRT != null) bannerRT.gameObject.SetActive(active);
        if (flashOverlayImage != null) flashOverlayImage.gameObject.SetActive(active);
    }

    // ==================== API CÔNG KHAI ====================

    /// <summary>
    /// Kích hoạt hiệu ứng triệu hồi riêng biệt theo tính chất của Obelisk, Slifer hoặc Ra.
    /// </summary>
    public void PlayGodSummon(MonsterEffectType godEffect, Sprite cardSprite, RectTransform targetSlotRT)
    {
        if (!isInitialized) return;
        if (godEffect != MonsterEffectType.DivineObelisk &&
            godEffect != MonsterEffectType.DivineSlifer &&
            godEffect != MonsterEffectType.DivineRa)
        {
            return;
        }

        ClearPools();
        activeGod = godEffect;
        isPlaying = true;
        elapsed = 0f;
        climaxTriggered = false;
        landTriggered = false;
        lightningRefreshTimer = 0f;

        targetZoneLocalPos = targetSlotRT != null ? ConvertToOverlayLocal(targetSlotRT) : Vector2.zero;

        if (cardArtImage != null)
        {
            cardArtImage.sprite = cardSprite;
            cardArtImage.color = Color.white;
        }

        ConfigureGodTheme(godEffect);
        SetVisualsActive(true);
        SpawnPhase1BuildUp(godEffect);
        PlayGodAudio(godEffect);
        MarkLayersDirty();
    }

    public void StopImmediately()
    {
        isPlaying = false;
        activeGod = MonsterEffectType.None;
        ClearPools();
        SetVisualsActive(false);
        if (boardShakeTarget != null) boardShakeTarget.anchoredPosition = boardShakeRestPos;
        shakeTimer = 0f;
    }

    private Vector2 ConvertToOverlayLocal(RectTransform targetRT)
    {
        if (targetRT == null || rootRT == null) return Vector2.zero;
        Canvas srcCanvas = targetRT.GetComponentInParent<Canvas>();
        Camera srcCam = (srcCanvas != null && srcCanvas.renderMode != RenderMode.ScreenSpaceOverlay) ? srcCanvas.worldCamera : null;
        Vector3 worldCenter = targetRT.TransformPoint(targetRT.rect.center);
        Vector2 screenPt = RectTransformUtility.WorldToScreenPoint(srcCam, worldCenter);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rootRT, screenPt, null, out Vector2 localPt))
        {
            return localPt;
        }
        return Vector2.zero;
    }

    private void ConfigureGodTheme(MonsterEffectType god)
    {
        if (god == MonsterEffectType.DivineObelisk)
        {
            duration = Mathf.Max(2.1f, totalDuration);
            climaxTime = 0.46f;
            descendStartTime = 1.48f;
            landTime = 1.88f;

            cardGlowFrameImage.color = new Color(ObeliskCyan.r, ObeliskCyan.g, ObeliskCyan.b, 0.85f);
            bannerBgImage.color = new Color(0.03f, 0.08f, 0.22f, 0.92f);
            bannerTopAccent.color = ObeliskCyan;
            bannerBottomAccent.color = ObeliskBlue;
            bannerTitleText.text = "THẦN LÍNH KHỔNG LỒ OBELISK";
            bannerTitleText.color = ObeliskCore;
            bannerSubText.text = "DIVINE-BEAST  •  ĐẠI ĐỊA & QUYỀN NĂNG HỦY DIỆT TUYỆT ĐỐI";
            bannerSubText.color = ObeliskCyan;
        }
        else if (god == MonsterEffectType.DivineSlifer)
        {
            duration = Mathf.Max(2.15f, totalDuration);
            climaxTime = 0.52f;
            descendStartTime = 1.52f;
            landTime = 1.92f;

            cardGlowFrameImage.color = new Color(SliferScarlet.r, SliferScarlet.g, SliferScarlet.b, 0.90f);
            bannerBgImage.color = new Color(0.20f, 0.02f, 0.04f, 0.92f);
            bannerTopAccent.color = SliferBoltGold;
            bannerBottomAccent.color = SliferCrimson;
            bannerTitleText.text = "RỒNG THẦN BẦU TRỜI SLIFER";
            bannerTitleText.color = SliferBoltWhite;
            bannerSubText.text = "DIVINE-BEAST  •  BẦU TRỜI ĐỎ THẪM & THIÊN LÔI VÔ TẬN";
            bannerSubText.color = SliferBoltGold;
        }
        else // DivineRa
        {
            duration = Mathf.Max(2.30f, totalDuration + 0.12f);
            climaxTime = 0.68f;
            descendStartTime = 1.66f;
            landTime = 2.06f;

            cardGlowFrameImage.color = new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, 0.95f);
            bannerBgImage.color = new Color(0.20f, 0.11f, 0.01f, 0.92f);
            bannerTopAccent.color = RaSunWhite;
            bannerBottomAccent.color = RaRoyalGold;
            bannerTitleText.text = "RỒNG CÁNH THẦN CỦA RA";
            bannerTitleText.color = RaSunWhite;
            bannerSubText.text = "DIVINE-BEAST  •  MẶT TRỜI TỐI CAO & PHƯỢNG HOÀNG BẤT TỬ";
            bannerSubText.color = RaRoyalGold;
        }
    }

    // ==================== PHASE 1: TỤ NĂNG LƯỢNG BAN ĐẦU ====================

    private void SpawnPhase1BuildUp(MonsterEffectType god)
    {
        if (god == MonsterEffectType.DivineObelisk)
        {
            // Mảnh đá đen xanh lơ lửng rung nhẹ trước khi Obelisk giáng xuống nghiền nát mặt đất
            for (int i = 0; i < 18; i++)
            {
                float x = UnityEngine.Random.Range(-520f, 520f);
                float y = UnityEngine.Random.Range(-320f, -120f);
                SpawnDebris(
                    pos: new Vector2(x, y),
                    vel: new Vector2(UnityEngine.Random.Range(-25f, 25f), UnityEngine.Random.Range(90f, 220f)),
                    size: UnityEngine.Random.Range(14f, 34f),
                    sides: UnityEngine.Random.Range(4, 7),
                    lifetime: UnityEngine.Random.Range(0.55f, 0.95f),
                    darkCol: ObeliskStone,
                    rimCol: ObeliskCyan
                );
            }

            // Các tia năng lượng xanh tụ về tâm
            for (int i = 0; i < 24; i++)
            {
                float ang = (i / 24f) * Mathf.PI * 2f;
                float dist = UnityEngine.Random.Range(240f, 440f);
                Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                SpawnParticle(
                    pos: dir * dist,
                    vel: -dir * (dist / 0.44f),
                    drag: 0.4f,
                    gravityY: 0f,
                    lifetime: 0.44f,
                    startSize: UnityEngine.Random.Range(14f, 26f),
                    endSize: 4f,
                    aspectX: 2.8f,
                    rot: ang * Mathf.Rad2Deg,
                    rotSpeed: 0f,
                    alignToVel: true,
                    tileIndex: 2,
                    startCol: ObeliskCyan,
                    endCol: ObeliskCore
                );
            }
        }
        else if (god == MonsterEffectType.DivineSlifer)
        {
            // Tia sét tiền báo giáng dọc từ đỉnh bầu trời đỏ thẫm
            SpawnForkedLightning(new Vector2(-360f, 540f), new Vector2(-120f, -40f), 10f, 0.36f, SliferCrimson, SliferBoltGold);
            SpawnForkedLightning(new Vector2(380f, 540f), new Vector2(140f, -20f), 10f, 0.36f, SliferCrimson, SliferBoltGold);

            // Xoáy tia lửa đỏ-vàng cuộn vào tâm bão
            for (int i = 0; i < 32; i++)
            {
                float ang = (i / 32f) * Mathf.PI * 2f;
                float dist = UnityEngine.Random.Range(220f, 480f);
                Vector2 radial = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                Vector2 tangent = new Vector2(-radial.y, radial.x);
                SpawnParticle(
                    pos: radial * dist,
                    vel: (-radial * 420f + tangent * 360f),
                    drag: 1.2f,
                    gravityY: 0f,
                    lifetime: UnityEngine.Random.Range(0.45f, 0.72f),
                    startSize: UnityEngine.Random.Range(18f, 34f),
                    endSize: 0f,
                    aspectX: 2.2f,
                    rot: 0f,
                    rotSpeed: 0f,
                    alignToVel: true,
                    tileIndex: 2,
                    startCol: (i % 2 == 0) ? SliferScarlet : SliferBoltGold,
                    endCol: new Color(SliferCrimson.r, SliferCrimson.g, SliferCrimson.b, 0f)
                );
            }
        }
        else // DivineRa
        {
            // Quả Cầu Ánh Sáng (Sphere Mode) hút các đốm lửa mặt trời từ khắp màn hình vào tâm
            SpawnRing(Vector2.zero, 280f, 24f, 14f, 0.62f, new Color(1f, 0.85f, 0.16f, 0.2f), new Color(1f, 0.99f, 0.88f, 0.95f), 40);
            for (int i = 0; i < 38; i++)
            {
                float ang = (i / 38f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.12f, 0.12f);
                float dist = UnityEngine.Random.Range(260f, 560f);
                Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                Vector2 perp = new Vector2(-dir.y, dir.x);
                SpawnParticle(
                    pos: dir * dist,
                    vel: -dir * (dist / 0.62f) + perp * UnityEngine.Random.Range(-80f, 80f),
                    drag: 0.3f,
                    gravityY: 0f,
                    lifetime: 0.66f,
                    startSize: UnityEngine.Random.Range(14f, 28f),
                    endSize: 6f,
                    aspectX: 1.5f,
                    rot: UnityEngine.Random.Range(0f, 180f),
                    rotSpeed: 180f,
                    alignToVel: false,
                    tileIndex: (i % 2 == 0) ? 3 : 0,
                    startCol: (i % 3 == 0) ? RaSunWhite : RaRoyalGold,
                    endCol: RaPhoenixOrange
                );
            }
        }
    }

    // ==================== PHASE 2: ĐỈNH ĐIỂM THẦN GIÁNG THẾ ====================

    private void TriggerClimax(MonsterEffectType god)
    {
        climaxTriggered = true;

        if (god == MonsterEffectType.DivineObelisk)
        {
            // Địa chấn cực mạnh + 10 đường nứt mặt đất phát sáng xanh cyan tỏa ra từ tâm
            TriggerShake(24f, 0.48f);
            GenerateSeismicCracks(Vector2.zero, count: 10, maxRadius: 720f);

            // 2 vòng sóng xung kích xanh Cobalt khổng lồ
            SpawnRing(Vector2.zero, 30f, 560f, 34f, 0.52f, ObeliskCore, new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, 0f), 36);
            SpawnRing(Vector2.zero, 20f, 390f, 20f, 0.42f, ObeliskCyan, new Color(ObeliskCyan.r, ObeliskCyan.g, ObeliskCyan.b, 0f), 32);

            // 36 khối đá nguyên khối vỡ tung từ tâm địa chấn
            for (int i = 0; i < 36; i++)
            {
                float ang = (i / 36f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.15f, 0.15f);
                float spd = UnityEngine.Random.Range(260f, 760f);
                SpawnDebris(
                    pos: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * UnityEngine.Random.Range(15f, 60f),
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd + new Vector2(0f, UnityEngine.Random.Range(40f, 160f)),
                    size: UnityEngine.Random.Range(16f, 44f),
                    sides: UnityEngine.Random.Range(3, 6),
                    lifetime: UnityEngine.Random.Range(0.75f, 1.25f),
                    darkCol: ObeliskStone,
                    rimCol: ObeliskCyan
                );
            }

            // Tia năng lượng Soul Energy xanh trắng bắn tỏa tròn
            for (int i = 0; i < 42; i++)
            {
                float ang = (i / 42f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.1f, 0.1f);
                float spd = UnityEngine.Random.Range(320f, 920f);
                SpawnParticle(
                    pos: Vector2.zero,
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd,
                    drag: 3.4f,
                    gravityY: 0f,
                    lifetime: UnityEngine.Random.Range(0.38f, 0.72f),
                    startSize: UnityEngine.Random.Range(22f, 42f),
                    endSize: 0f,
                    aspectX: 2.8f,
                    rot: ang * Mathf.Rad2Deg,
                    rotSpeed: 0f,
                    alignToVel: true,
                    tileIndex: 2,
                    startCol: (i % 3 == 0) ? ObeliskCore : ObeliskCyan,
                    endCol: new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, 0f)
                );
            }
        }
        else if (god == MonsterEffectType.DivineSlifer)
        {
            // Sấm sét cực đại giáng xuống từ bầu trời đỏ thẫm
            TriggerShake(20f, 0.42f);

            // 5 tia Thiên Lôi phân nhánh giáng thẳng xuống tâm & hai bên
            SpawnForkedLightning(new Vector2(0f, 560f), new Vector2(0f, -20f), 19f, 0.50f, SliferCrimson, SliferBoltWhite);
            SpawnForkedLightning(new Vector2(-240f, 560f), new Vector2(-45f, 25f), 13f, 0.45f, SliferCrimson, SliferBoltGold);
            SpawnForkedLightning(new Vector2(240f, 560f), new Vector2(45f, 15f), 13f, 0.45f, SliferCrimson, SliferBoltGold);
            SpawnForkedLightning(new Vector2(-520f, 540f), new Vector2(-210f, -180f), 9f, 0.40f, SliferCrimson, SliferBoltGold);
            SpawnForkedLightning(new Vector2(520f, 540f), new Vector2(210f, -180f), 9f, 0.40f, SliferCrimson, SliferBoltGold);

            // Vòng sóng bão sét đỏ-vàng
            SpawnRing(Vector2.zero, 24f, 540f, 28f, 0.48f, SliferBoltGold, new Color(SliferCrimson.r, SliferCrimson.g, SliferCrimson.b, 0f), 32);
            SpawnRing(Vector2.zero, 18f, 380f, 16f, 0.36f, SliferBoltWhite, new Color(SliferScarlet.r, SliferScarlet.g, SliferScarlet.b, 0f), 28);

            // 48 tia sét / tàn lửa đỏ-vàng văng mạnh từ tâm bão
            for (int i = 0; i < 48; i++)
            {
                float ang = (i / 48f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.12f, 0.12f);
                float spd = UnityEngine.Random.Range(300f, 940f);
                bool goldSpark = (i % 2 == 0);
                SpawnParticle(
                    pos: Vector2.zero,
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd,
                    drag: 3.6f,
                    gravityY: 0f,
                    lifetime: UnityEngine.Random.Range(0.35f, 0.78f),
                    startSize: UnityEngine.Random.Range(18f, 38f),
                    endSize: 0f,
                    aspectX: goldSpark ? 3.1f : 2.2f,
                    rot: ang * Mathf.Rad2Deg,
                    rotSpeed: 0f,
                    alignToVel: true,
                    tileIndex: 2,
                    startCol: goldSpark ? SliferBoltGold : SliferScarlet,
                    endCol: new Color(SliferCrimson.r, SliferCrimson.g, SliferCrimson.b, 0f)
                );
            }
        }
        else // DivineRa
        {
            // Siêu tân tinh Mặt Trời (Solar Supernova) nổ tung từ Quả Cầu Ánh Sáng!
            TriggerShake(17f, 0.38f);

            SpawnRing(Vector2.zero, 36f, 680f, 38f, 0.56f, RaSunWhite, new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, 0f), 44);
            SpawnRing(Vector2.zero, 20f, 470f, 22f, 0.44f, RaRoyalGold, new Color(RaDeepAmber.r, RaDeepAmber.g, RaDeepAmber.b, 0f), 36);

            // Vầng chớp ngôi sao Mặt Trời cực lớn
            SpawnParticle(
                pos: Vector2.zero,
                vel: Vector2.zero,
                drag: 0f,
                gravityY: 0f,
                lifetime: 0.46f,
                startSize: 440f,
                endSize: 780f,
                aspectX: 1f,
                rot: 0f,
                rotSpeed: 45f,
                alignToVel: false,
                tileIndex: 3,
                startCol: RaSunWhite,
                endCol: new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, 0f)
            );

            // 54 lông vũ lửa Phượng Hoàng & bụi vàng mặt trời tỏa rộng
            for (int i = 0; i < 54; i++)
            {
                float ang = (i / 54f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.1f, 0.1f);
                float spd = UnityEngine.Random.Range(240f, 820f);
                bool isStar = (i % 3 == 0);
                SpawnParticle(
                    pos: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 28f,
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd + new Vector2(0f, UnityEngine.Random.Range(40f, 150f)),
                    drag: 2.6f,
                    gravityY: 75f, // Tàn lửa phượng hoàng bay bổng lên trời
                    lifetime: UnityEngine.Random.Range(0.55f, 1.15f),
                    startSize: isStar ? UnityEngine.Random.Range(20f, 38f) : UnityEngine.Random.Range(28f, 52f),
                    endSize: 0f,
                    aspectX: isStar ? 1f : 1.7f,
                    rot: UnityEngine.Random.Range(0f, 360f),
                    rotSpeed: UnityEngine.Random.Range(-220f, 220f),
                    alignToVel: !isStar,
                    tileIndex: isStar ? 3 : 1,
                    startCol: isStar ? RaSunWhite : ((i % 2 == 0) ? RaRoyalGold : RaPhoenixOrange),
                    endCol: new Color(RaDeepAmber.r, RaDeepAmber.g, RaDeepAmber.b, 0f)
                );
            }
        }
    }

    // ==================== PHASE 3: GIÁNG XUỐNG ĐÚNG Ô QUÁI THÚ TRÊN BÀN ĐẤU ====================

    private void TriggerZoneLanding(MonsterEffectType god)
    {
        landTriggered = true;
        Vector2 p = targetZoneLocalPos;

        if (god == MonsterEffectType.DivineObelisk)
        {
            TriggerShake(14f, 0.28f);
            SpawnRing(p, 16f, 210f, 20f, 0.36f, ObeliskCore, new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, 0f), 28);
            SpawnRing(p, 12f, 145f, 12f, 0.28f, ObeliskCyan, new Color(ObeliskCyan.r, ObeliskCyan.g, ObeliskCyan.b, 0f), 24);

            for (int i = 0; i < 16; i++)
            {
                float ang = (i / 16f) * Mathf.PI * 2f;
                SpawnDebris(
                    pos: p + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 18f,
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * UnityEngine.Random.Range(140f, 360f),
                    size: UnityEngine.Random.Range(10f, 22f),
                    sides: UnityEngine.Random.Range(3, 6),
                    lifetime: UnityEngine.Random.Range(0.40f, 0.65f),
                    darkCol: ObeliskStone,
                    rimCol: ObeliskCyan
                );
            }
        }
        else if (god == MonsterEffectType.DivineSlifer)
        {
            TriggerShake(14f, 0.28f);
            // Tia Thiên Lôi giáng thẳng từ đỉnh màn hình xuống đúng ô bài của Slifer!
            SpawnForkedLightning(new Vector2(p.x + UnityEngine.Random.Range(-60f, 60f), 560f), p, 15f, 0.42f, SliferCrimson, SliferBoltWhite);
            SpawnRing(p, 14f, 215f, 18f, 0.36f, SliferBoltGold, new Color(SliferCrimson.r, SliferCrimson.g, SliferCrimson.b, 0f), 28);

            for (int i = 0; i < 20; i++)
            {
                float ang = (i / 20f) * Mathf.PI * 2f;
                SpawnParticle(
                    pos: p,
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * UnityEngine.Random.Range(180f, 440f),
                    drag: 4.0f,
                    gravityY: 0f,
                    lifetime: UnityEngine.Random.Range(0.25f, 0.48f),
                    startSize: UnityEngine.Random.Range(16f, 30f),
                    endSize: 0f,
                    aspectX: 2.6f,
                    rot: ang * Mathf.Rad2Deg,
                    rotSpeed: 0f,
                    alignToVel: true,
                    tileIndex: 2,
                    startCol: (i % 2 == 0) ? SliferBoltGold : SliferScarlet,
                    endCol: new Color(SliferCrimson.r, SliferCrimson.g, SliferCrimson.b, 0f)
                );
            }
        }
        else // DivineRa
        {
            TriggerShake(12f, 0.28f);
            SpawnRing(p, 16f, 230f, 22f, 0.40f, RaSunWhite, new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, 0f), 32);
            SpawnParticle(
                pos: p,
                vel: Vector2.zero,
                drag: 0f,
                gravityY: 0f,
                lifetime: 0.36f,
                startSize: 220f,
                endSize: 340f,
                aspectX: 1f,
                rot: 0f,
                rotSpeed: 60f,
                alignToVel: false,
                tileIndex: 3,
                startCol: RaSunWhite,
                endCol: new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, 0f)
            );
            for (int i = 0; i < 24; i++)
            {
                float ang = (i / 24f) * Mathf.PI * 2f;
                SpawnParticle(
                    pos: p + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 16f,
                    vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * UnityEngine.Random.Range(120f, 340f) + new Vector2(0f, 110f),
                    drag: 2.8f,
                    gravityY: 95f,
                    lifetime: UnityEngine.Random.Range(0.35f, 0.65f),
                    startSize: UnityEngine.Random.Range(14f, 28f),
                    endSize: 0f,
                    aspectX: 1.3f,
                    rot: UnityEngine.Random.Range(0f, 360f),
                    rotSpeed: 180f,
                    alignToVel: false,
                    tileIndex: (i % 2 == 0) ? 3 : 1,
                    startCol: (i % 2 == 0) ? RaSunWhite : RaRoyalGold,
                    endCol: new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, 0f)
                );
            }
        }
    }

    // ==================== UPDATE LOOP ====================

    void Update()
    {
        if (!isInitialized) return;

        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        if (dt <= 0f) return;

        UpdateBoardShake(dt);

        if (!isPlaying) return;

        elapsed += dt;

        if (!climaxTriggered && elapsed >= climaxTime)
        {
            TriggerClimax(activeGod);
        }

        if (!landTriggered && elapsed >= landTime)
        {
            TriggerZoneLanding(activeGod);
        }

        // Hiệu ứng phụ liên tục trong giai đoạn tỏa sáng (Climax -> Descend)
        if (climaxTriggered && elapsed < descendStartTime)
        {
            if (activeGod == MonsterEffectType.DivineSlifer)
            {
                lightningRefreshTimer -= dt;
                if (lightningRefreshTimer <= 0f)
                {
                    lightningRefreshTimer = 0.13f;
                    // Các tia sét nhỏ giật liên tục quanh lá bài Slifer
                    float a1 = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    float a2 = a1 + UnityEngine.Random.Range(0.8f, 1.8f);
                    Vector2 p1 = new Vector2(Mathf.Cos(a1) * 165f, Mathf.Sin(a1) * 215f + 25f);
                    Vector2 p2 = new Vector2(Mathf.Cos(a2) * 185f, Mathf.Sin(a2) * 230f + 25f);
                    SpawnForkedLightning(p1, p2, 6f, 0.16f, SliferCrimson, SliferBoltGold, withBranches: false);
                }
            }
            else if (activeGod == MonsterEffectType.DivineRa)
            {
                lightningRefreshTimer -= dt;
                if (lightningRefreshTimer <= 0f)
                {
                    lightningRefreshTimer = 0.05f;
                    // Tàn lửa phượng hoàng vàng lấp lánh bay lên quanh Ra
                    SpawnParticle(
                        pos: new Vector2(UnityEngine.Random.Range(-240f, 240f), UnityEngine.Random.Range(-180f, 120f)),
                        vel: new Vector2(UnityEngine.Random.Range(-45f, 45f), UnityEngine.Random.Range(140f, 280f)),
                        drag: 1.0f,
                        gravityY: 60f,
                        lifetime: UnityEngine.Random.Range(0.45f, 0.78f),
                        startSize: UnityEngine.Random.Range(12f, 24f),
                        endSize: 0f,
                        aspectX: 1f,
                        rot: UnityEngine.Random.Range(0f, 180f),
                        rotSpeed: 140f,
                        alignToVel: false,
                        tileIndex: 3,
                        startCol: RaSunWhite,
                        endCol: new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, 0f)
                    );
                }
            }
        }

        UpdatePools(dt);
        UpdateUiShowcaseElements();
        MarkLayersDirty();

        if (elapsed >= duration)
        {
            isPlaying = false;
            activeGod = MonsterEffectType.None;
            ClearPools();
            SetVisualsActive(false);
        }
    }

    private void UpdateUiShowcaseElements()
    {
        // 1. Nền tối tạo chiều sâu
        Color targetBg = activeGod == MonsterEffectType.DivineObelisk ? ObeliskBackdrop
            : activeGod == MonsterEffectType.DivineSlifer ? SliferBackdrop
            : RaBackdrop;

        float bgAlphaMul;
        if (elapsed < 0.22f)
            bgAlphaMul = EaseOutCubic(elapsed / 0.22f);
        else if (elapsed < descendStartTime)
            bgAlphaMul = 1f;
        else
            bgAlphaMul = 1f - EaseOutCubic(Mathf.Clamp01((elapsed - descendStartTime) / (duration - descendStartTime)));

        backdropImage.color = new Color(targetBg.r, targetBg.g, targetBg.b, targetBg.a * bgAlphaMul);

        // 2. Chớp toàn màn hình tại đỉnh điểm Climax
        Color flashCol = activeGod == MonsterEffectType.DivineObelisk ? ObeliskCore
            : activeGod == MonsterEffectType.DivineSlifer ? SliferBoltWhite
            : RaSunWhite;

        float flashA = 0f;
        if (elapsed >= climaxTime && elapsed < climaxTime + 0.36f)
        {
            float ft = (elapsed - climaxTime) / 0.36f;
            float basePeak = activeGod == MonsterEffectType.DivineRa ? 0.92f : 0.76f;
            flashA = basePeak * Mathf.Pow(1f - ft, 2.2f);
            // Chớp kép kiểu sấm sét cho Slifer
            if (activeGod == MonsterEffectType.DivineSlifer && ft > 0.22f && ft < 0.44f)
            {
                flashA = Mathf.Max(flashA, 0.55f * (1f - Mathf.Abs(ft - 0.33f) / 0.11f));
            }
        }
        else if (elapsed >= landTime && elapsed < landTime + 0.22f)
        {
            float lt = (elapsed - landTime) / 0.22f;
            flashA = 0.28f * (1f - lt) * (1f - lt);
        }
        flashOverlayImage.color = new Color(flashCol.r, flashCol.g, flashCol.b, flashA);

        // 3. Hoạt ảnh lá bài Thần Ai Cập (riêng cho từng Vị Thần)
        Vector2 cardCenterPos = new Vector2(0f, 34f);
        if (elapsed < climaxTime)
        {
            if (activeGod == MonsterEffectType.DivineObelisk)
            {
                // Obelisk giáng mạnh từ trên cao xuống (slam!)
                float t = Mathf.Clamp01((elapsed - 0.24f) / Mathf.Max(0.01f, climaxTime - 0.24f));
                if (t <= 0f)
                {
                    cardShowcaseRT.localScale = Vector3.zero;
                }
                else
                {
                    float slam = t * t * t;
                    cardShowcaseRT.anchoredPosition = Vector2.Lerp(new Vector2(0f, 560f), cardCenterPos, slam);
                    float sc = Mathf.Lerp(1.85f, 1.0f, slam);
                    cardShowcaseRT.localScale = new Vector3(sc, sc, 1f);
                    SetCardAlpha(Mathf.Clamp01(t * 2.5f));
                }
            }
            else
            {
                // Slifer & Ra ẩn trong bão sét / Quả Cầu Ánh Sáng (Sphere Mode) cho tới đúng khoảnh khắc Climax
                cardShowcaseRT.localScale = Vector3.zero;
            }
        }
        else if (elapsed < descendStartTime)
        {
            float t = Mathf.Clamp01((elapsed - climaxTime) / 0.26f);
            float sc = activeGod == MonsterEffectType.DivineObelisk
                ? Mathf.Lerp(1.16f, 1.0f, EaseOutCubic(t))
                : Mathf.Lerp(0.28f, 1.0f, EaseOutBack(t));

            Vector2 offset = Vector2.zero;
            if (activeGod == MonsterEffectType.DivineSlifer && t < 0.85f)
            {
                float jitter = (1f - t) * 10f;
                offset = new Vector2(UnityEngine.Random.Range(-jitter, jitter), UnityEngine.Random.Range(-jitter, jitter));
            }
            else if (activeGod == MonsterEffectType.DivineRa)
            {
                offset = new Vector2(0f, Mathf.Sin((elapsed - climaxTime) * 4.5f) * 9f);
            }

            cardShowcaseRT.anchoredPosition = cardCenterPos + offset;
            cardShowcaseRT.localScale = new Vector3(sc, sc, 1f);
            SetCardAlpha(1f);
        }
        else if (elapsed < landTime)
        {
            // Bay thẳng xuống đúng ô bài của Thần trên bàn đấu!
            float t = Mathf.Clamp01((elapsed - descendStartTime) / Mathf.Max(0.01f, landTime - descendStartTime));
            float eased = t * t * (3f - 2f * t);
            cardShowcaseRT.anchoredPosition = Vector2.Lerp(cardCenterPos, targetZoneLocalPos, eased);
            float sc = Mathf.Lerp(1.0f, 0.38f, eased);
            cardShowcaseRT.localScale = new Vector3(sc, sc, 1f);
            SetCardAlpha(t < 0.72f ? 1f : Mathf.Lerp(1f, 0f, (t - 0.72f) / 0.28f));
        }
        else
        {
            cardShowcaseRT.localScale = Vector3.zero;
        }

        // 4. Bảng danh hiệu Thần Ai Cập
        if (elapsed < climaxTime + 0.04f)
        {
            bannerRT.localScale = Vector3.zero;
        }
        else if (elapsed < descendStartTime)
        {
            float bt = Mathf.Clamp01((elapsed - (climaxTime + 0.04f)) / 0.24f);
            float bsc = EaseOutBack(bt);
            bannerRT.localScale = new Vector3(bsc, bsc, 1f);
        }
        else
        {
            float outT = Mathf.Clamp01((elapsed - descendStartTime) / 0.24f);
            float bsc = Mathf.Lerp(1f, 0f, EaseOutCubic(outT));
            bannerRT.localScale = new Vector3(bsc, bsc, 1f);
        }
    }

    private void SetCardAlpha(float a)
    {
        if (cardArtImage != null)
        {
            Color c = cardArtImage.color;
            c.a = a;
            cardArtImage.color = c;
        }
        if (cardGlowFrameImage != null)
        {
            Color g = cardGlowFrameImage.color;
            g.a = a * 0.90f;
            cardGlowFrameImage.color = g;
        }
    }

    private void UpdatePools(float dt)
    {
        for (int i = 0; i < boltPool.Count; i++)
        {
            LightningBolt b = boltPool[i];
            if (!b.active) continue;
            b.age += dt;
            if (b.age >= b.lifetime) b.active = false;
            boltPool[i] = b;
        }

        for (int i = 0; i < debrisPool.Count; i++)
        {
            RockDebris d = debrisPool[i];
            if (!d.active) continue;
            d.age += dt;
            if (d.age >= d.lifetime) { d.active = false; debrisPool[i] = d; continue; }
            d.vel *= Mathf.Exp(-1.6f * dt);
            d.vel.y -= 320f * dt; // Trọng lực nặng của đá nguyên khối Obelisk
            d.pos += d.vel * dt;
            d.rot += d.rotSpeed * dt;
            debrisPool[i] = d;
        }

        for (int i = 0; i < ringPool.Count; i++)
        {
            GodRing r = ringPool[i];
            if (!r.active) continue;
            r.age += dt;
            if (r.age >= r.lifetime) r.active = false;
            ringPool[i] = r;
        }

        for (int i = 0; i < particlePool.Count; i++)
        {
            GodParticle p = particlePool[i];
            if (!p.active) continue;
            p.age += dt;
            if (p.age >= p.lifetime) { p.active = false; particlePool[i] = p; continue; }
            if (p.drag > 0f) p.vel *= Mathf.Exp(-p.drag * dt);
            p.vel.y += p.gravityY * dt;
            p.pos += p.vel * dt;
            p.rot += p.rotSpeed * dt;
            particlePool[i] = p;
        }
    }

    private void TriggerShake(float amplitude, float dur)
    {
        if (boardShakeTarget == null) return;
        shakeDuration = Mathf.Max(0.05f, dur);
        shakeTimer = shakeDuration;
        shakeStrength = amplitude * shakeMultiplier;
    }

    private void UpdateBoardShake(float dt)
    {
        if (shakeTimer <= 0f || boardShakeTarget == null) return;
        shakeTimer -= dt;
        if (shakeTimer <= 0f)
        {
            boardShakeTarget.anchoredPosition = boardShakeRestPos;
        }
        else
        {
            float decay = shakeTimer / Mathf.Max(0.01f, shakeDuration);
            float mag = shakeStrength * decay * decay;
            boardShakeTarget.anchoredPosition = boardShakeRestPos + new Vector2(
                UnityEngine.Random.Range(-mag, mag),
                UnityEngine.Random.Range(-mag, mag)
            );
        }
    }

    private void MarkLayersDirty()
    {
        if (backAlphaLayer != null) backAlphaLayer.SetVerticesDirty();
        if (backAdditiveLayer != null) backAdditiveLayer.SetVerticesDirty();
        if (frontSpriteLayer != null) frontSpriteLayer.SetVerticesDirty();
    }

    // ==================== BUILD MESH CHO 3 VỊ THẦN ====================

    private void PopulateBackAlphaMesh(VertexHelper vh)
    {
        if (!isPlaying) return;

        // 1. OBELISK: Khe nứt địa chấn tối (viền đá nứt sâu) + các khối đá nguyên khối văng ra
        if (activeGod == MonsterEffectType.DivineObelisk)
        {
            if (climaxTriggered)
            {
                float crackAge = elapsed - climaxTime;
                float crackAlpha = elapsed < descendStartTime
                    ? 0.92f
                    : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.55f) * 0.92f;

                if (crackAlpha > 0.01f)
                {
                    float grow = EaseOutCubic(Mathf.Clamp01(crackAge / 0.16f));
                    for (int i = 0; i < activeCracks.Count; i++)
                    {
                        CrackBranch br = activeCracks[i];
                        AddPolylineStrip(
                            vh,
                            br.points,
                            grow,
                            br.baseWidth * 1.9f,
                            new Color(0.01f, 0.03f, 0.08f, crackAlpha),
                            new Color(0.01f, 0.03f, 0.08f, 0f)
                        );
                    }
                }
            }

            // Các khối đá nguyên khối vỡ tung
            for (int i = 0; i < debrisPool.Count; i++)
            {
                RockDebris d = debrisPool[i];
                if (!d.active) continue;
                float t = Mathf.Clamp01(d.age / d.lifetime);
                float a = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
                float sc = Mathf.Lerp(1f, 0.25f, t);
                AddPolygonRock(vh, d.pos, d.size * sc, d.sides, d.rot, new Color(d.darkColor.r, d.darkColor.g, d.darkColor.b, a), new Color(d.rimColor.r, d.rimColor.g, d.rimColor.b, a * 0.7f));
            }
        }
        // 2. SLIFER: Vòng xoáy mây bão đỏ thẫm cuộn quanh tâm
        else if (activeGod == MonsterEffectType.DivineSlifer)
        {
            float alpha = elapsed < descendStartTime
                ? Mathf.Clamp01(elapsed / 0.25f) * 0.52f
                : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.6f) * 0.52f;
            if (alpha > 0.01f)
            {
                DrawStormVortexRibbons(vh, Vector2.zero, elapsed * 3.8f, 6, 460f, 38f, new Color(0.45f, 0.02f, 0.05f, alpha));
            }
        }
        // 3. RA: Nền cam-vàng rực cho Đôi Cánh Phượng Hoàng sau khi Quả Cầu Ánh Sáng bùng nổ
        else if (activeGod == MonsterEffectType.DivineRa && climaxTriggered)
        {
            float wingAge = elapsed - climaxTime;
            float spread = EaseOutBack(Mathf.Clamp01(wingAge / 0.34f));
            float fade = elapsed < descendStartTime
                ? 0.72f
                : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.55f) * 0.72f;
            if (fade > 0.01f)
            {
                DrawPhoenixWings(vh, new Vector2(0f, 34f), spread, elapsed, new Color(RaDeepAmber.r, RaDeepAmber.g, RaDeepAmber.b, fade), new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, fade * 0.85f));
            }
        }
    }

    private void PopulateBackAdditiveMesh(VertexHelper vh)
    {
        if (!isPlaying) return;

        // ==================== 1. OBELISK: CỘT NĂNG LƯỢNG XANH + KHE NỨT PHÁT SÁNG CYAN ====================
        if (activeGod == MonsterEffectType.DivineObelisk)
        {
            // Cột năng lượng khổng lồ giáng từ đỉnh màn hình xuống tâm (và xuống ô bài lúc hạ cánh)
            if (elapsed < descendStartTime)
            {
                float pAlpha = elapsed < climaxTime
                    ? EaseOutCubic(elapsed / climaxTime) * 0.85f
                    : Mathf.Clamp01(1f - (elapsed - climaxTime) / 0.85f) * 0.95f;
                if (pAlpha > 0.01f)
                {
                    float w = elapsed < climaxTime ? Mathf.Lerp(32f, 180f, elapsed / climaxTime) : Mathf.Lerp(260f, 110f, Mathf.Clamp01((elapsed - climaxTime) / 0.7f));
                    AddVerticalBeam(vh, 0f, -540f, 560f, w, new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, pAlpha * 0.65f), new Color(ObeliskCyan.r, ObeliskCyan.g, ObeliskCyan.b, pAlpha));
                    AddVerticalBeam(vh, 0f, -540f, 560f, w * 0.34f, new Color(ObeliskCore.r, ObeliskCore.g, ObeliskCore.b, pAlpha), new Color(ObeliskCore.r, ObeliskCore.g, ObeliskCore.b, pAlpha));
                }
            }
            if (landTriggered)
            {
                float lt = Mathf.Clamp01((elapsed - landTime) / 0.48f);
                if (lt < 1f)
                {
                    float la = (1f - lt) * (1f - lt);
                    AddVerticalBeam(vh, targetZoneLocalPos.x, targetZoneLocalPos.y - 60f, 560f, 130f * (1f - lt * 0.4f), new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, la * 0.7f), new Color(ObeliskCore.r, ObeliskCore.g, ObeliskCore.b, la));
                }
            }

            // Lõi xanh điện sáng rực nằm trong các khe nứt địa chấn
            if (climaxTriggered)
            {
                float crackAge = elapsed - climaxTime;
                float crackAlpha = elapsed < descendStartTime
                    ? 0.95f
                    : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.50f) * 0.95f;
                if (crackAlpha > 0.01f)
                {
                    float grow = EaseOutCubic(Mathf.Clamp01(crackAge / 0.16f));
                    for (int i = 0; i < activeCracks.Count; i++)
                    {
                        CrackBranch br = activeCracks[i];
                        AddPolylineStrip(vh, br.points, grow, br.baseWidth * 1.15f, new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, crackAlpha * 0.85f), new Color(ObeliskBlue.r, ObeliskBlue.g, ObeliskBlue.b, 0f));
                        AddPolylineStrip(vh, br.points, grow, br.baseWidth * 0.48f, new Color(ObeliskCore.r, ObeliskCore.g, ObeliskCore.b, crackAlpha), new Color(ObeliskCyan.r, ObeliskCyan.g, ObeliskCyan.b, 0f));
                    }
                }
            }
        }
        // ==================== 2. SLIFER: XOÁY BÃO ĐỎ + THIÊN LÔI PHÂN NHÁNH ====================
        else if (activeGod == MonsterEffectType.DivineSlifer)
        {
            float vortexAlpha = elapsed < descendStartTime
                ? Mathf.Clamp01(elapsed / 0.25f) * 0.80f
                : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.55f) * 0.80f;
            if (vortexAlpha > 0.01f)
            {
                DrawStormVortexRibbons(vh, new Vector2(0f, 24f), elapsed * 5.2f, 5, 420f, 18f, new Color(SliferScarlet.r, SliferScarlet.g, SliferScarlet.b, vortexAlpha));
                DrawStormVortexRibbons(vh, new Vector2(0f, 24f), -elapsed * 3.6f, 3, 310f, 9f, new Color(SliferBoltGold.r, SliferBoltGold.g, SliferBoltGold.b, vortexAlpha * 0.72f));
            }
        }
        // ==================== 3. RA: 16 TIA NẮNG THẦN (GOD RAYS) + 3 VÒNG CỔ NGỮ AI CẬP + ĐÔI CÁNH PHƯỢNG HOÀNG ====================
        else if (activeGod == MonsterEffectType.DivineRa)
        {
            Vector2 sunCenter = new Vector2(0f, 34f);

            // 16 tia nắng Thần Mặt Trời quay rực rỡ khắp màn hình
            float rayAlpha = elapsed < climaxTime
                ? EaseOutCubic(elapsed / climaxTime) * 0.42f
                : (elapsed < descendStartTime
                    ? 0.76f
                    : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.55f) * 0.76f);

            if (rayAlpha > 0.01f)
            {
                float rayLen = elapsed < climaxTime ? Mathf.Lerp(200f, 640f, elapsed / climaxTime) : 1180f;
                DrawSunGodRays(vh, sunCenter, elapsed * 0.42f, rayCount: 16, length: rayLen, new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, rayAlpha * 0.65f));
                DrawSunGodRays(vh, sunCenter, -elapsed * 0.28f + 0.15f, rayCount: 8, length: rayLen * 0.82f, new Color(RaSunWhite.r, RaSunWhite.g, RaSunWhite.b, rayAlpha * 0.55f));
            }

            // 3 vòng tròn Cổ ngữ Mặt Trời (Hieratic Solar Rings) xoay ngược chiều quanh Quả Cầu Ánh Sáng & Thần Ra
            float ringFade = elapsed < descendStartTime
                ? Mathf.Clamp01(elapsed / 0.22f)
                : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.48f);
            if (ringFade > 0.01f)
            {
                float ringScale = elapsed < climaxTime
                    ? Mathf.Lerp(0.35f, 0.92f, EaseOutCubic(elapsed / climaxTime))
                    : Mathf.Lerp(1.05f, 1.32f, Mathf.Clamp01((elapsed - climaxTime) / 0.85f));
                DrawHieraticSolarRing(vh, sunCenter, 175f * ringScale, 8f, elapsed * 1.4f, 16, new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, ringFade * 0.88f));
                DrawHieraticSolarRing(vh, sunCenter, 235f * ringScale, 6f, -elapsed * 1.0f, 24, new Color(RaSunWhite.r, RaSunWhite.g, RaSunWhite.b, ringFade * 0.72f));
                DrawHieraticSolarRing(vh, sunCenter, 295f * ringScale, 5f, elapsed * 0.65f, 12, new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, ringFade * 0.60f));
            }

            // Đôi Cánh Phượng Hoàng Lửa rực cháy phát sáng sau khi bùng nổ
            if (climaxTriggered)
            {
                float wingAge = elapsed - climaxTime;
                float spread = EaseOutBack(Mathf.Clamp01(wingAge / 0.34f));
                float wFade = elapsed < descendStartTime
                    ? 0.95f
                    : Mathf.Clamp01(1f - (elapsed - descendStartTime) / 0.50f) * 0.95f;
                if (wFade > 0.01f)
                {
                    DrawPhoenixWings(vh, sunCenter, spread, elapsed, new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, wFade * 0.75f), new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, wFade));
                }
            }

            // Cột nắng vàng giáng xuống đúng ô bài của Ra khi hạ cánh
            if (landTriggered)
            {
                float lt = Mathf.Clamp01((elapsed - landTime) / 0.52f);
                if (lt < 1f)
                {
                    float la = (1f - lt) * (1f - lt);
                    AddVerticalBeam(vh, targetZoneLocalPos.x, targetZoneLocalPos.y - 60f, 560f, 145f * (1f - lt * 0.35f), new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, la * 0.75f), new Color(RaSunWhite.r, RaSunWhite.g, RaSunWhite.b, la));
                }
            }
        }

        // Vẽ tất cả các tia sét phân nhánh (dùng cho Slifer)
        for (int i = 0; i < boltPool.Count; i++)
        {
            LightningBolt b = boltPool[i];
            if (!b.active || b.points == null || b.points.Length < 2) continue;
            float t = Mathf.Clamp01(b.age / b.lifetime);
            float flicker = 0.75f + 0.25f * Mathf.Sin(b.age * 95f);
            float a = (1f - t) * flicker;
            AddPolylineStrip(vh, b.points, 1f, b.coreWidth * 2.8f, new Color(b.outerColor.r, b.outerColor.g, b.outerColor.b, a * 0.85f), new Color(b.outerColor.r, b.outerColor.g, b.outerColor.b, a * 0.3f));
            AddPolylineStrip(vh, b.points, 1f, b.coreWidth, new Color(b.coreColor.r, b.coreColor.g, b.coreColor.b, a), new Color(b.coreColor.r, b.coreColor.g, b.coreColor.b, a * 0.7f));
        }

        // Vẽ các vòng sóng xung kích
        for (int i = 0; i < ringPool.Count; i++)
        {
            GodRing r = ringPool[i];
            if (!r.active) continue;
            float t = Mathf.Clamp01(r.age / r.lifetime);
            float outerR = Mathf.Lerp(r.startR, r.endR, EaseOutCubic(t));
            float thick = Mathf.Max(2f, r.thickness * (1f - t * 0.85f));
            Color col = Color.Lerp(r.startCol, r.endCol, t);
            if (col.a <= 0.01f) continue;
            AddRingGeometry(vh, r.pos, Mathf.Max(0f, outerR - thick), outerR, col, r.segments);
        }
    }

    private void PopulateFrontSpriteMesh(VertexHelper vh)
    {
        if (!isPlaying) return;

        // Hiệu ứng Quả Cầu Ánh Sáng (Sphere Mode) chói lòa của Thần Ra trước khi nở ra thành Phượng Hoàng!
        if (activeGod == MonsterEffectType.DivineRa && elapsed < climaxTime + 0.14f)
        {
            float st = Mathf.Clamp01(elapsed / climaxTime);
            float pulse = 1f + 0.07f * Mathf.Sin(elapsed * 38f);
            float sphereSize = elapsed < climaxTime
                ? Mathf.Lerp(36f, 320f, EaseOutCubic(st)) * pulse
                : Mathf.Lerp(320f, 640f, (elapsed - climaxTime) / 0.14f);
            float alpha = elapsed < climaxTime
                ? Mathf.Clamp01(st * 2.2f)
                : 1f - Mathf.Clamp01((elapsed - climaxTime) / 0.14f);

            Vector2 center = new Vector2(0f, 34f);
            AddAtlasQuad(vh, center, sphereSize * 1.45f, sphereSize * 1.45f, elapsed * 80f, 0, new Color(RaPhoenixOrange.r, RaPhoenixOrange.g, RaPhoenixOrange.b, alpha * 0.80f));
            AddAtlasQuad(vh, center, sphereSize * 1.05f, sphereSize * 1.05f, -elapsed * 160f, 1, new Color(RaRoyalGold.r, RaRoyalGold.g, RaRoyalGold.b, alpha * 0.96f));
            AddAtlasQuad(vh, center, sphereSize * 0.68f, sphereSize * 0.68f, elapsed * 210f, 0, new Color(RaSunWhite.r, RaSunWhite.g, RaSunWhite.b, alpha));
            AddAtlasQuad(vh, center, sphereSize * 1.35f, sphereSize * 1.35f, elapsed * 35f, 3, new Color(RaSunWhite.r, RaSunWhite.g, RaSunWhite.b, alpha * 0.90f));
        }

        // Hào quang phát sáng sau lưng / viền quả cầu năng lượng lúc lá bài đang bay xuống ô bài
        if (elapsed >= descendStartTime && elapsed < landTime)
        {
            float dtNorm = (elapsed - descendStartTime) / Mathf.Max(0.01f, landTime - descendStartTime);
            Vector2 p = Vector2.Lerp(new Vector2(0f, 34f), targetZoneLocalPos, dtNorm * dtNorm * (3f - 2f * dtNorm));
            Color auraCol = activeGod == MonsterEffectType.DivineObelisk ? ObeliskCyan
                : activeGod == MonsterEffectType.DivineSlifer ? SliferBoltGold
                : RaRoyalGold;
            AddAtlasQuad(vh, p, 220f, 220f, elapsed * 240f, 1, new Color(auraCol.r, auraCol.g, auraCol.b, 0.72f));
        }

        // Toàn bộ các hạt năng lượng, tia lửa, bụi vàng, tia sét
        for (int i = 0; i < particlePool.Count; i++)
        {
            GodParticle p = particlePool[i];
            if (!p.active) continue;
            float t = Mathf.Clamp01(p.age / p.lifetime);
            float sz = Mathf.Lerp(p.startSize, p.endSize, EaseOutCubic(t));
            if (sz <= 0.5f) continue;
            Color col = Color.Lerp(p.startCol, p.endCol, t);
            if (col.a <= 0.01f) continue;

            float ang = p.alignToVel && p.vel.sqrMagnitude > 1f
                ? Mathf.Atan2(p.vel.y, p.vel.x) * Mathf.Rad2Deg
                : p.rot;
            AddAtlasQuad(vh, p.pos, sz * p.aspectX, sz, ang, p.tileIndex, col);
        }
    }

    // ==================== HÌNH HỌC ĐẶC TRƯNG CỦA 3 VỊ THẦN ====================

    private void GenerateSeismicCracks(Vector2 origin, int count, float maxRadius)
    {
        activeCracks.Clear();
        for (int i = 0; i < count; i++)
        {
            float baseAng = (i / (float)count) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.18f, 0.18f);
            float len = maxRadius * UnityEngine.Random.Range(0.70f, 1.05f);
            int segs = 9;
            Vector2[] pts = new Vector2[segs + 1];
            pts[0] = origin;

            Vector2 dir = new Vector2(Mathf.Cos(baseAng), Mathf.Sin(baseAng));
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float jag = 0f;
            for (int s = 1; s <= segs; s++)
            {
                float frac = s / (float)segs;
                jag += UnityEngine.Random.Range(-34f, 34f);
                jag = Mathf.Clamp(jag, -68f, 68f);
                pts[s] = origin + dir * (len * frac) + perp * (jag * frac);
            }

            activeCracks.Add(new CrackBranch { points = pts, baseWidth = UnityEngine.Random.Range(7f, 12f), delay = 0f });

            // Nhánh nứt phụ tách ra từ giữa khe nứt chính
            if (i % 2 == 0)
            {
                Vector2 forkStart = pts[4];
                float forkAng = baseAng + (UnityEngine.Random.value < 0.5f ? 0.55f : -0.55f);
                Vector2 fDir = new Vector2(Mathf.Cos(forkAng), Mathf.Sin(forkAng));
                Vector2 fPerp = new Vector2(-fDir.y, fDir.x);
                Vector2[] fPts = new Vector2[5];
                fPts[0] = forkStart;
                for (int s = 1; s <= 4; s++)
                {
                    fPts[s] = forkStart + fDir * (len * 0.36f * (s / 4f)) + fPerp * UnityEngine.Random.Range(-18f, 18f);
                }
                activeCracks.Add(new CrackBranch { points = fPts, baseWidth = 5.5f, delay = 0.04f });
            }
        }
    }

    private void SpawnForkedLightning(Vector2 start, Vector2 end, float coreWidth, float lifetime, Color outerColor, Color coreColor, bool withBranches = true)
    {
        Vector2[] mainPts = BuildJaggedBoltPoints(start, end, segments: 12, jitter: 54f);
        AddBoltToPool(mainPts, coreWidth, lifetime, outerColor, coreColor);

        if (withBranches)
        {
            for (int b = 0; b < 2; b++)
            {
                int startIdx = UnityEngine.Random.Range(3, 8);
                Vector2 branchOrigin = mainPts[startIdx];
                Vector2 dir = (end - start).normalized;
                float side = (b == 0 ? 1f : -1f) * UnityEngine.Random.Range(0.45f, 0.85f);
                float c = Mathf.Cos(side);
                float s = Mathf.Sin(side);
                Vector2 bDir = new Vector2(dir.x * c - dir.y * s, dir.x * s + dir.y * c);
                Vector2 branchEnd = branchOrigin + bDir * UnityEngine.Random.Range(140f, 280f);
                Vector2[] bPts = BuildJaggedBoltPoints(branchOrigin, branchEnd, segments: 6, jitter: 28f);
                AddBoltToPool(bPts, coreWidth * 0.52f, lifetime * 0.85f, outerColor, coreColor);
            }
        }
    }

    private Vector2[] BuildJaggedBoltPoints(Vector2 start, Vector2 end, int segments, float jitter)
    {
        Vector2[] pts = new Vector2[segments + 1];
        Vector2 delta = end - start;
        Vector2 dir = delta.sqrMagnitude > 0.01f ? delta.normalized : Vector2.down;
        Vector2 perp = new Vector2(-dir.y, dir.x);

        pts[0] = start;
        pts[segments] = end;
        for (int i = 1; i < segments; i++)
        {
            float frac = i / (float)segments;
            float env = Mathf.Sin(frac * Mathf.PI);
            pts[i] = Vector2.Lerp(start, end, frac) + perp * (UnityEngine.Random.Range(-jitter, jitter) * env);
        }
        return pts;
    }

    private void DrawStormVortexRibbons(VertexHelper vh, Vector2 center, float baseAngle, int arms, float maxRadius, float width, Color color)
    {
        const int steps = 16;
        for (int arm = 0; arm < arms; arm++)
        {
            float armOffset = baseAngle + (arm / (float)arms) * Mathf.PI * 2f;
            int prevL = -1;
            int prevR = -1;

            for (int s = 0; s <= steps; s++)
            {
                float u = s / (float)steps;
                float r = Mathf.Lerp(45f, maxRadius, u);
                float ang = armOffset + u * 2.8f;
                Vector2 pos = center + new Vector2(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r * 0.76f);
                Vector2 radial = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));

                float env = Mathf.Sin(u * Mathf.PI);
                float hw = width * 0.5f * env;
                Color col = new Color(color.r, color.g, color.b, color.a * env);

                int cL = vh.currentVertCount;
                UIVertex vl = UIVertex.simpleVert;
                vl.position = pos + radial * hw;
                vl.color = col;
                vh.AddVert(vl);

                int cR = vh.currentVertCount;
                UIVertex vr = UIVertex.simpleVert;
                vr.position = pos - radial * hw;
                vr.color = col;
                vh.AddVert(vr);

                if (prevL >= 0)
                {
                    vh.AddTriangle(prevL, prevR, cR);
                    vh.AddTriangle(prevL, cR, cL);
                }
                prevL = cL;
                prevR = cR;
            }
        }
    }

    private void DrawSunGodRays(VertexHelper vh, Vector2 center, float angleOffset, int rayCount, float length, Color innerColor)
    {
        Color outerCol = new Color(innerColor.r, innerColor.g, innerColor.b, 0f);
        float halfAngle = (Mathf.PI / rayCount) * 0.42f;

        for (int i = 0; i < rayCount; i++)
        {
            float a = angleOffset + (i / (float)rayCount) * Mathf.PI * 2f;
            Vector2 p1 = center + new Vector2(Mathf.Cos(a - halfAngle), Mathf.Sin(a - halfAngle)) * length;
            Vector2 p2 = center + new Vector2(Mathf.Cos(a + halfAngle), Mathf.Sin(a + halfAngle)) * length;

            int idx = vh.currentVertCount;
            UIVertex v0 = UIVertex.simpleVert; v0.position = center; v0.color = innerColor; vh.AddVert(v0);
            UIVertex v1 = UIVertex.simpleVert; v1.position = p1; v1.color = outerCol; vh.AddVert(v1);
            UIVertex v2 = UIVertex.simpleVert; v2.position = p2; v2.color = outerCol; vh.AddVert(v2);
            vh.AddTriangle(idx, idx + 1, idx + 2);
        }
    }

    private void DrawHieraticSolarRing(VertexHelper vh, Vector2 center, float radius, float thickness, float rotOffset, int glyphCount, Color col)
    {
        AddRingGeometry(vh, center, Mathf.Max(2f, radius - thickness), radius, col, 40);

        // Các răng cổ ngữ hình học xung quanh vòng Mặt Trời Ai Cập
        for (int i = 0; i < glyphCount; i++)
        {
            float a = rotOffset + (i / (float)glyphCount) * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float inner = radius + 2f;
            float outer = radius + ((i % 2 == 0) ? 24f : 13f);
            float hw = (i % 2 == 0) ? 6.5f : 3.5f;

            int s = vh.currentVertCount;
            UIVertex v0 = UIVertex.simpleVert; v0.position = center + dir * inner - perp * hw; v0.color = col; vh.AddVert(v0);
            UIVertex v1 = UIVertex.simpleVert; v1.position = center + dir * inner + perp * hw; v1.color = col; vh.AddVert(v1);
            UIVertex v2 = UIVertex.simpleVert; v2.position = center + dir * outer; v2.color = new Color(col.r, col.g, col.b, col.a * 0.25f); vh.AddVert(v2);
            vh.AddTriangle(s, s + 1, s + 2);
        }
    }

    private void DrawPhoenixWings(VertexHelper vh, Vector2 center, float spread, float time, Color outerCol, Color innerCol)
    {
        if (spread <= 0.02f) return;

        // Vẽ 2 cánh Phượng Hoàng đối xứng trái - phải, mỗi cánh gồm 7 lông vũ lửa dài uyển chuyển
        for (int side = -1; side <= 1; side += 2)
        {
            for (int f = 0; f < 7; f++)
            {
                float frac = f / 6f; // 0 = lông đỉnh cao nhất, 1 = lông dưới thấp hơn
                float flap = Mathf.Sin(time * 7f - frac * 1.4f) * 14f;
                float len = Mathf.Lerp(590f, 300f, frac) * spread;
                float angDeg = Mathf.Lerp(26f, -34f, frac) + flap * 0.25f;
                float angRad = angDeg * Mathf.Deg2Rad;

                Vector2 dir = new Vector2(side * Mathf.Cos(angRad), Mathf.Sin(angRad));
                Vector2 perp = new Vector2(-dir.y, dir.x);

                Vector2 root = center + new Vector2(side * 65f, Mathf.Lerp(55f, -70f, frac));
                Vector2 mid = root + dir * (len * 0.52f) + new Vector2(0f, 34f * spread + flap);
                Vector2 tip = root + dir * len + new Vector2(0f, 12f * spread + flap * 1.4f);

                float midWidth = Mathf.Lerp(54f, 30f, frac) * spread;
                Color tipCol = new Color(outerCol.r, outerCol.g, outerCol.b, 0f);

                int s = vh.currentVertCount;
                UIVertex vRoot = UIVertex.simpleVert; vRoot.position = root; vRoot.color = innerCol; vh.AddVert(vRoot);
                UIVertex vMidA = UIVertex.simpleVert; vMidA.position = mid + perp * midWidth; vMidA.color = outerCol; vh.AddVert(vMidA);
                UIVertex vTip = UIVertex.simpleVert; vTip.position = tip; vTip.color = tipCol; vh.AddVert(vTip);
                UIVertex vMidB = UIVertex.simpleVert; vMidB.position = mid - perp * (midWidth * 0.65f); vMidB.color = innerCol; vh.AddVert(vMidB);

                vh.AddTriangle(s, s + 1, s + 2);
                vh.AddTriangle(s, s + 2, s + 3);
            }
        }
    }

    // ==================== HELPER HÌNH HỌC CƠ BẢN ====================

    private void AddVerticalBeam(VertexHelper vh, float centerX, float minY, float maxY, float width, Color edgeCol, Color coreCol)
    {
        float hw = width * 0.5f;
        Color transparentEdge = new Color(edgeCol.r, edgeCol.g, edgeCol.b, 0f);

        int s = vh.currentVertCount;
        // 3 dải dọc: Trái mờ -> Tâm sáng -> Phải mờ
        UIVertex v0 = UIVertex.simpleVert; v0.position = new Vector2(centerX - hw, minY); v0.color = transparentEdge; vh.AddVert(v0);
        UIVertex v1 = UIVertex.simpleVert; v1.position = new Vector2(centerX, minY); v1.color = coreCol; vh.AddVert(v1);
        UIVertex v2 = UIVertex.simpleVert; v2.position = new Vector2(centerX + hw, minY); v2.color = transparentEdge; vh.AddVert(v2);

        UIVertex v3 = UIVertex.simpleVert; v3.position = new Vector2(centerX - hw, maxY); v3.color = transparentEdge; vh.AddVert(v3);
        UIVertex v4 = UIVertex.simpleVert; v4.position = new Vector2(centerX, maxY); v4.color = coreCol; vh.AddVert(v4);
        UIVertex v5 = UIVertex.simpleVert; v5.position = new Vector2(centerX + hw, maxY); v5.color = transparentEdge; vh.AddVert(v5);

        vh.AddTriangle(s + 0, s + 1, s + 4);
        vh.AddTriangle(s + 0, s + 4, s + 3);
        vh.AddTriangle(s + 1, s + 2, s + 5);
        vh.AddTriangle(s + 1, s + 5, s + 4);
    }

    private void AddPolylineStrip(VertexHelper vh, Vector2[] pts, float progress, float startWidth, Color startCol, Color endCol)
    {
        if (pts == null || pts.Length < 2 || progress <= 0.01f) return;

        int maxIdx = Mathf.Clamp(Mathf.CeilToInt((pts.Length - 1) * progress), 1, pts.Length - 1);
        int prevL = -1;
        int prevR = -1;

        for (int i = 0; i <= maxIdx; i++)
        {
            float u = i / (float)(pts.Length - 1);
            Vector2 pos = pts[i];
            Vector2 tangent = i == 0 ? (pts[1] - pos)
                : (i == pts.Length - 1 ? (pos - pts[i - 1]) : (pts[i + 1] - pts[i - 1]));
            tangent = tangent.sqrMagnitude > 0.001f ? tangent.normalized : Vector2.up;
            Vector2 perp = new Vector2(-tangent.y, tangent.x);

            float hw = startWidth * 0.5f * Mathf.Lerp(1f, 0.12f, u);
            Color col = Color.Lerp(startCol, endCol, u);

            int cL = vh.currentVertCount;
            UIVertex vl = UIVertex.simpleVert; vl.position = pos + perp * hw; vl.color = col; vh.AddVert(vl);

            int cR = vh.currentVertCount;
            UIVertex vr = UIVertex.simpleVert; vr.position = pos - perp * hw; vr.color = col; vh.AddVert(vr);

            if (prevL >= 0)
            {
                vh.AddTriangle(prevL, prevR, cR);
                vh.AddTriangle(prevL, cR, cL);
            }
            prevL = cL;
            prevR = cR;
        }
    }

    private void AddPolygonRock(VertexHelper vh, Vector2 center, float radius, int sides, float angleDeg, Color coreCol, Color rimCol)
    {
        if (radius <= 1f || sides < 3) return;
        float baseRad = angleDeg * Mathf.Deg2Rad;
        int cIdx = vh.currentVertCount;
        UIVertex vc = UIVertex.simpleVert; vc.position = center; vc.color = coreCol; vh.AddVert(vc);

        int rStart = vh.currentVertCount;
        for (int i = 0; i < sides; i++)
        {
            float a = baseRad + (i / (float)sides) * Mathf.PI * 2f;
            float r = radius * ((i % 2 == 0) ? 1.0f : 0.78f);
            UIVertex vr = UIVertex.simpleVert;
            vr.position = center + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            vr.color = (i == 0 || i == 1) ? rimCol : coreCol;
            vh.AddVert(vr);
        }

        for (int i = 0; i < sides; i++)
        {
            vh.AddTriangle(cIdx, rStart + i, rStart + ((i + 1) % sides));
        }
    }

    private void AddRingGeometry(VertexHelper vh, Vector2 center, float innerR, float outerR, Color col, int segments)
    {
        int startIdx = vh.currentVertCount;
        Color innerCol = new Color(col.r, col.g, col.b, col.a * 0.25f);
        for (int i = 0; i < segments; i++)
        {
            float ang = (i / (float)segments) * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));

            UIVertex vi = UIVertex.simpleVert; vi.position = center + dir * innerR; vi.color = innerCol; vh.AddVert(vi);
            UIVertex vo = UIVertex.simpleVert; vo.position = center + dir * outerR; vo.color = col; vh.AddVert(vo);
        }
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            int i0 = startIdx + i * 2;
            int o0 = i0 + 1;
            int i1 = startIdx + next * 2;
            int o1 = i1 + 1;
            vh.AddTriangle(i0, o0, o1);
            vh.AddTriangle(i0, o1, i1);
        }
    }

    private void AddAtlasQuad(VertexHelper vh, Vector2 center, float width, float height, float angleDeg, int tileIndex, Color color)
    {
        float hw = width * 0.5f;
        float hh = height * 0.5f;
        float rad = angleDeg * Mathf.Deg2Rad;
        float c = Mathf.Cos(rad);
        float s = Mathf.Sin(rad);
        Vector2 rx = new Vector2(c * hw, s * hw);
        Vector2 ry = new Vector2(-s * hh, c * hh);

        float u0 = (tileIndex == 1 || tileIndex == 3) ? 0.515f : 0.015f;
        float u1 = (tileIndex == 1 || tileIndex == 3) ? 0.985f : 0.485f;
        float v0 = (tileIndex == 2 || tileIndex == 3) ? 0.515f : 0.015f;
        float v1 = (tileIndex == 2 || tileIndex == 3) ? 0.985f : 0.485f;

        int start = vh.currentVertCount;
        UIVertex v = UIVertex.simpleVert; v.color = color;

        v.position = center - rx - ry; v.uv0 = new Vector2(u0, v0); vh.AddVert(v);
        v.position = center + rx - ry; v.uv0 = new Vector2(u1, v0); vh.AddVert(v);
        v.position = center + rx + ry; v.uv0 = new Vector2(u1, v1); vh.AddVert(v);
        v.position = center - rx + ry; v.uv0 = new Vector2(u0, v1); vh.AddVert(v);

        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }

    // ==================== QUẢN LÝ POOL ====================

    private void ClearPools()
    {
        activeCracks.Clear();
        for (int i = 0; i < boltPool.Count; i++) { var b = boltPool[i]; b.active = false; boltPool[i] = b; }
        for (int i = 0; i < debrisPool.Count; i++) { var d = debrisPool[i]; d.active = false; debrisPool[i] = d; }
        for (int i = 0; i < ringPool.Count; i++) { var r = ringPool[i]; r.active = false; ringPool[i] = r; }
        for (int i = 0; i < particlePool.Count; i++) { var p = particlePool[i]; p.active = false; particlePool[i] = p; }
    }

    private void AddBoltToPool(Vector2[] pts, float coreWidth, float lifetime, Color outerColor, Color coreColor)
    {
        int idx = -1;
        for (int i = 0; i < boltPool.Count; i++) if (!boltPool[i].active) { idx = i; break; }
        if (idx < 0) { boltPool.Add(default); idx = boltPool.Count - 1; }
        boltPool[idx] = new LightningBolt
        {
            active = true,
            points = pts,
            age = 0f,
            lifetime = Mathf.Max(0.08f, lifetime),
            coreWidth = coreWidth,
            outerColor = outerColor,
            coreColor = coreColor
        };
    }

    private void SpawnDebris(Vector2 pos, Vector2 vel, float size, int sides, float lifetime, Color darkCol, Color rimCol)
    {
        int idx = -1;
        for (int i = 0; i < debrisPool.Count; i++) if (!debrisPool[i].active) { idx = i; break; }
        if (idx < 0) { debrisPool.Add(default); idx = debrisPool.Count - 1; }
        debrisPool[idx] = new RockDebris
        {
            active = true,
            pos = pos,
            vel = vel,
            rot = UnityEngine.Random.Range(0f, 360f),
            rotSpeed = UnityEngine.Random.Range(-320f, 320f),
            size = size,
            sides = sides,
            age = 0f,
            lifetime = lifetime,
            darkColor = darkCol,
            rimColor = rimCol
        };
    }

    private void SpawnRing(Vector2 pos, float startR, float endR, float thickness, float lifetime, Color startCol, Color endCol, int segments)
    {
        int idx = -1;
        for (int i = 0; i < ringPool.Count; i++) if (!ringPool[i].active) { idx = i; break; }
        if (idx < 0) { ringPool.Add(default); idx = ringPool.Count - 1; }
        ringPool[idx] = new GodRing
        {
            active = true,
            pos = pos,
            age = 0f,
            lifetime = lifetime,
            startR = startR,
            endR = endR,
            thickness = thickness,
            startCol = startCol,
            endCol = endCol,
            segments = segments
        };
    }

    private void SpawnParticle(Vector2 pos, Vector2 vel, float drag, float gravityY, float lifetime, float startSize, float endSize, float aspectX, float rot, float rotSpeed, bool alignToVel, int tileIndex, Color startCol, Color endCol)
    {
        int idx = -1;
        for (int i = 0; i < particlePool.Count; i++) if (!particlePool[i].active) { idx = i; break; }
        if (idx < 0)
        {
            if (particlePool.Count < 380) { particlePool.Add(default); idx = particlePool.Count - 1; }
            else idx = 0;
        }
        particlePool[idx] = new GodParticle
        {
            active = true,
            pos = pos,
            vel = vel,
            drag = drag,
            gravityY = gravityY,
            age = 0f,
            lifetime = Mathf.Max(0.05f, lifetime),
            startSize = startSize,
            endSize = endSize,
            aspectX = aspectX,
            rot = rot,
            rotSpeed = rotSpeed,
            alignToVel = alignToVel,
            tileIndex = tileIndex,
            startCol = startCol,
            endCol = endCol
        };
    }

    // ==================== ÂM THANH TỔNG HỢP ĐẶC TRƯNG 3 VỊ THẦN (PROCEDURAL PCM) ====================

    private void SetupAudio()
    {
        divineAudioSource = gameObject.AddComponent<AudioSource>();
        divineAudioSource.playOnAwake = false;
        divineAudioSource.loop = false;
        divineAudioSource.spatialBlend = 0f; // 2D UI Milestone audio

        obeliskClip = SynthesizeObeliskClip();
        sliferClip = SynthesizeSliferClip();
        raClip = SynthesizeRaClip();
    }

    private void PlayGodAudio(MonsterEffectType god)
    {
        if (!playDivineSynthesizedAudio || divineAudioSource == null) return;

        float vol = 0.85f;
        if (SoundManager.Instance != null)
        {
            if (!SoundManager.Instance.IsSFXOn) return;
            vol = Mathf.Clamp01(SoundManager.Instance.sfxVolume * 0.9f);
        }

        AudioClip clip = god == MonsterEffectType.DivineObelisk ? obeliskClip
            : god == MonsterEffectType.DivineSlifer ? sliferClip
            : raClip;

        if (clip != null)
        {
            divineAudioSource.PlayOneShot(clip, vol);
        }
    }

    private AudioClip SynthesizeObeliskClip()
    {
        const int sr = 22050;
        float len = 1.65f;
        int samples = Mathf.RoundToInt(sr * len);
        float[] data = new float[samples];
        System.Random rng = new System.Random(101);
        float noiseLP = 0f;

        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)sr;
            float n = (float)(rng.NextDouble() * 2.0 - 1.0);
            noiseLP = noiseLP * 0.92f + n * 0.08f;

            float val = 0f;
            // Pha tụ năng lượng nặng (0 -> 0.46s)
            if (t < 0.46f)
            {
                float u = t / 0.46f;
                val += Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(45f, 95f, u) * t) * 0.28f * u;
                val += noiseLP * 0.25f * u;
            }
            else
            {
                // Pha địa chấn giáng mạnh (0.46s+)
                float dt = t - 0.46f;
                float slamEnv = Mathf.Exp(-3.4f * dt);
                float subFreq = Mathf.Lerp(92f, 28f, Mathf.Clamp01(dt / 0.35f));
                float sub = Mathf.Sin(2f * Mathf.PI * subFreq * dt) * slamEnv * 0.72f;
                float rumble = noiseLP * Mathf.Exp(-2.5f * dt) * 0.65f;
                // Hợp âm quyền lực trầm (C2 + G2 + C3)
                float chordEnv = Mathf.Exp(-1.6f * dt) * 0.22f;
                float chord = (Mathf.Sin(2f * Mathf.PI * 65.4f * dt) + 0.7f * Mathf.Sin(2f * Mathf.PI * 98.0f * dt) + 0.5f * Mathf.Sin(2f * Mathf.PI * 130.8f * dt)) * chordEnv;
                val = sub + rumble + chord;
            }

            data[i] = Mathf.Clamp(val * FadeEdgeEnvelope(i, samples, sr), -0.95f, 0.95f);
        }

        AudioClip clip = AudioClip.Create("GodSummon_Obelisk", samples, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    private AudioClip SynthesizeSliferClip()
    {
        const int sr = 22050;
        float len = 1.65f;
        int samples = Mathf.RoundToInt(sr * len);
        float[] data = new float[samples];
        System.Random rng = new System.Random(202);
        float stormLP = 0f;

        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)sr;
            float n = (float)(rng.NextDouble() * 2.0 - 1.0);
            stormLP = stormLP * 0.86f + n * 0.14f;

            float val = 0f;
            // Gió bão & tĩnh điện (0 -> 0.52s)
            if (t < 0.52f)
            {
                float u = t / 0.52f;
                float crackle = (rng.NextDouble() < 0.04) ? n * 0.55f : 0f;
                val = stormLP * 0.35f * u + crackle * u;
            }
            else
            {
                // Tiếng sét nổ sắc gắt + sấm rền uy lực (0.52s+)
                float dt = t - 0.52f;
                float snap = n * Mathf.Exp(-22f * dt) * 0.85f;
                float thunder = stormLP * Mathf.Exp(-2.2f * dt) * 0.70f;
                float zap = Mathf.Sin(2f * Mathf.PI * (240f + 90f * Mathf.Sin(dt * 80f)) * dt) * Mathf.Exp(-8f * dt) * 0.35f;
                // Hợp âm thứ uy nghiêm (D2 + A2 + F3)
                float chordEnv = Mathf.Exp(-1.8f * dt) * 0.18f;
                float chord = (Mathf.Sin(2f * Mathf.PI * 73.4f * dt) + 0.7f * Mathf.Sin(2f * Mathf.PI * 110.0f * dt) + 0.5f * Mathf.Sin(2f * Mathf.PI * 174.6f * dt)) * chordEnv;
                val = snap + thunder + zap + chord;
            }

            data[i] = Mathf.Clamp(val * FadeEdgeEnvelope(i, samples, sr), -0.95f, 0.95f);
        }

        AudioClip clip = AudioClip.Create("GodSummon_Slifer", samples, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    private AudioClip SynthesizeRaClip()
    {
        const int sr = 22050;
        float len = 1.85f;
        int samples = Mathf.RoundToInt(sr * len);
        float[] data = new float[samples];
        System.Random rng = new System.Random(303);
        float flameLP = 0f;

        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)sr;
            float n = (float)(rng.NextDouble() * 2.0 - 1.0);
            flameLP = flameLP * 0.88f + n * 0.12f;

            float val = 0f;
            // Quả Cầu Ánh Sáng (Sphere Mode) ngân vang tăng cao dần (0 -> 0.68s)
            if (t < 0.68f)
            {
                float u = t / 0.68f;
                float shimmer = (Mathf.Sin(2f * Mathf.PI * 261.6f * t)
                               + 0.6f * Mathf.Sin(2f * Mathf.PI * 392.0f * t)
                               + 0.45f * Mathf.Sin(2f * Mathf.PI * 523.25f * t)
                               + 0.35f * Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(523f, 1046f, u) * t)) * 0.18f * u;
                val = shimmer + flameLP * 0.18f * u;
            }
            else
            {
                // Siêu tân tinh Mặt Trời + Phượng Hoàng Lửa rực cháy (0.68s+)
                float dt = t - 0.68f;
                float boom = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(110f, 44f, Mathf.Clamp01(dt / 0.35f)) * dt) * Mathf.Exp(-3.6f * dt) * 0.55f;
                float flameWhoosh = flameLP * Mathf.Exp(-1.9f * dt) * 0.52f;
                // Hợp âm Mặt Trời trưởng sáng rực (C3 + G3 + D4 + E4 + C5)
                float chorusEnv = Mathf.Exp(-1.35f * dt) * 0.16f;
                float chorus = (Mathf.Sin(2f * Mathf.PI * 130.8f * dt)
                              + 0.8f * Mathf.Sin(2f * Mathf.PI * 196.0f * dt)
                              + 0.6f * Mathf.Sin(2f * Mathf.PI * 293.7f * dt)
                              + 0.7f * Mathf.Sin(2f * Mathf.PI * 329.6f * dt)
                              + 0.5f * Mathf.Sin(2f * Mathf.PI * 523.3f * dt)) * chorusEnv;
                val = boom + flameWhoosh + chorus;
            }

            data[i] = Mathf.Clamp(val * FadeEdgeEnvelope(i, samples, sr), -0.95f, 0.95f);
        }

        AudioClip clip = AudioClip.Create("GodSummon_Ra", samples, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static float FadeEdgeEnvelope(int sampleIndex, int totalSamples, int sampleRate)
    {
        int fadeSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * 0.006f)); // 6ms chống click
        if (sampleIndex < fadeSamples) return sampleIndex / (float)fadeSamples;
        int remain = totalSamples - 1 - sampleIndex;
        if (remain < fadeSamples) return Mathf.Max(0f, remain / (float)fadeSamples);
        return 1f;
    }

    // ==================== PROCEDURAL 256x256 ATLAS ====================

    private Texture2D CreateProceduralAtlas()
    {
        const int size = 256;
        const int half = 128;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int tileX = x < half ? 0 : 1;
                int tileY = y < half ? 0 : 1;
                int tileIdx = tileY * 2 + tileX;

                float lx = ((x - tileX * half) / (float)(half - 1)) * 2f - 1f;
                float ly = ((y - tileY * half) / (float)(half - 1)) * 2f - 1f;
                float r = Mathf.Sqrt(lx * lx + ly * ly);
                float a = 0f;

                if (r < 0.96f)
                {
                    if (tileIdx == 0)
                    {
                        a = Mathf.Clamp01(Mathf.Exp(-4.5f * r * r) * Mathf.Clamp01((0.96f - r) / 0.16f));
                    }
                    else if (tileIdx == 1)
                    {
                        float ang = Mathf.Atan2(ly, lx);
                        float swirl = ang + r * 3.4f;
                        float tongues = 0.56f + 0.28f * Mathf.Sin(swirl * 5f) + 0.16f * Mathf.Cos(ang * 8f - r * 5f);
                        float shaped = r / Mathf.Lerp(0.68f, 1.02f, tongues);
                        a = Mathf.Clamp01((Mathf.Exp(-3.0f * shaped * shaped) + Mathf.Exp(-9f * r * r) * 0.45f) * Mathf.Clamp01((0.96f - r) / 0.16f));
                    }
                    else if (tileIdx == 2)
                    {
                        float sr = Mathf.Sqrt((lx * 1.05f) * (lx * 1.05f) + (ly * 2.5f) * (ly * 2.5f));
                        a = Mathf.Clamp01(Mathf.Exp(-3.4f * sr * sr) * Mathf.Clamp01((0.96f - Mathf.Max(Mathf.Abs(lx), Mathf.Abs(ly))) / 0.14f));
                    }
                    else
                    {
                        float ax = Mathf.Abs(lx);
                        float ay = Mathf.Abs(ly);
                        float star = Mathf.Exp(-18f * ay * ay - 3.0f * ax * ax) + Mathf.Exp(-18f * ax * ax - 3.0f * ay * ay) + Mathf.Exp(-9f * r * r) * 0.85f;
                        a = Mathf.Clamp01(star * Mathf.Clamp01((0.96f - r) / 0.14f));
                    }
                }

                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        return tex;
    }

    // ==================== EASING HELPERS ====================

    private static float EaseOutCubic(float t)
    {
        float inv = 1f - Mathf.Clamp01(t);
        return 1f - inv * inv * inv;
    }

    private static float EaseOutBack(float t)
    {
        float c1 = 1.70158f;
        float c3 = c1 + 1f;
        float x = Mathf.Clamp01(t) - 1f;
        return 1f + c3 * x * x * x + c1 * x * x;
    }

    // ==================== CUSTOM GRAPHIC LAYERS ====================

    private class GodMeshLayer : MaskableGraphic
    {
        public Action<VertexHelper> onPopulate;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            onPopulate?.Invoke(vh);
        }
    }

    private class GodSpriteLayer : MaskableGraphic
    {
        public Action<VertexHelper> onPopulate;
        private Texture2D atlas;

        public override Texture mainTexture => atlas != null ? (Texture)atlas : s_WhiteTexture;

        public void SetAtlas(Texture2D tex)
        {
            atlas = tex;
            SetMaterialDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            onPopulate?.Invoke(vh);
        }
    }
}
