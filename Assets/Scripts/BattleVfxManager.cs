using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Hệ thống hiệu ứng hình ảnh (VFX) chạy trực tiếp trên uGUI Overlay Canvas (pooled, không Instantiate/Destroy theo từng đòn).
/// - Hiệu ứng Tấn công: Quả cầu lửa lớn xuất phát từ chính giữa lá bài tấn công bay tới chính giữa lá bài mục tiêu
///   (hoặc vùng LP đối phương khi Đánh trực tiếp) với đường đạn (projectile trail) nhiều lớp rõ nét + vụ nổ va chạm.
/// - Hiệu ứng Phá hủy (Destroy): Quái thú vỡ vụn thành các khối đa giác (polygon pieces) màu vàng xoay lật 3D và tan biến dần.
/// </summary>
public class BattleVfxManager : MonoBehaviour
{
    public static BattleVfxManager Instance { get; private set; }

    [Header("Cấu hình Quả cầu lửa (Attack Fireball)")]
    [Tooltip("Thời gian tụ lửa tại chính giữa lá bài tấn công trước khi phóng.")]
    public float chargeDuration = 0.07f;

    [Tooltip("Thời gian quả cầu lửa bay từ lá bài tấn công tới mục tiêu.")]
    public float flightDuration = 0.36f;

    [Tooltip("Độ tồn tại của đường đạn (projectile trail) phía sau quả cầu lửa.")]
    public float trailLifetime = 0.28f;

    [Tooltip("Hệ số kích thước tổng thể của quả cầu lửa lớn.")]
    public float fireballScale = 1.08f;

    [Tooltip("Bề rộng đường đạn (projectile trail) tại đầu quả cầu lửa.")]
    public float trailHeadWidth = 56f;

    [Header("Cấu hình Phá hủy (Golden Polygon Shatter)")]
    [Tooltip("Thời gian tồn tại tối thiểu của từng mảnh đa giác màu vàng.")]
    public float shardMinLifetime = 0.72f;

    [Tooltip("Thời gian tồn tại tối đa của từng mảnh đa giác màu vàng.")]
    public float shardMaxLifetime = 1.08f;

    [Tooltip("Cường độ rung nhẹ bàn đấu khi quả cầu lửa va chạm.")]
    public float impactShakeAmplitude = 9f;

    public float AttackImpactDelay => Mathf.Max(0.05f, chargeDuration + flightDuration);

    private RectTransform vfxRootRT;
    private RectTransform boardShakeTarget;
    private Vector2 boardShakeRestPos;
    private float shakeTimer = 0f;
    private float shakeDuration = 0.22f;
    private float shakeStrength = 0f;

    private VfxSolidMeshLayer solidAlphaLayer;
    private VfxSolidMeshLayer solidAdditiveLayer;
    private VfxSpriteAdditiveLayer spriteAdditiveLayer;

    private Texture2D proceduralAtlasTex;
    private Material additiveMaterial;
    private bool isInitialized = false;

    // ==================== POOLED DATA STRUCTURES ====================

    private struct TrailNode
    {
        public Vector2 pos;
        public float age;
    }

    private class FireballInstance
    {
        public bool active;
        public Vector2 startPos;
        public Vector2 endPos;
        public Vector2 currentPos;
        public Vector2 moveDir;
        public float elapsed;
        public float chargeTime;
        public float travelTime;
        public float scale;
        public bool impacted;
        public float spinA;
        public float spinB;
        public float sparkCarry;
        public Action onImpact;
        public readonly List<TrailNode> trail = new List<TrailNode>(128);
    }

    private struct PolygonShard
    {
        public bool active;
        public Vector2 pos;
        public Vector2 vel;
        public float rotZ;
        public float rotZSpeed;
        public float tumblePhase;
        public float tumbleSpeed;
        public Vector2 tumbleAxis;
        public float age;
        public float lifetime;
        public int vertCount; // 3, 4, hoặc 5 đỉnh tạo khối đa giác sắc nét
        public Vector2 v0, v1, v2, v3, v4;
        public Color coreGold;
        public Color edgeGold;
    }

    private struct VfxParticle
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
        public float rotation;
        public float rotSpeed;
        public bool alignToVelocity;
        public int tileIndex; // 0 = glow, 1 = flame, 2 = streak, 3 = star
        public Color startColor;
        public Color endColor;
    }

    private struct ShockwaveRing
    {
        public bool active;
        public Vector2 pos;
        public float age;
        public float lifetime;
        public float startRadius;
        public float endRadius;
        public float startThickness;
        public Color startColor;
        public Color endColor;
        public int segments;
    }

    private struct CardPopAnim
    {
        public bool active;
        public RectTransform target;
        public Vector3 baseScale;
        public float age;
        public float duration;
        public float peakScale;
    }

    private readonly List<FireballInstance> fireballPool = new List<FireballInstance>(8);
    private readonly List<PolygonShard> shardPool = new List<PolygonShard>(256);
    private readonly List<VfxParticle> particlePool = new List<VfxParticle>(320);
    private readonly List<ShockwaveRing> ringPool = new List<ShockwaveRing>(24);
    private readonly List<CardPopAnim> cardPopPool = new List<CardPopAnim>(8);

    private bool hadActiveVfxLastFrame = false;

    // Bảng màu vàng hoàng kim đặc trưng của Yu-Gi-Oh cho khối đa giác phá hủy
    private static readonly Color GoldSpecular = new Color(1.00f, 0.98f, 0.72f, 1f);
    private static readonly Color GoldBright = new Color(1.00f, 0.88f, 0.18f, 1f);
    private static readonly Color GoldRich = new Color(1.00f, 0.74f, 0.06f, 1f);
    private static readonly Color GoldDeep = new Color(0.86f, 0.52f, 0.03f, 1f);

    // Bảng màu lửa đậm rực cho quả cầu lửa và đường đạn
    private static readonly Color FireWhiteHot = new Color(1.00f, 0.99f, 0.88f, 1f);
    private static readonly Color FireYellow = new Color(1.00f, 0.84f, 0.22f, 1f);
    private static readonly Color FireOrange = new Color(1.00f, 0.46f, 0.05f, 1f);
    private static readonly Color FireCrimson = new Color(0.92f, 0.16f, 0.02f, 1f);
    private static readonly Color FireDarkRed = new Color(0.55f, 0.05f, 0.00f, 0f);

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
        if (proceduralAtlasTex != null) Destroy(proceduralAtlasTex);
        if (additiveMaterial != null) Destroy(additiveMaterial);
    }

    /// <summary>
    /// Khởi tạo lớp VFX bên trong BattleAutoBoardRoot (nằm trên các lá bài và dưới các popup/modal).
    /// </summary>
    public static BattleVfxManager EnsureCreated(RectTransform parentBoardRoot, int insertSiblingIndex = -1)
    {
        if (Instance != null && Instance.isInitialized)
        {
            if (insertSiblingIndex >= 0 && Instance.vfxRootRT != null && Instance.vfxRootRT.parent == parentBoardRoot)
            {
                Instance.vfxRootRT.SetSiblingIndex(Mathf.Clamp(insertSiblingIndex, 0, parentBoardRoot.childCount - 1));
            }
            return Instance;
        }

        GameObject go = new GameObject("BattleVfxLayer", typeof(RectTransform), typeof(BattleVfxManager));
        go.transform.SetParent(parentBoardRoot, false);
        if (insertSiblingIndex >= 0)
        {
            go.transform.SetSiblingIndex(Mathf.Clamp(insertSiblingIndex, 0, parentBoardRoot.childCount - 1));
        }

        BattleVfxManager mgr = go.GetComponent<BattleVfxManager>();
        mgr.Initialize(parentBoardRoot);
        return mgr;
    }

    private void Initialize(RectTransform shakeTarget)
    {
        if (isInitialized) return;
        isInitialized = true;

        vfxRootRT = GetComponent<RectTransform>();
        vfxRootRT.anchorMin = Vector2.zero;
        vfxRootRT.anchorMax = Vector2.one;
        vfxRootRT.offsetMin = Vector2.zero;
        vfxRootRT.offsetMax = Vector2.zero;

        boardShakeTarget = shakeTarget;
        boardShakeRestPos = shakeTarget != null ? shakeTarget.anchoredPosition : Vector2.zero;

        proceduralAtlasTex = CreateProceduralVfxAtlas();
        Shader addShader = Resources.Load<Shader>("BattleVfxAdditive");
        if (addShader != null)
        {
            additiveMaterial = new Material(addShader);
        }

        // Lớp 1 (Alpha Blend): Nền đường đạn lửa đậm + Các khối đa giác vàng sắc nét khi phá hủy
        solidAlphaLayer = CreateLayer<VfxSolidMeshLayer>("VfxSolidAlphaLayer", null);
        solidAlphaLayer.onPopulateMesh = PopulateSolidAlphaMesh;

        // Lớp 2 (Additive Blend): Lõi đường đạn sáng rực + Vòng sóng xung kích + Ánh lóe mặt đa giác vàng
        solidAdditiveLayer = CreateLayer<VfxSolidMeshLayer>("VfxSolidAdditiveLayer", additiveMaterial);
        solidAdditiveLayer.onPopulateMesh = PopulateSolidAdditiveMesh;

        // Lớp 3 (Additive Blend + Procedural Texture Atlas): Quả cầu lửa lớn đa lớp + Tia lửa + Bụi vàng lấp lánh
        spriteAdditiveLayer = CreateLayer<VfxSpriteAdditiveLayer>("VfxSpriteAdditiveLayer", additiveMaterial);
        spriteAdditiveLayer.SetAtlasTexture(proceduralAtlasTex);
        spriteAdditiveLayer.onPopulateMesh = PopulateSpriteAdditiveMesh;

        // Khởi tạo sẵn pool để không cấp phát mới khi chiến đấu
        for (int i = 0; i < 6; i++) fireballPool.Add(new FireballInstance());
        for (int i = 0; i < 160; i++) shardPool.Add(default);
        for (int i = 0; i < 240; i++) particlePool.Add(default);
        for (int i = 0; i < 16; i++) ringPool.Add(default);
        for (int i = 0; i < 6; i++) cardPopPool.Add(default);
    }

    private T CreateLayer<T>(string objName, Material mat) where T : MaskableGraphic
    {
        GameObject obj = new GameObject(objName, typeof(RectTransform), typeof(CanvasRenderer), typeof(T));
        obj.transform.SetParent(transform, false);

        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        T graphic = obj.GetComponent<T>();
        graphic.raycastTarget = false;
        graphic.maskable = false;
        if (mat != null) graphic.material = mat;
        return graphic;
    }

    // ==================== API CÔNG KHAI ====================

    /// <summary>
    /// Chuyển đổi tâm của bất kỳ RectTransform nào (lá bài hoặc khung LP ở Canvas khác) về tọa độ cục bộ của BattleVfxLayer.
    /// </summary>
    public Vector2 GetRectCenterLocal(RectTransform targetRT)
    {
        if (targetRT == null || vfxRootRT == null) return Vector2.zero;

        Canvas srcCanvas = targetRT.GetComponentInParent<Canvas>();
        Camera srcCam = (srcCanvas != null && srcCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? srcCanvas.worldCamera
            : null;

        Canvas dstCanvas = vfxRootRT.GetComponentInParent<Canvas>();
        Camera dstCam = (dstCanvas != null && dstCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? dstCanvas.worldCamera
            : null;

        Vector3 worldCenter = targetRT.TransformPoint(targetRT.rect.center);
        Vector2 screenPt = RectTransformUtility.WorldToScreenPoint(srcCam, worldCenter);
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(vfxRootRT, screenPt, dstCam, out Vector2 localPt))
        {
            return localPt;
        }
        return Vector2.zero;
    }

    /// <summary>
    /// Phóng quả cầu lửa lớn với đường đạn rõ nét từ chính giữa lá bài tấn công tới mục tiêu.
    /// </summary>
    public float PlayFireballAttack(
        RectTransform attackerSlotRT,
        RectTransform attackerVisualRT,
        Vector2 targetLocalPos,
        Action onImpact = null,
        float scaleMultiplier = 1f)
    {
        if (!isInitialized)
        {
            onImpact?.Invoke();
            return 0f;
        }

        Vector2 startLocal = GetRectCenterLocal(attackerSlotRT);
        Vector2 delta = targetLocalPos - startLocal;
        Vector2 dir = delta.sqrMagnitude > 0.01f ? delta.normalized : Vector2.up;

        FireballInstance fb = AcquireFireball();
        fb.active = true;
        fb.startPos = startLocal;
        fb.endPos = targetLocalPos;
        fb.currentPos = startLocal;
        fb.moveDir = dir;
        fb.elapsed = 0f;
        fb.chargeTime = Mathf.Max(0.02f, chargeDuration);
        fb.travelTime = Mathf.Max(0.12f, flightDuration);
        fb.scale = Mathf.Max(0.5f, fireballScale * scaleMultiplier);
        fb.impacted = false;
        fb.spinA = UnityEngine.Random.Range(0f, 360f);
        fb.spinB = UnityEngine.Random.Range(0f, 360f);
        fb.sparkCarry = 0f;
        fb.onImpact = onImpact;
        fb.trail.Clear();
        fb.trail.Add(new TrailNode { pos = startLocal, age = 0f });

        // Hiệu ứng giật nhẹ lá bài tấn công khi tụ cầu lửa
        if (attackerVisualRT != null)
        {
            TriggerCardPop(attackerVisualRT, peakScale: 1.16f, duration: 0.24f);
        }

        // Vòng tụ lửa & tia lửa khởi phát ngay chính giữa lá bài tấn công
        SpawnShockwaveRing(
            startLocal,
            startRadius: 10f,
            endRadius: 72f * fb.scale,
            startThickness: 12f,
            lifetime: 0.22f,
            startColor: new Color(1f, 0.92f, 0.45f, 0.95f),
            endColor: new Color(1f, 0.35f, 0.02f, 0f),
            segments: 28
        );

        SpawnParticle(
            pos: startLocal,
            vel: Vector2.zero,
            drag: 0f,
            gravityY: 0f,
            lifetime: 0.18f,
            startSize: 96f * fb.scale,
            endSize: 145f * fb.scale,
            aspectX: 1f,
            rotation: UnityEngine.Random.Range(0f, 90f),
            rotSpeed: 120f,
            alignToVelocity: false,
            tileIndex: 3,
            startColor: new Color(1f, 0.94f, 0.65f, 0.95f),
            endColor: new Color(1f, 0.42f, 0.05f, 0f)
        );

        for (int i = 0; i < 10; i++)
        {
            float ang = (i / 10f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.22f, 0.22f);
            float spd = UnityEngine.Random.Range(140f, 320f);
            SpawnParticle(
                pos: startLocal + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 12f,
                vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd,
                drag: 5.5f,
                gravityY: 0f,
                lifetime: UnityEngine.Random.Range(0.18f, 0.30f),
                startSize: UnityEngine.Random.Range(16f, 28f),
                endSize: 0f,
                aspectX: 2.3f,
                rotation: ang * Mathf.Rad2Deg,
                rotSpeed: 0f,
                alignToVelocity: true,
                tileIndex: 2,
                startColor: FireYellow,
                endColor: new Color(1f, 0.25f, 0.02f, 0f)
            );
        }

        MarkLayersDirty();
        return fb.chargeTime + fb.travelTime;
    }

    /// <summary>
    /// Phá hủy (Destroy): Quái thú vỡ vụn thành các khối đa giác (polygon pieces) màu vàng và tan biến dần.
    /// </summary>
    public void PlayDestroyShatter(RectTransform slotRT, bool isDefense)
    {
        if (!isInitialized || slotRT == null) return;
        Vector2 center = GetRectCenterLocal(slotRT);
        PlayDestroyShatterAtLocal(center, isDefense);
    }

    public void PlayDestroyShatterAtLocal(Vector2 center, bool isDefense)
    {
        if (!isInitialized) return;

        // Kích thước thực của lá bài trên sân (đổi chiều nếu đang xoay ngang ở thế Phòng thủ)
        float cardW = isDefense ? 132f : 104f;
        float cardH = isDefense ? 104f : 132f;
        int cols = isDefense ? 5 : 4;
        int rows = isDefense ? 4 : 5;

        // Tạo lưới đỉnh ngẫu nhiên (jittered grid) phủ kín lá bài để cắt thành các mảnh đa giác khớp nhau
        Vector2[,] grid = new Vector2[cols + 1, rows + 1];
        float cellW = cardW / cols;
        float cellH = cardH / rows;
        float halfW = cardW * 0.5f;
        float halfH = cardH * 0.5f;

        for (int x = 0; x <= cols; x++)
        {
            for (int y = 0; y <= rows; y++)
            {
                float gx = -halfW + x * cellW;
                float gy = -halfH + y * cellH;

                bool borderX = (x == 0 || x == cols);
                bool borderY = (y == 0 || y == rows);

                if (!borderX) gx += UnityEngine.Random.Range(-cellW * 0.36f, cellW * 0.36f);
                if (!borderY) gy += UnityEngine.Random.Range(-cellH * 0.36f, cellH * 0.36f);

                grid[x, y] = new Vector2(gx, gy);
            }
        }

        // Cắt từng ô lưới thành khối đa giác 3, 4 hoặc 5 đỉnh màu vàng hoàng kim
        for (int x = 0; x < cols; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                Vector2 p00 = grid[x, y];
                Vector2 p10 = grid[x + 1, y];
                Vector2 p11 = grid[x + 1, y + 1];
                Vector2 p01 = grid[x, y + 1];

                float roll = UnityEngine.Random.value;
                if (roll < 0.48f)
                {
                    // Tách thành 2 mảnh tam giác sắc cạnh (polygon 3 đỉnh)
                    if (UnityEngine.Random.value < 0.5f)
                    {
                        EmitGoldenPolygonShard(center, p00, p10, p11);
                        EmitGoldenPolygonShard(center, p00, p11, p01);
                    }
                    else
                    {
                        EmitGoldenPolygonShard(center, p00, p10, p01);
                        EmitGoldenPolygonShard(center, p10, p11, p01);
                    }
                }
                else if (roll < 0.80f)
                {
                    // Giữ nguyên mảnh tứ giác bất quy tắc (polygon 4 đỉnh)
                    EmitGoldenPolygonShard(center, p00, p10, p11, p01);
                }
                else
                {
                    // Tạo mảnh ngũ giác pha lê (polygon 5 đỉnh) + 1 mảnh góc nhỏ
                    Vector2 midTop = Vector2.Lerp(p01, p11, UnityEngine.Random.Range(0.35f, 0.65f));
                    Vector2 midRight = Vector2.Lerp(p10, p11, UnityEngine.Random.Range(0.35f, 0.65f));
                    EmitGoldenPolygonShard(center, p00, p10, midRight, midTop, p01);
                    EmitGoldenPolygonShard(center, midRight, p11, midTop);
                }
            }
        }

        // Vầng sáng chói màu vàng tại thời điểm quái thú bắt đầu vỡ vụn
        SpawnParticle(
            pos: center,
            vel: Vector2.zero,
            drag: 0f,
            gravityY: 0f,
            lifetime: 0.24f,
            startSize: 165f,
            endSize: 225f,
            aspectX: 1f,
            rotation: UnityEngine.Random.Range(0f, 45f),
            rotSpeed: 65f,
            alignToVelocity: false,
            tileIndex: 0,
            startColor: new Color(1f, 0.94f, 0.45f, 0.88f),
            endColor: new Color(1f, 0.72f, 0.05f, 0f)
        );

        SpawnParticle(
            pos: center,
            vel: Vector2.zero,
            drag: 0f,
            gravityY: 0f,
            lifetime: 0.28f,
            startSize: 155f,
            endSize: 195f,
            aspectX: 1f,
            rotation: 0f,
            rotSpeed: 40f,
            alignToVelocity: false,
            tileIndex: 3,
            startColor: new Color(1f, 0.98f, 0.72f, 0.95f),
            endColor: new Color(1f, 0.80f, 0.12f, 0f)
        );

        // Vòng sóng vỡ màu vàng lan tỏa nhanh
        SpawnShockwaveRing(
            center,
            startRadius: 20f,
            endRadius: 128f,
            startThickness: 10f,
            lifetime: 0.34f,
            startColor: new Color(1f, 0.92f, 0.38f, 0.95f),
            endColor: new Color(1f, 0.68f, 0.05f, 0f),
            segments: 8 // Vòng đa giác 8 cạnh khớp thẩm mỹ vỡ polygon!
        );

        // Bụi ánh sáng màu vàng lấp lánh bay tản ra và tan dần lên trên
        for (int i = 0; i < 24; i++)
        {
            float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float dist = UnityEngine.Random.Range(0f, 46f);
            Vector2 pPos = center + new Vector2(Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist);
            float spd = UnityEngine.Random.Range(85f, 260f);
            Vector2 pVel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd + new Vector2(0f, UnityEngine.Random.Range(20f, 70f));

            bool isStar = (i % 2 == 0);
            SpawnParticle(
                pos: pPos,
                vel: pVel,
                drag: 2.8f,
                gravityY: 55f, // Bay nhẹ lên như mảnh sáng hologram tan biến
                lifetime: UnityEngine.Random.Range(0.55f, 0.98f),
                startSize: isStar ? UnityEngine.Random.Range(14f, 26f) : UnityEngine.Random.Range(8f, 16f),
                endSize: 0f,
                aspectX: 1f,
                rotation: UnityEngine.Random.Range(0f, 180f),
                rotSpeed: UnityEngine.Random.Range(-180f, 180f),
                alignToVelocity: false,
                tileIndex: isStar ? 3 : 0,
                startColor: isStar ? GoldSpecular : GoldBright,
                endColor: new Color(1f, 0.72f, 0.05f, 0f)
            );
        }

        MarkLayersDirty();
    }

    public void ClearAllVfx()
    {
        for (int i = 0; i < fireballPool.Count; i++)
        {
            fireballPool[i].active = false;
            fireballPool[i].onImpact = null;
            fireballPool[i].trail.Clear();
        }
        for (int i = 0; i < shardPool.Count; i++)
        {
            PolygonShard s = shardPool[i];
            s.active = false;
            shardPool[i] = s;
        }
        for (int i = 0; i < particlePool.Count; i++)
        {
            VfxParticle p = particlePool[i];
            p.active = false;
            particlePool[i] = p;
        }
        for (int i = 0; i < ringPool.Count; i++)
        {
            ShockwaveRing r = ringPool[i];
            r.active = false;
            ringPool[i] = r;
        }
        for (int i = 0; i < cardPopPool.Count; i++)
        {
            CardPopAnim c = cardPopPool[i];
            if (c.active && c.target != null) c.target.localScale = c.baseScale;
            c.active = false;
            cardPopPool[i] = c;
        }
        if (boardShakeTarget != null)
        {
            boardShakeTarget.anchoredPosition = boardShakeRestPos;
        }
        shakeTimer = 0f;
        MarkLayersDirty();
    }

    // ==================== UPDATE LOOP ====================

    void Update()
    {
        if (!isInitialized) return;

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (dt <= 0f) return;

        bool anyActive = false;

        // 1. Cập nhật các quả cầu lửa & đường đạn
        for (int i = 0; i < fireballPool.Count; i++)
        {
            FireballInstance fb = fireballPool[i];
            if (!fb.active) continue;

            fb.elapsed += dt;
            fb.spinA += 480f * dt;
            fb.spinB -= 650f * dt;

            // Già hóa các điểm đường đạn hiện có
            float maxTrailAge = Mathf.Max(0.08f, trailLifetime);
            for (int t = fb.trail.Count - 1; t >= 0; t--)
            {
                TrailNode node = fb.trail[t];
                node.age += dt;
                if (node.age >= maxTrailAge)
                {
                    fb.trail.RemoveAt(t);
                }
                else
                {
                    fb.trail[t] = node;
                }
            }

            if (!fb.impacted)
            {
                if (fb.elapsed <= fb.chargeTime)
                {
                    // Pha tụ năng lượng tại chính giữa lá bài tấn công
                    fb.currentPos = fb.startPos;
                    if (fb.trail.Count == 0)
                    {
                        fb.trail.Add(new TrailNode { pos = fb.startPos, age = 0f });
                    }
                    else
                    {
                        TrailNode last = fb.trail[fb.trail.Count - 1];
                        last.pos = fb.startPos;
                        last.age = 0f;
                        fb.trail[fb.trail.Count - 1] = last;
                    }
                }
                else
                {
                    float flightT = Mathf.Clamp01((fb.elapsed - fb.chargeTime) / fb.travelTime);
                    // Gia tốc nhẹ để quả cầu lao đi uy lực và đập trúng mục tiêu ở vận tốc cao nhất
                    float easedT = Mathf.Lerp(flightT, flightT * flightT, 0.34f);

                    Vector2 prevPos = fb.currentPos;
                    Vector2 nextPos = Vector2.LerpUnclamped(fb.startPos, fb.endPos, easedT);

                    // Chia bước mịn để đường đạn luôn sắc nét, không gãy khúc ngay cả khi FPS thấp
                    float segDist = Vector2.Distance(prevPos, nextPos);
                    int subSteps = Mathf.Clamp(Mathf.CeilToInt(segDist / 12f), 1, 8);
                    for (int s = 1; s <= subSteps; s++)
                    {
                        float frac = s / (float)subSteps;
                        Vector2 sample = Vector2.Lerp(prevPos, nextPos, frac);
                        float subAge = (1f - frac) * dt;
                        fb.trail.Add(new TrailNode { pos = sample, age = subAge });
                        if (fb.trail.Count > 120) fb.trail.RemoveAt(0);
                    }

                    fb.currentPos = nextPos;

                    // Rải tia lửa tàn lửa dọc theo đường đạn
                    fb.sparkCarry += dt * 115f;
                    Vector2 perp = new Vector2(-fb.moveDir.y, fb.moveDir.x);
                    while (fb.sparkCarry >= 1f)
                    {
                        fb.sparkCarry -= 1f;
                        float sideOffset = UnityEngine.Random.Range(-20f, 20f) * fb.scale;
                        Vector2 spawnPt = fb.currentPos - fb.moveDir * UnityEngine.Random.Range(4f, 26f) + perp * sideOffset;
                        Vector2 sparkVel = -fb.moveDir * UnityEngine.Random.Range(110f, 280f) + perp * UnityEngine.Random.Range(-130f, 130f);

                        bool brightSpark = UnityEngine.Random.value < 0.45f;
                        SpawnParticle(
                            pos: spawnPt,
                            vel: sparkVel,
                            drag: 4.2f,
                            gravityY: 0f,
                            lifetime: UnityEngine.Random.Range(0.16f, 0.32f),
                            startSize: UnityEngine.Random.Range(12f, 24f) * fb.scale,
                            endSize: 0f,
                            aspectX: brightSpark ? 2.4f : 1.6f,
                            rotation: 0f,
                            rotSpeed: 0f,
                            alignToVelocity: true,
                            tileIndex: 2,
                            startColor: brightSpark ? FireWhiteHot : FireOrange,
                            endColor: new Color(0.95f, 0.18f, 0.01f, 0f)
                        );
                    }

                    if (flightT >= 1f)
                    {
                        fb.impacted = true;
                        fb.currentPos = fb.endPos;
                        fb.trail.Add(new TrailNode { pos = fb.endPos, age = 0f });
                        SpawnFireballImpact(fb.endPos, fb.scale);

                        Action cb = fb.onImpact;
                        fb.onImpact = null;
                        cb?.Invoke();
                    }
                }
                anyActive = true;
            }
            else
            {
                if (fb.trail.Count <= 1)
                {
                    fb.active = false;
                    fb.trail.Clear();
                }
                else
                {
                    anyActive = true;
                }
            }
        }

        // 2. Cập nhật các khối đa giác màu vàng (Golden Polygon Shards)
        float shardDrag = Mathf.Exp(-2.3f * dt);
        for (int i = 0; i < shardPool.Count; i++)
        {
            PolygonShard s = shardPool[i];
            if (!s.active) continue;

            s.age += dt;
            if (s.age >= s.lifetime)
            {
                s.active = false;
                shardPool[i] = s;
                continue;
            }

            s.vel *= shardDrag;
            s.vel.y += 42f * dt; // Bay nhẹ dần lên và tan biến trong không trung
            s.pos += s.vel * dt;
            s.rotZ += s.rotZSpeed * dt;
            s.tumblePhase += s.tumbleSpeed * dt;

            shardPool[i] = s;
            anyActive = true;
        }

        // 3. Cập nhật hạt tia lửa & bụi vàng
        for (int i = 0; i < particlePool.Count; i++)
        {
            VfxParticle p = particlePool[i];
            if (!p.active) continue;

            p.age += dt;
            if (p.age >= p.lifetime)
            {
                p.active = false;
                particlePool[i] = p;
                continue;
            }

            if (p.drag > 0f) p.vel *= Mathf.Exp(-p.drag * dt);
            p.vel.y += p.gravityY * dt;
            p.pos += p.vel * dt;
            p.rotation += p.rotSpeed * dt;

            particlePool[i] = p;
            anyActive = true;
        }

        // 4. Cập nhật vòng sóng xung kích
        for (int i = 0; i < ringPool.Count; i++)
        {
            ShockwaveRing r = ringPool[i];
            if (!r.active) continue;

            r.age += dt;
            if (r.age >= r.lifetime)
            {
                r.active = false;
                ringPool[i] = r;
                continue;
            }

            ringPool[i] = r;
            anyActive = true;
        }

        // 5. Cập nhật hiệu ứng giật/nhấn của lá bài tấn công
        for (int i = 0; i < cardPopPool.Count; i++)
        {
            CardPopAnim c = cardPopPool[i];
            if (!c.active) continue;

            c.age += dt;
            if (c.target == null || c.age >= c.duration)
            {
                if (c.target != null) c.target.localScale = c.baseScale;
                c.active = false;
                cardPopPool[i] = c;
                continue;
            }

            float u = c.age / c.duration;
            float scaleMul = u < 0.35f
                ? Mathf.Lerp(1f, c.peakScale, EaseOutCubic(u / 0.35f))
                : Mathf.Lerp(c.peakScale, 1f, EaseOutCubic((u - 0.35f) / 0.65f));
            c.target.localScale = c.baseScale * scaleMul;
            cardPopPool[i] = c;
        }

        // 6. Rung nhẹ bàn đấu khi va chạm
        if (shakeTimer > 0f && boardShakeTarget != null)
        {
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

        if (anyActive || hadActiveVfxLastFrame)
        {
            MarkLayersDirty();
            hadActiveVfxLastFrame = anyActive;
        }
    }

    private void MarkLayersDirty()
    {
        if (solidAlphaLayer != null) solidAlphaLayer.SetVerticesDirty();
        if (solidAdditiveLayer != null) solidAdditiveLayer.SetVerticesDirty();
        if (spriteAdditiveLayer != null) spriteAdditiveLayer.SetVerticesDirty();
    }

    // ==================== VỤ NỔ VA CHẠM QUẢ CẦU LỬA ====================

    private void SpawnFireballImpact(Vector2 pos, float scale)
    {
        // Chớp lửa rực sáng tại tâm va chạm
        SpawnParticle(
            pos: pos,
            vel: Vector2.zero,
            drag: 0f,
            gravityY: 0f,
            lifetime: 0.20f,
            startSize: 195f * scale,
            endSize: 270f * scale,
            aspectX: 1f,
            rotation: UnityEngine.Random.Range(0f, 360f),
            rotSpeed: 90f,
            alignToVelocity: false,
            tileIndex: 0,
            startColor: new Color(1f, 0.96f, 0.72f, 1f),
            endColor: new Color(1f, 0.32f, 0.02f, 0f)
        );

        SpawnParticle(
            pos: pos,
            vel: Vector2.zero,
            drag: 0f,
            gravityY: 0f,
            lifetime: 0.24f,
            startSize: 185f * scale,
            endSize: 235f * scale,
            aspectX: 1f,
            rotation: UnityEngine.Random.Range(0f, 45f),
            rotSpeed: -110f,
            alignToVelocity: false,
            tileIndex: 3,
            startColor: FireWhiteHot,
            endColor: new Color(1f, 0.55f, 0.04f, 0f)
        );

        // Hai vòng sóng nổ lửa đồng tâm
        SpawnShockwaveRing(
            pos,
            startRadius: 22f * scale,
            endRadius: 158f * scale,
            startThickness: 18f * scale,
            lifetime: 0.26f,
            startColor: new Color(1f, 0.72f, 0.16f, 0.95f),
            endColor: new Color(1f, 0.18f, 0.02f, 0f),
            segments: 32
        );
        SpawnShockwaveRing(
            pos,
            startRadius: 14f * scale,
            endRadius: 112f * scale,
            startThickness: 11f * scale,
            lifetime: 0.20f,
            startColor: new Color(1f, 0.98f, 0.80f, 1f),
            endColor: new Color(1f, 0.55f, 0.08f, 0f),
            segments: 28
        );

        // 28 tia lửa nổ văng ra mọi hướng
        for (int i = 0; i < 28; i++)
        {
            float ang = (i / 28f) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.14f, 0.14f);
            float spd = UnityEngine.Random.Range(240f, 620f) * scale;
            bool isHotCore = (i % 3 == 0);
            SpawnParticle(
                pos: pos + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 10f,
                vel: new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * spd,
                drag: 4.8f,
                gravityY: 0f,
                lifetime: UnityEngine.Random.Range(0.24f, 0.44f),
                startSize: UnityEngine.Random.Range(16f, 34f) * scale,
                endSize: 0f,
                aspectX: isHotCore ? 2.6f : 2.1f,
                rotation: ang * Mathf.Rad2Deg,
                rotSpeed: 0f,
                alignToVelocity: true,
                tileIndex: 2,
                startColor: isHotCore ? FireWhiteHot : (i % 2 == 0 ? FireYellow : FireOrange),
                endColor: new Color(0.95f, 0.14f, 0.02f, 0f)
            );
        }

        // Rung nhẹ bàn đấu tạo cảm giác va đập uy lực
        if (boardShakeTarget != null && impactShakeAmplitude > 0.1f)
        {
            shakeDuration = 0.22f;
            shakeTimer = shakeDuration;
            shakeStrength = impactShakeAmplitude * scale;
        }
    }

    // ==================== SINH MẢNH ĐA GIÁC MÀU VÀNG ====================

    private void EmitGoldenPolygonShard(Vector2 cardCenter, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 centroid = (a + b + c) / 3f;
        AddShardToPool(cardCenter, centroid, 3, a - centroid, b - centroid, c - centroid, Vector2.zero, Vector2.zero);
    }

    private void EmitGoldenPolygonShard(Vector2 cardCenter, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        Vector2 centroid = (a + b + c + d) * 0.25f;
        AddShardToPool(cardCenter, centroid, 4, a - centroid, b - centroid, c - centroid, d - centroid, Vector2.zero);
    }

    private void EmitGoldenPolygonShard(Vector2 cardCenter, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 e)
    {
        Vector2 centroid = (a + b + c + d + e) * 0.2f;
        AddShardToPool(cardCenter, centroid, 5, a - centroid, b - centroid, c - centroid, d - centroid, e - centroid);
    }

    private void AddShardToPool(
        Vector2 cardCenter,
        Vector2 localCentroid,
        int vertCount,
        Vector2 v0, Vector2 v1, Vector2 v2, Vector2 v3, Vector2 v4)
    {
        int idx = AcquireShardIndex();
        Vector2 outward = localCentroid.sqrMagnitude > 1f
            ? localCentroid.normalized
            : new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)).normalized;

        // Xoay nhẹ hướng bắn để các khối đa giác tản tự nhiên
        float jitterAng = UnityEngine.Random.Range(-0.42f, 0.42f);
        float cosJ = Mathf.Cos(jitterAng);
        float sinJ = Mathf.Sin(jitterAng);
        Vector2 burstDir = new Vector2(outward.x * cosJ - outward.y * sinJ, outward.x * sinJ + outward.y * cosJ);

        float distRatio = Mathf.Clamp01(localCentroid.magnitude / 75f);
        float speed = Mathf.Lerp(105f, 305f, distRatio) * UnityEngine.Random.Range(0.75f, 1.28f);

        float axisAng = UnityEngine.Random.Range(0f, Mathf.PI * 2f);

        // Phối sắc vàng hoàng kim phong phú cho từng khối đa giác
        float tint = UnityEngine.Random.value;
        Color coreCol = Color.Lerp(GoldBright, GoldSpecular, tint * 0.65f);
        Color edgeCol = Color.Lerp(GoldRich, GoldDeep, UnityEngine.Random.Range(0.15f, 0.75f));

        shardPool[idx] = new PolygonShard
        {
            active = true,
            pos = cardCenter + localCentroid,
            vel = burstDir * speed + new Vector2(0f, UnityEngine.Random.Range(10f, 55f)),
            rotZ = 0f,
            rotZSpeed = UnityEngine.Random.Range(-340f, 340f),
            tumblePhase = UnityEngine.Random.Range(0f, Mathf.PI * 0.35f),
            tumbleSpeed = UnityEngine.Random.Range(4.8f, 10.5f),
            tumbleAxis = new Vector2(Mathf.Cos(axisAng), Mathf.Sin(axisAng)),
            age = 0f,
            lifetime = UnityEngine.Random.Range(shardMinLifetime, shardMaxLifetime),
            vertCount = vertCount,
            v0 = v0,
            v1 = v1,
            v2 = v2,
            v3 = v3,
            v4 = v4,
            coreGold = coreCol,
            edgeGold = edgeCol
        };
    }

    // ==================== BUILD MESH CHO 3 LỚP RENDER ====================

    private void PopulateSolidAlphaMesh(VertexHelper vh)
    {
        // 1. Vẽ nền đậm của đường đạn quả cầu lửa (tạo độ tương phản sắc nét ngay cả trên nền lá bài sáng)
        float maxTrailAge = Mathf.Max(0.08f, trailLifetime);
        for (int i = 0; i < fireballPool.Count; i++)
        {
            FireballInstance fb = fireballPool[i];
            if (!fb.active) continue;

            if (fb.trail.Count >= 2)
            {
                AddTaperedRibbon(
                    vh,
                    fb.trail,
                    maxTrailAge,
                    headHalfWidth: trailHeadWidth * 0.56f * fb.scale,
                    headColor: new Color(FireOrange.r, FireOrange.g, FireOrange.b, 0.88f),
                    midColor: new Color(FireCrimson.r, FireCrimson.g, FireCrimson.b, 0.58f),
                    tailColor: FireDarkRed,
                    waveAmp: 0f,
                    waveFreq: 0f,
                    wavePhase: 0f
                );
            }

            // Lõi nền đặc của quả cầu lửa để giữ hình khối lớn rõ ràng
            if (!fb.impacted)
            {
                float chargeMul = fb.elapsed < fb.chargeTime
                    ? Mathf.Lerp(0.35f, 1f, EaseOutCubic(fb.elapsed / fb.chargeTime))
                    : 1f;
                AddFilledDisc(
                    vh,
                    fb.currentPos,
                    radius: 31f * fb.scale * chargeMul,
                    centerColor: new Color(1f, 0.78f, 0.20f, 0.92f),
                    edgeColor: new Color(0.95f, 0.22f, 0.02f, 0.0f),
                    segments: 20
                );
            }
        }

        // 2. Vẽ các khối đa giác màu vàng (Polygon Pieces) khi quái thú bị phá hủy
        Vector2 lightDir = new Vector2(-0.55f, 0.83f).normalized;
        for (int i = 0; i < shardPool.Count; i++)
        {
            PolygonShard s = shardPool[i];
            if (!s.active) continue;

            float t = Mathf.Clamp01(s.age / s.lifetime);
            // Giữ rõ nét lúc đầu rồi thu nhỏ & tan biến mượt dần (không biến mất đột ngột)
            float scale = t < 0.22f ? 1f : Mathf.Lerp(1f, 0.08f, EaseInQuad((t - 0.22f) / 0.78f));
            float alpha = t < 0.28f ? 1f : 1f - SmoothStep((t - 0.28f) / 0.72f);
            if (alpha <= 0.01f || scale <= 0.02f) continue;

            // Giả lập xoay lật 3D theo trục tumbleAxis để ánh sáng lướt trên từng mặt đa giác vàng
            float cosTumble = Mathf.Abs(Mathf.Cos(s.tumblePhase));
            float tumbleScale = Mathf.Lerp(0.16f, 1f, cosTumble);
            float gleam = Mathf.Pow(cosTumble, 2.2f);

            Color cCenter = Color.Lerp(s.coreGold, GoldSpecular, gleam * 0.75f);
            cCenter.a = alpha;

            int centerVertIdx = vh.currentVertCount;
            UIVertex vc = UIVertex.simpleVert;
            vc.position = s.pos;
            vc.color = cCenter;
            vc.uv0 = Vector2.zero;
            vh.AddVert(vc);

            float rad = s.rotZ * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rad);
            float sinR = Mathf.Sin(rad);

            int rimStart = vh.currentVertCount;
            for (int v = 0; v < s.vertCount; v++)
            {
                Vector2 lv = GetShardVertex(in s, v) * scale;

                // Nén theo trục tumble để tạo hiệu ứng khối đa giác lật trong không gian 3D
                float proj = Vector2.Dot(lv, s.tumbleAxis);
                Vector2 along = s.tumbleAxis * proj;
                Vector2 ortho = lv - along;
                Vector2 tumbled = ortho + along * tumbleScale;

                // Xoay 2D quanh tâm mảnh
                Vector2 rotated = new Vector2(
                    tumbled.x * cosR - tumbled.y * sinR,
                    tumbled.x * sinR + tumbled.y * cosR
                );

                // Đổ bóng cạnh đa giác (faceted shading) cho cảm giác khối pha lê vàng
                Vector2 nDir = rotated.sqrMagnitude > 0.001f ? rotated.normalized : Vector2.up;
                float facet = 0.5f + 0.5f * Vector2.Dot(nDir, lightDir);
                Color vCol = Color.Lerp(s.edgeGold, GoldBright, facet * 0.72f + gleam * 0.28f);
                vCol.a = alpha;

                UIVertex vr = UIVertex.simpleVert;
                vr.position = s.pos + rotated;
                vr.color = vCol;
                vr.uv0 = Vector2.zero;
                vh.AddVert(vr);
            }

            for (int v = 0; v < s.vertCount; v++)
            {
                int nextV = (v + 1) % s.vertCount;
                vh.AddTriangle(centerVertIdx, rimStart + v, rimStart + nextV);
            }
        }
    }

    private void PopulateSolidAdditiveMesh(VertexHelper vh)
    {
        float maxTrailAge = Mathf.Max(0.08f, trailLifetime);

        // 1. Đường đạn phát sáng đa lớp (Plasma ngoài + Lõi tia sáng trắng-vàng sắc nét + 2 dải lửa xoắn)
        for (int i = 0; i < fireballPool.Count; i++)
        {
            FireballInstance fb = fireballPool[i];
            if (!fb.active || fb.trail.Count < 2) continue;

            // Lớp plasma cam-vàng rực rỡ
            AddTaperedRibbon(
                vh,
                fb.trail,
                maxTrailAge,
                headHalfWidth: trailHeadWidth * 0.44f * fb.scale,
                headColor: new Color(1.00f, 0.76f, 0.16f, 0.95f),
                midColor: new Color(1.00f, 0.38f, 0.04f, 0.65f),
                tailColor: new Color(1.00f, 0.12f, 0.00f, 0.0f),
                waveAmp: 0f,
                waveFreq: 0f,
                wavePhase: 0f
            );

            // Lõi tia đạn trắng-vàng siêu sắc nét ở chính giữa đường bay
            AddTaperedRibbon(
                vh,
                fb.trail,
                maxTrailAge,
                headHalfWidth: trailHeadWidth * 0.19f * fb.scale,
                headColor: new Color(1.00f, 1.00f, 0.92f, 1.0f),
                midColor: new Color(1.00f, 0.92f, 0.48f, 0.85f),
                tailColor: new Color(1.00f, 0.58f, 0.06f, 0.0f),
                waveAmp: 0f,
                waveFreq: 0f,
                wavePhase: 0f
            );

            // 2 dải lửa xoắn ốc mỏng cuộn quanh đường đạn tăng độ sống động
            float phase = fb.elapsed * 26f;
            AddTaperedRibbon(
                vh,
                fb.trail,
                maxTrailAge,
                headHalfWidth: 4.5f * fb.scale,
                headColor: new Color(1.00f, 0.86f, 0.35f, 0.80f),
                midColor: new Color(1.00f, 0.45f, 0.05f, 0.50f),
                tailColor: new Color(1.00f, 0.15f, 0.00f, 0.0f),
                waveAmp: 17f * fb.scale,
                waveFreq: 14f,
                wavePhase: phase
            );
            AddTaperedRibbon(
                vh,
                fb.trail,
                maxTrailAge,
                headHalfWidth: 4.5f * fb.scale,
                headColor: new Color(1.00f, 0.78f, 0.25f, 0.75f),
                midColor: new Color(1.00f, 0.35f, 0.03f, 0.45f),
                tailColor: new Color(1.00f, 0.12f, 0.00f, 0.0f),
                waveAmp: -17f * fb.scale,
                waveFreq: 14f,
                wavePhase: phase + Mathf.PI
            );
        }

        // 2. Ánh lóe vàng phát sáng trên các khối đa giác khi mặt khối hướng đúng góc bắt sáng
        for (int i = 0; i < shardPool.Count; i++)
        {
            PolygonShard s = shardPool[i];
            if (!s.active) continue;

            float cosTumble = Mathf.Abs(Mathf.Cos(s.tumblePhase));
            if (cosTumble < 0.72f) continue;

            float t = Mathf.Clamp01(s.age / s.lifetime);
            float scale = t < 0.22f ? 1f : Mathf.Lerp(1f, 0.08f, EaseInQuad((t - 0.22f) / 0.78f));
            float alpha = (t < 0.28f ? 1f : 1f - SmoothStep((t - 0.28f) / 0.72f)) * ((cosTumble - 0.72f) / 0.28f) * 0.65f;
            if (alpha <= 0.02f) continue;

            float tumbleScale = Mathf.Lerp(0.16f, 1f, cosTumble);
            float rad = s.rotZ * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rad);
            float sinR = Mathf.Sin(rad);

            Color glintCol = new Color(1f, 0.96f, 0.58f, alpha);
            int centerIdx = vh.currentVertCount;
            UIVertex vc = UIVertex.simpleVert;
            vc.position = s.pos;
            vc.color = glintCol;
            vc.uv0 = Vector2.zero;
            vh.AddVert(vc);

            int rimStart = vh.currentVertCount;
            for (int v = 0; v < s.vertCount; v++)
            {
                Vector2 lv = GetShardVertex(in s, v) * (scale * 1.06f);
                float proj = Vector2.Dot(lv, s.tumbleAxis);
                Vector2 tumbled = (lv - s.tumbleAxis * proj) + s.tumbleAxis * (proj * tumbleScale);
                Vector2 rotated = new Vector2(tumbled.x * cosR - tumbled.y * sinR, tumbled.x * sinR + tumbled.y * cosR);

                UIVertex vr = UIVertex.simpleVert;
                vr.position = s.pos + rotated;
                vr.color = new Color(1f, 0.82f, 0.15f, alpha * 0.45f);
                vr.uv0 = Vector2.zero;
                vh.AddVert(vr);
            }

            for (int v = 0; v < s.vertCount; v++)
            {
                vh.AddTriangle(centerIdx, rimStart + v, rimStart + ((v + 1) % s.vertCount));
            }
        }

        // 3. Các vòng sóng xung kích (khởi phát, va chạm, vỡ đa giác)
        for (int i = 0; i < ringPool.Count; i++)
        {
            ShockwaveRing r = ringPool[i];
            if (!r.active) continue;

            float t = Mathf.Clamp01(r.age / r.lifetime);
            float eased = EaseOutCubic(t);
            float outerR = Mathf.Lerp(r.startRadius, r.endRadius, eased);
            float thick = Mathf.Max(1.5f, r.startThickness * (1f - t));
            float innerR = Mathf.Max(0f, outerR - thick);
            Color col = Color.Lerp(r.startColor, r.endColor, t);
            if (col.a <= 0.01f) continue;

            AddShockwaveRingGeometry(vh, r.pos, innerR, outerR, col, r.segments);
        }
    }

    private void PopulateSpriteAdditiveMesh(VertexHelper vh)
    {
        // 1. Đầu Quả Cầu Lửa Lớn (4 lớp phát sáng quay ngược chiều nhau)
        for (int i = 0; i < fireballPool.Count; i++)
        {
            FireballInstance fb = fireballPool[i];
            if (!fb.active || fb.impacted) continue;

            float chargeMul = fb.elapsed < fb.chargeTime
                ? Mathf.Lerp(0.32f, 1.08f, EaseOutCubic(fb.elapsed / fb.chargeTime))
                : 1f;
            float pulse = 1f + 0.06f * Mathf.Sin(fb.elapsed * 42f);
            float s = fb.scale * chargeMul * pulse;
            float flightAngle = Mathf.Atan2(fb.moveDir.y, fb.moveDir.x) * Mathf.Rad2Deg;

            // Lớp 1: Hào quang lửa đỏ-cam rộng rực
            AddAtlasQuad(vh, fb.currentPos, 166f * s, 166f * s, 0f, tileIndex: 0, new Color(1.00f, 0.30f, 0.02f, 0.85f));
            // Lớp 2: Quả cầu ngọn lửa xoáy chiều kim đồng hồ
            AddAtlasQuad(vh, fb.currentPos, 118f * s, 118f * s, fb.spinA, tileIndex: 1, new Color(1.00f, 0.56f, 0.06f, 0.96f));
            // Lớp 3: Quả cầu ngọn lửa vàng-cam xoáy ngược chiều
            AddAtlasQuad(vh, fb.currentPos, 94f * s, 94f * s, fb.spinB, tileIndex: 1, new Color(1.00f, 0.84f, 0.18f, 0.95f));
            // Lớp 4: Lõi nhiệt trắng-vàng chói hơi kéo dài theo hướng bay
            AddAtlasQuad(vh, fb.currentPos, 66f * s, 54f * s, flightAngle, tileIndex: 0, new Color(1.00f, 0.99f, 0.88f, 1.0f));
        }

        // 2. Tia lửa đường đạn, chớp va chạm, và bụi ánh sáng vàng khi phá hủy
        for (int i = 0; i < particlePool.Count; i++)
        {
            VfxParticle p = particlePool[i];
            if (!p.active) continue;

            float t = Mathf.Clamp01(p.age / p.lifetime);
            float size = Mathf.Lerp(p.startSize, p.endSize, EaseOutCubic(t));
            if (size <= 0.5f) continue;

            Color col = Color.Lerp(p.startColor, p.endColor, t);
            if (col.a <= 0.01f) continue;

            float angle = p.alignToVelocity && p.vel.sqrMagnitude > 1f
                ? Mathf.Atan2(p.vel.y, p.vel.x) * Mathf.Rad2Deg
                : p.rotation;

            AddAtlasQuad(vh, p.pos, size * p.aspectX, size, angle, p.tileIndex, col);
        }
    }

    // ==================== HELPER HÌNH HỌC ====================

    private void AddTaperedRibbon(
        VertexHelper vh,
        List<TrailNode> nodes,
        float maxTrailAge,
        float headHalfWidth,
        Color headColor,
        Color midColor,
        Color tailColor,
        float waveAmp,
        float waveFreq,
        float wavePhase)
    {
        int count = nodes.Count;
        if (count < 2) return;

        int prevLeft = -1;
        int prevRight = -1;

        for (int i = 0; i < count; i++)
        {
            Vector2 pos = nodes[i].pos;
            float u = count > 1 ? (i / (float)(count - 1)) : 1f;
            float ageFactor = Mathf.Clamp01(1f - nodes[i].age / maxTrailAge);
            float profile = u * ageFactor;

            // Tính vector tiếp tuyến mượt tại điểm i
            Vector2 tangent;
            if (i == 0)
            {
                tangent = nodes[1].pos - pos;
            }
            else if (i == count - 1)
            {
                tangent = pos - nodes[i - 1].pos;
            }
            else
            {
                tangent = nodes[i + 1].pos - nodes[i - 1].pos;
            }

            if (tangent.sqrMagnitude < 0.0001f)
            {
                tangent = Vector2.up;
            }
            else
            {
                tangent.Normalize();
            }

            Vector2 perp = new Vector2(-tangent.y, tangent.x);

            // Nếu là dải lửa xoắn ốc thì dịch nhẹ tâm theo sóng sin dọc đường bay
            if (Mathf.Abs(waveAmp) > 0.01f)
            {
                float env = Mathf.Sin(u * Mathf.PI) * ageFactor;
                float offset = Mathf.Sin(u * waveFreq - wavePhase) * waveAmp * env;
                pos += perp * offset;
            }

            float hw = headHalfWidth * Mathf.Pow(profile, 0.72f);

            Color col = profile >= 0.5f
                ? Color.Lerp(midColor, headColor, (profile - 0.5f) * 2f)
                : Color.Lerp(tailColor, midColor, profile * 2f);

            int curLeft = vh.currentVertCount;
            UIVertex vl = UIVertex.simpleVert;
            vl.position = pos + perp * hw;
            vl.color = col;
            vl.uv0 = Vector2.zero;
            vh.AddVert(vl);

            int curRight = vh.currentVertCount;
            UIVertex vr = UIVertex.simpleVert;
            vr.position = pos - perp * hw;
            vr.color = col;
            vr.uv0 = Vector2.zero;
            vh.AddVert(vr);

            if (prevLeft >= 0)
            {
                vh.AddTriangle(prevLeft, prevRight, curRight);
                vh.AddTriangle(prevLeft, curRight, curLeft);
            }

            prevLeft = curLeft;
            prevRight = curRight;
        }
    }

    private void AddFilledDisc(VertexHelper vh, Vector2 center, float radius, Color centerColor, Color edgeColor, int segments)
    {
        if (radius <= 0.5f) return;

        int centerIdx = vh.currentVertCount;
        UIVertex vc = UIVertex.simpleVert;
        vc.position = center;
        vc.color = centerColor;
        vc.uv0 = Vector2.zero;
        vh.AddVert(vc);

        int rimStart = vh.currentVertCount;
        for (int i = 0; i < segments; i++)
        {
            float ang = (i / (float)segments) * Mathf.PI * 2f;
            UIVertex vr = UIVertex.simpleVert;
            vr.position = center + new Vector2(Mathf.Cos(ang) * radius, Mathf.Sin(ang) * radius);
            vr.color = edgeColor;
            vr.uv0 = Vector2.zero;
            vh.AddVert(vr);
        }

        for (int i = 0; i < segments; i++)
        {
            vh.AddTriangle(centerIdx, rimStart + i, rimStart + ((i + 1) % segments));
        }
    }

    private void AddShockwaveRingGeometry(VertexHelper vh, Vector2 center, float innerR, float outerR, Color col, int segments)
    {
        int startIdx = vh.currentVertCount;
        Color innerCol = new Color(col.r, col.g, col.b, col.a * 0.25f);

        for (int i = 0; i < segments; i++)
        {
            float ang = (i / (float)segments) * Mathf.PI * 2f;
            Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));

            UIVertex vi = UIVertex.simpleVert;
            vi.position = center + dir * innerR;
            vi.color = innerCol;
            vi.uv0 = Vector2.zero;
            vh.AddVert(vi);

            UIVertex vo = UIVertex.simpleVert;
            vo.position = center + dir * outerR;
            vo.color = col;
            vo.uv0 = Vector2.zero;
            vh.AddVert(vo);
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

        // Chừa biên 0.015f cho từng ô 2x2 trong atlas để lọc bilinear không bị lem viền
        float u0 = (tileIndex == 1 || tileIndex == 3) ? 0.515f : 0.015f;
        float u1 = (tileIndex == 1 || tileIndex == 3) ? 0.985f : 0.485f;
        float v0 = (tileIndex == 2 || tileIndex == 3) ? 0.515f : 0.015f;
        float v1 = (tileIndex == 2 || tileIndex == 3) ? 0.985f : 0.485f;

        int start = vh.currentVertCount;

        UIVertex vert = UIVertex.simpleVert;
        vert.color = color;

        vert.position = center - rx - ry;
        vert.uv0 = new Vector2(u0, v0);
        vh.AddVert(vert);

        vert.position = center + rx - ry;
        vert.uv0 = new Vector2(u1, v0);
        vh.AddVert(vert);

        vert.position = center + rx + ry;
        vert.uv0 = new Vector2(u1, v1);
        vh.AddVert(vert);

        vert.position = center - rx + ry;
        vert.uv0 = new Vector2(u0, v1);
        vh.AddVert(vert);

        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }

    private static Vector2 GetShardVertex(in PolygonShard s, int index)
    {
        switch (index)
        {
            case 0: return s.v0;
            case 1: return s.v1;
            case 2: return s.v2;
            case 3: return s.v3;
            default: return s.v4;
        }
    }

    // ==================== POOL ACQUISITION ====================

    private FireballInstance AcquireFireball()
    {
        for (int i = 0; i < fireballPool.Count; i++)
        {
            if (!fireballPool[i].active) return fireballPool[i];
        }
        FireballInstance created = new FireballInstance();
        fireballPool.Add(created);
        return created;
    }

    private int AcquireShardIndex()
    {
        for (int i = 0; i < shardPool.Count; i++)
        {
            if (!shardPool[i].active) return i;
        }
        shardPool.Add(default);
        return shardPool.Count - 1;
    }

    private void SpawnParticle(
        Vector2 pos,
        Vector2 vel,
        float drag,
        float gravityY,
        float lifetime,
        float startSize,
        float endSize,
        float aspectX,
        float rotation,
        float rotSpeed,
        bool alignToVelocity,
        int tileIndex,
        Color startColor,
        Color endColor)
    {
        int idx = -1;
        for (int i = 0; i < particlePool.Count; i++)
        {
            if (!particlePool[i].active) { idx = i; break; }
        }
        if (idx < 0)
        {
            if (particlePool.Count < 420)
            {
                particlePool.Add(default);
                idx = particlePool.Count - 1;
            }
            else
            {
                idx = 0;
            }
        }

        particlePool[idx] = new VfxParticle
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
            rotation = rotation,
            rotSpeed = rotSpeed,
            alignToVelocity = alignToVelocity,
            tileIndex = tileIndex,
            startColor = startColor,
            endColor = endColor
        };
    }

    private void SpawnShockwaveRing(
        Vector2 pos,
        float startRadius,
        float endRadius,
        float startThickness,
        float lifetime,
        Color startColor,
        Color endColor,
        int segments)
    {
        int idx = -1;
        for (int i = 0; i < ringPool.Count; i++)
        {
            if (!ringPool[i].active) { idx = i; break; }
        }
        if (idx < 0)
        {
            ringPool.Add(default);
            idx = ringPool.Count - 1;
        }

        ringPool[idx] = new ShockwaveRing
        {
            active = true,
            pos = pos,
            age = 0f,
            lifetime = Mathf.Max(0.06f, lifetime),
            startRadius = startRadius,
            endRadius = endRadius,
            startThickness = startThickness,
            startColor = startColor,
            endColor = endColor,
            segments = Mathf.Clamp(segments, 6, 40)
        };
    }

    private void TriggerCardPop(RectTransform rt, float peakScale, float duration)
    {
        for (int i = 0; i < cardPopPool.Count; i++)
        {
            if (cardPopPool[i].active && cardPopPool[i].target == rt)
            {
                CardPopAnim existing = cardPopPool[i];
                existing.age = 0f;
                existing.duration = duration;
                existing.peakScale = peakScale;
                cardPopPool[i] = existing;
                return;
            }
        }

        int idx = -1;
        for (int i = 0; i < cardPopPool.Count; i++)
        {
            if (!cardPopPool[i].active) { idx = i; break; }
        }
        if (idx < 0)
        {
            cardPopPool.Add(default);
            idx = cardPopPool.Count - 1;
        }

        cardPopPool[idx] = new CardPopAnim
        {
            active = true,
            target = rt,
            baseScale = rt.localScale,
            age = 0f,
            duration = duration,
            peakScale = peakScale
        };
    }

    // ==================== SINH TEXTURE ATLAS PROCEDURAL (256x256) ====================

    private Texture2D CreateProceduralVfxAtlas()
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

                // Tọa độ chuẩn hóa [-1, 1] trong từng ô 128x128 (chừa mép trong suốt 3px)
                float lx = ((x - tileX * half) / (float)(half - 1)) * 2f - 1f;
                float ly = ((y - tileY * half) / (float)(half - 1)) * 2f - 1f;
                float r = Mathf.Sqrt(lx * lx + ly * ly);
                float a = 0f;

                if (r < 0.96f)
                {
                    if (tileIdx == 0)
                    {
                        // Tile 0: Quầng sáng tròn mượt không viền cứng (Soft Radial Glow)
                        float core = Mathf.Exp(-4.6f * r * r);
                        float edgeFade = Mathf.Clamp01((0.96f - r) / 0.18f);
                        a = Mathf.Clamp01(core * edgeFade);
                    }
                    else if (tileIdx == 1)
                    {
                        // Tile 1: Quả cầu lửa xoáy có các lưỡi lửa cuộn ra ngoài (Swirling Flame Body)
                        float ang = Mathf.Atan2(ly, lx);
                        float swirl = ang + r * 3.6f;
                        float tongues = 0.55f + 0.28f * Mathf.Sin(swirl * 5f) + 0.17f * Mathf.Cos(ang * 8f - r * 5f);
                        float shapedR = r / Mathf.Lerp(0.68f, 1.02f, tongues);
                        float body = Mathf.Exp(-3.1f * shapedR * shapedR);
                        float hotCenter = Mathf.Exp(-9.5f * r * r) * 0.45f;
                        float edgeFade = Mathf.Clamp01((0.96f - r) / 0.16f);
                        a = Mathf.Clamp01((body + hotCenter) * edgeFade);
                    }
                    else if (tileIdx == 2)
                    {
                        // Tile 2: Tia lửa kéo dài sắc nét hướng theo vận tốc (Velocity Spark Streak)
                        float sx = lx * 1.05f;
                        float sy = ly * 2.55f;
                        float sr = Mathf.Sqrt(sx * sx + sy * sy);
                        float core = Mathf.Exp(-3.4f * sr * sr);
                        float edgeFade = Mathf.Clamp01((0.96f - Mathf.Max(Mathf.Abs(lx), Mathf.Abs(ly))) / 0.14f);
                        a = Mathf.Clamp01(core * edgeFade);
                    }
                    else
                    {
                        // Tile 3: Tia sáng ngôi sao 4 cánh lấp lánh (4-Point Star Glint)
                        float ax = Mathf.Abs(lx);
                        float ay = Mathf.Abs(ly);
                        float horiz = Mathf.Exp(-18f * ay * ay - 3.2f * ax * ax);
                        float vert = Mathf.Exp(-18f * ax * ax - 3.2f * ay * ay);
                        float centerGlow = Mathf.Exp(-9.5f * r * r) * 0.85f;
                        float edgeFade = Mathf.Clamp01((0.96f - r) / 0.14f);
                        a = Mathf.Clamp01((horiz + vert + centerGlow) * edgeFade);
                    }
                }

                byte alphaByte = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
                pixels[y * size + x] = new Color32(255, 255, 255, alphaByte);
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

    private static float EaseInQuad(float t)
    {
        float c = Mathf.Clamp01(t);
        return c * c;
    }

    private static float SmoothStep(float t)
    {
        float c = Mathf.Clamp01(t);
        return c * c * (3f - 2f * c);
    }

    // ==================== CUSTOM UGUI GRAPHIC LAYERS ====================

    private class VfxSolidMeshLayer : MaskableGraphic
    {
        public Action<VertexHelper> onPopulateMesh;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            onPopulateMesh?.Invoke(vh);
        }
    }

    private class VfxSpriteAdditiveLayer : MaskableGraphic
    {
        public Action<VertexHelper> onPopulateMesh;
        private Texture2D atlasTex;

        public override Texture mainTexture => atlasTex != null ? (Texture)atlasTex : s_WhiteTexture;

        public void SetAtlasTexture(Texture2D tex)
        {
            atlasTex = tex;
            SetMaterialDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            onPopulateMesh?.Invoke(vh);
        }
    }
}
