using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Bộ điều phối ván đấu 1v1 trên BattleScene theo mô hình Host-Authoritative + Perspective-Flipped Snapshot.
/// Hỗ trợ đầy đủ Quái thú, Hiến tế, Bài Phép (Monster Reborn, Swords of Revealing Light...), Bài Bẫy (Magical Hats)
/// và các tương tác bất đồng bộ (Async Prompts) có timeout an toàn.
/// </summary>
public class NetworkDuelController : MonoBehaviour
{
    public static NetworkDuelController Instance { get; private set; }

    private const string MSG_CLIENT_SCENE_READY = "YGO_Duel_ClientSceneReady";
    private const string MSG_DUEL_SNAPSHOT = "YGO_Duel_Snapshot";
    private const string MSG_DUEL_ACTION = "YGO_Duel_ActionRequest";
    private const string MSG_DUEL_STATUS_ONLY = "YGO_Duel_StatusFeedback";
    private const string MSG_DUEL_LEAVE = "YGO_Duel_LeaveNotify";
    private const string MSG_DUEL_AFK_TIMER = "YGO_Duel_AfkTimer";

    private NetworkManager networkManager;
    private bool handlersRegistered = false;

    // ==================== AUTHORITATIVE STATE (CHỈ CHẠY TRÊN HOST) ====================
    private Player[] authPlayers = new Player[2];
    private List<CardData>[] authDecks = new List<CardData>[2];
    private List<CardData>[] authHands = new List<CardData>[2];
    private List<CardData>[] authGraveyards = new List<CardData>[2];
    private CardData[][] authSpellZones = new CardData[2][];
    private bool[][] authSpellFaceDown = new bool[2][];

    private Duel authDuel;
    private BattleManager.TurnState authTurnState = BattleManager.TurnState.DrawPhase;
    private BattleManager.TurnState authTurnStateBeforeEndPhaseDiscard = BattleManager.TurnState.MainPhase;
    private bool hostMatchInitialized = false;
    private int snapshotSeq = 0;

    private readonly List<string> currentActionLogs = new List<string>();
    private readonly List<string> currentActionSfx = new List<string>();

    private class AuthVfxAttackRecord
    {
        public int attackerSlot;
        public int attackerZone;
        public bool isDirectAttack;
        public int targetSlot;
        public int targetZone;
    }

    private class AuthVfxDestroyRecord
    {
        public int ownerSlot;
        public int zoneIndex;
        public bool isDefense;
        public string cardName;
    }

    private class AuthVfxGodSummonRecord
    {
        public int ownerSlot;
        public int zoneIndex;
        public MonsterEffectType godEffect;
        public string cardName;
    }

    private AuthVfxAttackRecord currentActionAttack = null;
    private readonly List<AuthVfxDestroyRecord> currentActionDestroys = new List<AuthVfxDestroyRecord>();
    private AuthVfxGodSummonRecord currentActionGodSummon = null;

    private bool isMatchOver = false;
    private int matchWinnerSlot = -1;
    private string matchOverReason = "";
    private readonly bool[] rematchVotes = new bool[2];
    private int nextStartingPlayerSlot = 0;
    private bool pendingRematchResetFlag = false;

    // Quản lý các lá Phép/Bẫy thường vừa lật ngửa 0.8s trước khi chuyển xuống Mộ
    private class PendingTransientSpell
    {
        public int playerSlot;
        public int zoneIndex;
        public CardData card;
    }
    private readonly List<PendingTransientSpell> pendingTransientSpells = new List<PendingTransientSpell>();

    // Quản lý tương tác bất đồng bộ (Bước 5: FLIP chọn Mộ / Menu chọn bài úp khi bị tấn công)
    private NetPendingPromptType pendingPromptType = NetPendingPromptType.None;
    private int pendingPromptId = 0;
    private int pendingPromptPlayerSlot = -1;
    private string pendingFlipCardName = "";
    private int pendingAttackActorSlot = -1;
    private int pendingAttackerZone = -1;
    private int pendingTargetZone = -1;
    private bool pendingIsDirectAttack = false;
    private bool declinedMagicalHatsThisBattlePhase = false;
    private Coroutine promptTimeoutCoroutine;

    // Bộ đếm Anti-AFK trên Host (10s không có thao tác game hợp lệ -> đếm ngược 35s -> xử thua)
    private int afkTrackedSlot = 0;
    private float afkIdleElapsed = 0f;
    private int lastBroadcastAfkSeconds = -1;
    private bool remoteClientSceneReady = false;

    // ==================== CLIENT SYNC STATE ====================
    private bool clientReceivedSnapshot = false;
    private Coroutine clientHandshakeRoutine;

    public static NetworkDuelController EnsureInstance()
    {
        if (Instance != null) return Instance;

        NetworkDuelController existing = FindObjectOfType<NetworkDuelController>();
        if (existing != null)
        {
            Instance = existing;
            return existing;
        }

        GameObject go = new GameObject("NetworkDuelController");
        Instance = go.AddComponent<NetworkDuelController>();
        return Instance;
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

    public void InitializeForBattleScene(BattleManager bm)
    {
        networkManager = NetworkManager.Singleton != null
            ? NetworkManager.Singleton
            : FindObjectOfType<NetworkManager>();

        RegisterMessageHandlers();

        if (MatchSessionConfig.LocalIsHost)
        {
            InitializeHostMatch(bm);
        }
        else
        {
            clientReceivedSnapshot = false;
            if (clientHandshakeRoutine != null) StopCoroutine(clientHandshakeRoutine);
            clientHandshakeRoutine = StartCoroutine(ClientHandshakeRoutine());
        }
    }

    void OnDestroy()
    {
        UnregisterMessageHandlers();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void RegisterMessageHandlers()
    {
        if (networkManager == null || networkManager.CustomMessagingManager == null || handlersRegistered) return;

        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_CLIENT_SCENE_READY, OnReceiveClientSceneReady);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DUEL_SNAPSHOT, OnReceiveDuelSnapshot);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DUEL_ACTION, OnReceiveDuelActionRequest);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DUEL_STATUS_ONLY, OnReceiveStatusFeedback);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DUEL_LEAVE, OnReceivePlayerLeft);
        networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MSG_DUEL_AFK_TIMER, OnReceiveAfkTimerUpdate);
        handlersRegistered = true;
    }

    private void UnregisterMessageHandlers()
    {
        if (networkManager == null || networkManager.CustomMessagingManager == null || !handlersRegistered) return;

        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_CLIENT_SCENE_READY);
        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DUEL_SNAPSHOT);
        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DUEL_ACTION);
        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DUEL_STATUS_ONLY);
        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DUEL_LEAVE);
        networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MSG_DUEL_AFK_TIMER);
        handlersRegistered = false;
    }

    void Update()
    {
        UpdateHostAntiAfkTimer();
    }

    private int GetActiveDecisionSlot()
    {
        if (pendingPromptType != NetPendingPromptType.None &&
            pendingPromptPlayerSlot >= 0 && pendingPromptPlayerSlot <= 1)
        {
            return pendingPromptPlayerSlot;
        }
        return authDuel != null ? authDuel.currentIndex : 0;
    }

    private void ResetAuthoritativeAfkTimer()
    {
        afkTrackedSlot = GetActiveDecisionSlot();
        afkIdleElapsed = 0f;
        lastBroadcastAfkSeconds = -1;
    }

    private void UpdateHostAntiAfkTimer()
    {
        if (!MatchSessionConfig.LocalIsHost || !hostMatchInitialized || isMatchOver || authDuel == null)
            return;

        // Chờ Client tải xong BattleScene (hoặc tối đa sau khi đã kết nối) mới bắt đầu đếm giờ
        if (!remoteClientSceneReady)
            return;

        int activeSlot = GetActiveDecisionSlot();
        if (activeSlot != afkTrackedSlot)
        {
            afkTrackedSlot = activeSlot;
            afkIdleElapsed = 0f;
            if (lastBroadcastAfkSeconds != -1)
            {
                lastBroadcastAfkSeconds = -1;
                BroadcastAfkTimerToBoth(-1, activeSlot);
            }
        }

        afkIdleElapsed += Time.deltaTime;
        if (afkIdleElapsed < BattleManager.AFK_IDLE_GRACE_SECONDS)
        {
            if (lastBroadcastAfkSeconds != -1)
            {
                lastBroadcastAfkSeconds = -1;
                BroadcastAfkTimerToBoth(-1, activeSlot);
            }
            return;
        }

        float countdownElapsed = afkIdleElapsed - BattleManager.AFK_IDLE_GRACE_SECONDS;
        if (countdownElapsed >= BattleManager.AFK_COUNTDOWN_SECONDS)
        {
            int loserSlot = activeSlot;
            int winnerSlot = 1 - loserSlot;
            string loserName = (authPlayers[loserSlot] != null && !string.IsNullOrWhiteSpace(authPlayers[loserSlot].name))
                ? authPlayers[loserSlot].name
                : "Đối thủ";

            lastBroadcastAfkSeconds = -1;
            DeclareAuthoritativeGameOver(winnerSlot, $"{loserName} bị xử thua do không thao tác quá thời gian quy định (Anti-AFK)!");
            BroadcastSnapshotsToBoth(matchOverReason);
            return;
        }

        int remainSec = Mathf.CeilToInt(Mathf.Max(1f, BattleManager.AFK_COUNTDOWN_SECONDS - countdownElapsed));
        if (remainSec != lastBroadcastAfkSeconds)
        {
            lastBroadcastAfkSeconds = remainSec;
            BroadcastAfkTimerToBoth(remainSec, activeSlot);
        }
    }

    private void BroadcastAfkTimerToBoth(int remainingSeconds, int activeSlot)
    {
        string targetName = (activeSlot >= 0 && activeSlot <= 1 && authPlayers[activeSlot] != null)
            ? authPlayers[activeSlot].name
            : "";

        if (BattleManager.Instance != null)
        {
            BattleManager.Instance.UpdateAfkCountdownBanner(
                remainingSeconds,
                isForLocalPlayer: (activeSlot == 0),
                targetPlayerName: targetName
            );
        }

        if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening) return;
        ulong clientId = ResolveRemoteClientId();
        if (clientId != ulong.MaxValue && clientId != NetworkManager.ServerClientId &&
            networkManager.ConnectedClientsIds != null && networkManager.ConnectedClientsIds.Contains(clientId))
        {
            SendJsonMessage(MSG_DUEL_AFK_TIMER, clientId, new NetAfkTimerDto
            {
                remainingSeconds = remainingSeconds,
                isForMe = (activeSlot == 1),
                targetPlayerName = targetName
            });
        }
    }

    private void OnReceiveAfkTimerUpdate(ulong senderClientId, FastBufferReader reader)
    {
        if (MatchSessionConfig.LocalIsHost) return;
        NetAfkTimerDto dto = ReadJsonMessage<NetAfkTimerDto>(reader);
        if (dto == null || BattleManager.Instance == null) return;

        BattleManager.Instance.UpdateAfkCountdownBanner(
            dto.remainingSeconds,
            isForLocalPlayer: dto.isForMe,
            targetPlayerName: dto.targetPlayerName
        );
    }

    // ==================== GỬI YÊU CẦU HÀNH ĐỘNG TỪ NGƯỜI CHƠI ====================

    public void RequestAction(NetDuelActionRequest request)
    {
        if (request == null) return;

        if (MatchSessionConfig.LocalIsHost)
        {
            ProcessActionOnHost(actorSlot: 0, request);
        }
        else if (networkManager != null && networkManager.IsConnectedClient)
        {
            SendJsonMessage(MSG_DUEL_ACTION, NetworkManager.ServerClientId, request);
        }
    }

    public void RequestRematch()
    {
        RequestAction(new NetDuelActionRequest
        {
            actionType = (int)NetDuelActionType.RequestRematch
        });
    }

    public void NotifyLeavingMatch()
    {
        if (networkManager == null || !networkManager.IsListening || networkManager.CustomMessagingManager == null)
            return;

        NetLeaveMatchDto dto = new NetLeaveMatchDto
        {
            leaverName = MatchSessionConfig.GetLocalDisplayName()
        };

        if (MatchSessionConfig.LocalIsHost)
        {
            ulong clientId = ResolveRemoteClientId();
            if (clientId != ulong.MaxValue && clientId != NetworkManager.ServerClientId &&
                networkManager.ConnectedClientsIds != null && networkManager.ConnectedClientsIds.Contains(clientId))
            {
                SendJsonMessage(MSG_DUEL_LEAVE, clientId, dto);
            }
        }
        else if (networkManager.IsConnectedClient)
        {
            SendJsonMessage(MSG_DUEL_LEAVE, NetworkManager.ServerClientId, dto);
        }
    }

    // ==================== HOST: KHỞI TẠO VÁN ĐẤU & SỰ KIỆN DUEL ====================

    private void InitializeHostMatch(BattleManager bm)
    {
        if (bm == null) return;

        int startLP = bm.startingLP > 4000 ? bm.startingLP : 8000;
        string hostName = string.IsNullOrWhiteSpace(MatchSessionConfig.HostPlayerName) ? "Host" : MatchSessionConfig.HostPlayerName.Trim();
        string clientName = string.IsNullOrWhiteSpace(MatchSessionConfig.ClientPlayerName) ? "Client" : MatchSessionConfig.ClientPlayerName.Trim();
        if (string.Equals(hostName, clientName, StringComparison.OrdinalIgnoreCase))
        {
            hostName = $"{hostName} (P1)";
            clientName = $"{clientName} (P2)";
        }

        for (int s = 0; s < 2; s++)
        {
            authHands[s] = new List<CardData>();
            authGraveyards[s] = new List<CardData>();
            authSpellZones[s] = new CardData[Player.ZoneCount];
            authSpellFaceDown[s] = new bool[Player.ZoneCount];
        }

        authDecks[0] = bm.BuildDeckFromJson(MatchSessionConfig.HostDeckJson);
        authDecks[1] = bm.BuildDeckFromJson(MatchSessionConfig.ClientDeckJson);

        if (authDecks[0].Count == 0)
        {
            string fallbackJson = MatchSessionConfig.BuildLocalDeckJson(out _, out _, out _);
            authDecks[0] = bm.BuildDeckFromJson(fallbackJson);
        }
        if (authDecks[1].Count == 0)
        {
            string fallbackJson = MatchSessionConfig.BuildLocalDeckJson(out _, out _, out _);
            authDecks[1] = bm.BuildDeckFromJson(fallbackJson);
        }

        ShuffleDeck(authDecks[0]);
        ShuffleDeck(authDecks[1]);

        authPlayers[0] = new Player(hostName) { lifePoints = startLP };
        authPlayers[1] = new Player(clientName) { lifePoints = startLP };

        isMatchOver = false;
        matchWinnerSlot = -1;
        matchOverReason = "";
        rematchVotes[0] = false;
        rematchVotes[1] = false;
        pendingTransientSpells.Clear();
        pendingPromptType = NetPendingPromptType.None;
        declinedMagicalHatsThisBattlePhase = false;
        currentActionLogs.Clear();
        currentActionSfx.Clear();
        currentActionAttack = null;
        currentActionDestroys.Clear();
        currentActionGodSummon = null;

        authDuel = new Duel(authPlayers[0], authPlayers[1]);
        authDuel.currentIndex = nextStartingPlayerSlot;
        nextStartingPlayerSlot = 1 - nextStartingPlayerSlot; // Đổi người đi trước ở ván Đấu lại tiếp theo
        BindAuthoritativeDuelEvents();

        // Rút 5 lá mở màn cho mỗi bên
        for (int i = 0; i < 5; i++)
        {
            DrawCardAuthoritative(0, checkDeckOut: false);
            DrawCardAuthoritative(1, checkDeckOut: false);
        }

        authTurnState = BattleManager.TurnState.MainPhase;
        hostMatchInitialized = true;
        snapshotSeq = 1;

        currentActionLogs.Clear();
        currentActionSfx.Clear();
        currentActionAttack = null;
        currentActionDestroys.Clear();
        currentActionGodSummon = null;
        QueueSfx("Draw");

        Debug.Log($"[NetworkDuel][Host] Đã khởi tạo Duel 1v1! {hostName} (Tay:{authHands[0].Count}, Deck:{authDecks[0].Count}) vs {clientName} (Tay:{authHands[1].Count}, Deck:{authDecks[1].Count}). Lượt 1: {authDuel.Current.name}");

        BroadcastSnapshotsToBoth(customStatus: null);
    }

    private void BindAuthoritativeDuelEvents()
    {
        if (authDuel == null) return;

        authDuel.OnLog += msg =>
        {
            if (!string.IsNullOrWhiteSpace(msg))
            {
                currentActionLogs.Add(msg);
            }
        };

        authDuel.OnSummoned += summoned =>
        {
            QueueSfx("Summon");
            if (summoned != null && !summoned.IsFaceDown && summoned.data != null && summoned.data.IsDivineGod)
            {
                currentActionGodSummon = new AuthVfxGodSummonRecord
                {
                    ownerSlot = GetPlayerSlot(summoned.owner),
                    zoneIndex = summoned.zoneIndex,
                    godEffect = summoned.data.monsterEffect,
                    cardName = summoned.data.cardName
                };
            }
        };
        authDuel.OnSpellActivated += _ => QueueSfx("Spell");
        authDuel.OnAttackDeclared += (attacker, target) =>
        {
            QueueSfx("Attack");
            if (attacker != null)
            {
                int atkSlot = GetPlayerSlot(attacker.owner);
                int tgtSlot = target != null ? GetPlayerSlot(target.owner) : (1 - atkSlot);
                currentActionAttack = new AuthVfxAttackRecord
                {
                    attackerSlot = atkSlot,
                    attackerZone = attacker.zoneIndex,
                    isDirectAttack = (target == null),
                    targetSlot = tgtSlot,
                    targetZone = target != null ? target.zoneIndex : -1
                };
            }
        };
        authDuel.OnPositionChanged += _ => QueueSfx("Summon");

        authDuel.OnDamage += (damagedPlayer, amount) =>
        {
            if (amount > 0) QueueSfx("Damage");
            CheckAuthoritativeLPGameOver();
        };

        authDuel.OnMonsterShattered += shatteredCard =>
        {
            if (shatteredCard == null) return;
            string resolvedName = shatteredCard.isMagicalHatDecoy
                ? (shatteredCard.originalSpellTrapData != null ? shatteredCard.originalSpellTrapData.cardName : "")
                : (shatteredCard.data != null ? shatteredCard.data.cardName : "");
            currentActionDestroys.Add(new AuthVfxDestroyRecord
            {
                ownerSlot = GetPlayerSlot(shatteredCard.owner),
                zoneIndex = shatteredCard.zoneIndex,
                isDefense = shatteredCard.IsDefense,
                cardName = resolvedName
            });
        };

        authDuel.OnDestroyed += destroyedCard =>
        {
            QueueSfx("Destroy");
            if (destroyedCard != null && destroyedCard.data != null)
            {
                int ownerSlot = GetPlayerSlot(destroyedCard.owner);
                authGraveyards[ownerSlot].Add(destroyedCard.data);
            }
        };

        authDuel.OnRequestDraw += (p, count) =>
        {
            int slot = GetPlayerSlot(p);
            for (int i = 0; i < count; i++)
            {
                if (!DrawCardAuthoritative(slot, checkDeckOut: true)) break;
                QueueSfx("Draw");
            }
        };

        authDuel.OnReturnMonsterToHand += (owner, data) =>
        {
            if (data == null) return;
            int slot = GetPlayerSlot(owner);
            authHands[slot].Add(data);
            SyncAuthoritativeHandCounts();
        };

        authDuel.OnSearchMonsterFromDeck += HandleAuthoritativeSearchMonsterFromDeck;
        authDuel.OnRequestDeckSpellsTraps += HandleAuthoritativeRequestDeckSpellsTraps;

        authDuel.OnSendCardToGraveyard += (owner, data) =>
        {
            if (data == null) return;
            int slot = GetPlayerSlot(owner);
            authGraveyards[slot].Add(data);
        };

        authDuel.OnSwordsExpired += HandleAuthoritativeSwordsExpired;
        authDuel.OnTriggerFlipGraveyardToDeck += HandleAuthoritativeFlipGraveyardToDeck;
    }

    private void SyncAuthoritativeHandCounts()
    {
        for (int s = 0; s < 2; s++)
        {
            if (authPlayers[s] != null && authHands[s] != null)
            {
                authPlayers[s].handCount = authHands[s].Count;
            }
        }
    }

    private int GetPlayerSlot(Player p)
    {
        return (p == authPlayers[1]) ? 1 : 0;
    }

    private void QueueSfx(string sfxId)
    {
        if (string.IsNullOrEmpty(sfxId)) return;
        if (!currentActionSfx.Contains(sfxId))
        {
            currentActionSfx.Add(sfxId);
        }
    }

    private void ShuffleDeck(List<CardData> deck)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int rnd = UnityEngine.Random.Range(0, i + 1);
            CardData tmp = deck[i];
            deck[i] = deck[rnd];
            deck[rnd] = tmp;
        }
    }

    private bool DrawCardAuthoritative(int playerSlot, bool checkDeckOut)
    {
        if (playerSlot < 0 || playerSlot > 1 || isMatchOver) return false;
        List<CardData> deck = authDecks[playerSlot];
        List<CardData> hand = authHands[playerSlot];

        if (deck == null || deck.Count == 0)
        {
            if (checkDeckOut)
            {
                int winner = 1 - playerSlot;
                string loserName = authPlayers[playerSlot].name;
                DeclareAuthoritativeGameOver(winner, $"{loserName} không thể rút bài vì Deck đã hết bài (Deck Out)!");
            }
            return false;
        }

        CardData top = deck[0];
        deck.RemoveAt(0);
        hand.Add(top);
        SyncAuthoritativeHandCounts();
        return true;
    }

    private void CheckAuthoritativeLPGameOver()
    {
        if (isMatchOver || authPlayers[0] == null || authPlayers[1] == null) return;

        if (authPlayers[0].lifePoints <= 0)
        {
            DeclareAuthoritativeGameOver(1, $"{authPlayers[0].name} đã hết điểm gốc (LP = 0)!");
        }
        else if (authPlayers[1].lifePoints <= 0)
        {
            DeclareAuthoritativeGameOver(0, $"{authPlayers[1].name} đã hết điểm gốc (LP = 0)!");
        }
    }

    private void DeclareAuthoritativeGameOver(int winnerSlot, string reason)
    {
        if (isMatchOver) return;
        isMatchOver = true;
        matchWinnerSlot = winnerSlot;
        matchOverReason = reason;
        authTurnState = BattleManager.TurnState.GameOver;
        ClearPendingPromptState();
    }

    // ==================== QUẢN LÝ Ô PHÉP/BẪY & TRANSIENT CLEAR ====================

    private int FindEmptyAuthoritativeSpellZone(int playerSlot, int preferredZone = -1)
    {
        CardData[] zones = authSpellZones[playerSlot];
        if (preferredZone >= 0 && preferredZone < Player.ZoneCount && zones[preferredZone] == null)
            return preferredZone;

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (zones[i] == null) return i;
        }
        return -1;
    }

    private int FindSetAuthoritativeTrapZone(int playerSlot, SpellEffectType effectType)
    {
        CardData[] zones = authSpellZones[playerSlot];
        bool[] faceDowns = authSpellFaceDown[playerSlot];

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (zones[i] != null && faceDowns[i] && zones[i].spellEffect == effectType)
                return i;
        }
        return -1;
    }

    private bool CanAuthoritativeSetCardReact(int playerSlot, int spellZone, out string failReason)
    {
        failReason = "";
        if (playerSlot < 0 || playerSlot > 1 || spellZone < 0 || spellZone >= Player.ZoneCount)
        {
            failReason = "Ô bài không hợp lệ.";
            return false;
        }

        CardData card = authSpellZones[playerSlot][spellZone];
        bool isFaceDown = authSpellFaceDown[playerSlot][spellZone];
        if (card == null || !isFaceDown)
        {
            failReason = "Lá bài không ở trạng thái đặt úp.";
            return false;
        }

        int enemySlot = 1 - playerSlot;

        // Nếu đang ở cửa sổ phản ứng Hang Rơi (Trap Hole) khi đối thủ vừa Triệu hồi Thường / Lật
        if (pendingPromptType == NetPendingPromptType.TrapHoleOnSummon)
        {
            MonsterCard summonedTarget = (pendingAttackActorSlot >= 0 && pendingAttackActorSlot <= 1 &&
                                          pendingTargetZone >= 0 && pendingTargetZone < Player.ZoneCount)
                ? authPlayers[pendingAttackActorSlot].monsterZones[pendingTargetZone]
                : null;
            return Duel.CanActivateTrapHoleOnSummon(card, summonedTarget, out failReason);
        }

        bool deckHasSpellTrap = authDecks[playerSlot].Any(c => c != null && c.IsSpellOrTrap);
        bool gyHasMonster = authGraveyards[playerSlot].Any(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned);

        return Duel.CanActivateSpellOrTrap(
            card,
            authPlayers[playerSlot],
            authPlayers[enemySlot],
            authDecks[playerSlot].Count,
            deckHasSpellTrap,
            gyHasMonster,
            out failReason
        );
    }

    private bool HasAuthoritativeActivatableSetCardForReaction(int playerSlot)
    {
        for (int i = 0; i < Player.ZoneCount; i++)
        {
            if (CanAuthoritativeSetCardReact(playerSlot, i, out _))
                return true;
        }
        if (authHands[playerSlot] != null &&
            authHands[playerSlot].Any(c => c != null && c.cardType == CardType.Monster && c.monsterEffect == MonsterEffectType.KuribohHandGuard))
        {
            return true;
        }
        return false;
    }

    private bool HasAuthoritativeActivatableTrapHoleOnSummon(int defenderSlot, MonsterCard summonedMonster)
    {
        if (defenderSlot < 0 || defenderSlot > 1 || summonedMonster == null || summonedMonster.IsFaceDown)
            return false;

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            CardData card = authSpellZones[defenderSlot][i];
            bool isFaceDown = authSpellFaceDown[defenderSlot][i];
            if (card != null && isFaceDown && Duel.CanActivateTrapHoleOnSummon(card, summonedMonster, out _))
                return true;
        }
        return false;
    }

    private void ScheduleTransientSpellClear(int playerSlot, int zoneIndex, CardData card, float delay = 0.85f)
    {
        var entry = new PendingTransientSpell
        {
            playerSlot = playerSlot,
            zoneIndex = zoneIndex,
            card = card
        };
        pendingTransientSpells.Add(entry);
        StartCoroutine(TransientSpellClearRoutine(entry, delay));
    }

    private IEnumerator TransientSpellClearRoutine(PendingTransientSpell entry, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (!pendingTransientSpells.Contains(entry)) yield break;
        if (networkManager == null || !networkManager.IsListening) yield break;

        pendingTransientSpells.Remove(entry);
        if (authSpellZones[entry.playerSlot][entry.zoneIndex] == entry.card)
        {
            authSpellZones[entry.playerSlot][entry.zoneIndex] = null;
            authSpellFaceDown[entry.playerSlot][entry.zoneIndex] = false;
            authGraveyards[entry.playerSlot].Add(entry.card);

            currentActionSfx.Clear();
            BroadcastSnapshotsToBoth(customStatus: null, keepExistingStatusOnClient: true);
        }
    }

    private void FlushPendingTransientSpellsImmediate()
    {
        if (pendingTransientSpells.Count == 0) return;

        foreach (var entry in pendingTransientSpells.ToList())
        {
            if (authSpellZones[entry.playerSlot][entry.zoneIndex] == entry.card)
            {
                authSpellZones[entry.playerSlot][entry.zoneIndex] = null;
                authSpellFaceDown[entry.playerSlot][entry.zoneIndex] = false;
                authGraveyards[entry.playerSlot].Add(entry.card);
            }
        }
        pendingTransientSpells.Clear();
    }

    // ==================== AUTHORITATIVE EFFECT CALLBACKS ====================

    private CardData HandleAuthoritativeSearchMonsterFromDeck(Player p)
    {
        int slot = GetPlayerSlot(p);
        List<CardData> deck = authDecks[slot];
        List<CardData> hand = authHands[slot];

        CardData target = deck.FirstOrDefault(c => c.cardType == CardType.Monster && c.atk <= 1500)
                       ?? deck.FirstOrDefault(c => c.cardType == CardType.Monster);

        if (target != null)
        {
            deck.Remove(target);
            hand.Add(target);
            SyncAuthoritativeHandCounts();
            ShuffleDeck(deck);
        }
        return target;
    }

    private List<CardData> HandleAuthoritativeRequestDeckSpellsTraps(Player p, int maxCount)
    {
        int slot = GetPlayerSlot(p);
        List<CardData> deck = authDecks[slot];
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
        }
        return picked;
    }

    private void HandleAuthoritativeSwordsExpired(Player unblockedPlayer)
    {
        int unblockedSlot = GetPlayerSlot(unblockedPlayer);
        int casterSlot = 1 - unblockedSlot;

        CardData[] zones = authSpellZones[casterSlot];
        bool[] faceDowns = authSpellFaceDown[casterSlot];
        List<CardData> gy = authGraveyards[casterSlot];

        // Chỉ hủy lá Lớp Kiếm Bảo Hộ Của Ánh Sáng đang nằm NGỬA (!faceDowns[i]), không hủy nhầm lá đang đặt úp
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

    private void HandleAuthoritativeFlipGraveyardToDeck(Player owner, MonsterCard card)
    {
        int slot = GetPlayerSlot(owner);
        List<CardData> gy = authGraveyards[slot];

        if (gy.Count == 0)
        {
            currentActionLogs.Add($"[FLIP - {card.data.cardName}] Mộ bài đang trống, không có lá nào để trả về Deck.");
            return;
        }

        // Mở tương tác bất đồng bộ cho đúng người sở hữu quái thú FLIP (kể cả khi bị đánh trong lượt đối thủ!)
        pendingPromptType = NetPendingPromptType.FlipSelectGraveyardToDeck;
        pendingPromptId++;
        pendingPromptPlayerSlot = slot;
        pendingFlipCardName = card.data.cardName;

        if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
        promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 15f));

        currentActionLogs.Add($"[FLIP - {card.data.cardName}] Đang chờ {owner.name} chọn 1 lá bài trong Mộ trả về Deck...");
    }

    private void ClearPendingPromptState()
    {
        pendingPromptType = NetPendingPromptType.None;
        pendingPromptPlayerSlot = -1;
        pendingFlipCardName = "";
        pendingAttackActorSlot = -1;
        pendingAttackerZone = -1;
        pendingTargetZone = -1;
        pendingIsDirectAttack = false;

        if (promptTimeoutCoroutine != null)
        {
            StopCoroutine(promptTimeoutCoroutine);
            promptTimeoutCoroutine = null;
        }
    }

    private IEnumerator PromptTimeoutRoutine(int watchedPromptId, float timeoutSeconds)
    {
        // Bộ đếm Anti-AFK (10s không thao tác + 35s đếm ngược = 45s xử thua) đã quản lý toàn bộ thời gian chờ phản hồi.
        yield break;
    }

    // ==================== XỬ LÝ HÀNH ĐỘNG TRÊN HOST (BƯỚC 4 & 5) ====================

    private void OnReceiveDuelActionRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (!MatchSessionConfig.LocalIsHost || !hostMatchInitialized) return;
        if (senderClientId == NetworkManager.ServerClientId) return;

        NetDuelActionRequest req = ReadJsonMessage<NetDuelActionRequest>(reader);
        if (req == null) return;

        ProcessActionOnHost(actorSlot: 1, req);
    }

    private void ProcessActionOnHost(int actorSlot, NetDuelActionRequest req)
    {
        if (!hostMatchInitialized || authDuel == null) return;

        NetDuelActionType actionType = (NetDuelActionType)req.actionType;

        // Cho phép bình chọn Đấu lại (Rematch) sau khi trận đấu đã kết thúc
        if (actionType == NetDuelActionType.RequestRematch)
        {
            if (isMatchOver || authTurnState == BattleManager.TurnState.GameOver)
            {
                ExecuteAuthoritativeRematchVote(actorSlot);
            }
            return;
        }

        if (isMatchOver || authTurnState == BattleManager.TurnState.GameOver) return;

        // 1. Nếu đang có Prompt bất đồng bộ (FLIP chọn Mộ hoặc Menu chọn bài úp khi bị tấn công)
        if (pendingPromptType != NetPendingPromptType.None)
        {
            if (actionType == NetDuelActionType.RespondPromptChoice && actorSlot == pendingPromptPlayerSlot)
            {
                ExecuteAuthoritativePromptResponse(actorSlot, req);
            }
            else if (actionType == NetDuelActionType.ActivateSetSpellTrap &&
                     (pendingPromptType == NetPendingPromptType.TrapMagicalHats || pendingPromptType == NetPendingPromptType.TrapHoleOnSummon) &&
                     actorSlot == pendingPromptPlayerSlot)
            {
                // Người phòng thủ bấm trực tiếp vào ô bài úp trên sân
                ExecuteAuthoritativePromptResponse(actorSlot, req);
            }
            else
            {
                string waitTargetName = (pendingPromptPlayerSlot >= 0 && pendingPromptPlayerSlot <= 1)
                    ? authPlayers[pendingPromptPlayerSlot].name
                    : "đối thủ";
                SendStatusFeedbackToSlot(actorSlot, $"Đang chờ {waitTargetName} phản hồi hiệu ứng...");
            }
            return;
        }

        FlushPendingTransientSpellsImmediate();

        Player actor = authPlayers[actorSlot];
        Player enemy = authPlayers[1 - actorSlot];

        // 2. Cho phép người phòng thủ chủ động bấm vào lá Magical Hats đang úp trong Battle Phase của đối thủ
        if (actionType == NetDuelActionType.ActivateSetSpellTrap && authDuel.currentIndex != actorSlot)
        {
            ExecuteAuthoritativeActivateSetSpell(actorSlot, actor, req);
            return;
        }

        // 3. Các hành động còn lại chỉ cho phép người đang giữ lượt thực hiện
        if (authDuel.currentIndex != actorSlot)
        {
            SendStatusFeedbackToSlot(actorSlot, $"Chưa tới lượt của bạn! Đang là lượt của {authDuel.Current.name}.");
            return;
        }

        // Bảo vệ trạng thái Phase: Khi không có Prompt và trận chưa kết thúc, chỉ có thể ở MainPhase hoặc BattlePhase
        if (authTurnState != BattleManager.TurnState.MainPhase && authTurnState != BattleManager.TurnState.BattlePhase)
        {
            authTurnState = BattleManager.TurnState.MainPhase;
        }

        currentActionLogs.Clear();
        currentActionSfx.Clear();
        currentActionAttack = null;
        currentActionDestroys.Clear();
        currentActionGodSummon = null;

        switch (actionType)
        {
            case NetDuelActionType.SummonMonster:
                ExecuteAuthoritativeSummon(actorSlot, actor, req);
                break;

            case NetDuelActionType.ChangeMonsterPosition:
                ExecuteAuthoritativeChangePosition(actorSlot, actor, req);
                break;

            case NetDuelActionType.EnterBattlePhase:
                ExecuteAuthoritativeEnterBattlePhase(actorSlot, actor);
                break;

            case NetDuelActionType.AttackMonster:
                ExecuteAuthoritativeAttackMonster(actorSlot, actor, enemy, req);
                break;

            case NetDuelActionType.DirectAttack:
                ExecuteAuthoritativeDirectAttack(actorSlot, actor, req);
                break;

            case NetDuelActionType.EndTurn:
                ExecuteAuthoritativeEndTurn(actorSlot);
                break;

            case NetDuelActionType.ActivateSpellFromHand:
                ExecuteAuthoritativeActivateSpellFromHand(actorSlot, actor, req);
                break;

            case NetDuelActionType.SetSpellTrapFromHand:
                ExecuteAuthoritativeSetSpellFromHand(actorSlot, actor, req);
                break;

            case NetDuelActionType.ActivateSetSpellTrap:
                ExecuteAuthoritativeActivateSetSpell(actorSlot, actor, req);
                break;

            case NetDuelActionType.ActivateMonsterFieldEffect:
                ExecuteAuthoritativeActivateMonsterFieldEffect(actorSlot, actor, req);
                break;
        }
    }

    private void ExecuteAuthoritativeActivateMonsterFieldEffect(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        if (authTurnState != BattleManager.TurnState.MainPhase)
        {
            SendStatusFeedbackToSlot(actorSlot, "Chỉ có thể kích hoạt hiệu ứng quái thú trong Main Phase!");
            return;
        }

        if (req.myZoneIndex < 0 || req.myZoneIndex >= Player.ZoneCount) return;

        MonsterCard monster = actor.monsterZones[req.myZoneIndex];
        if (monster == null || monster.IsFaceDown || monster.data == null)
        {
            SendStatusFeedbackToSlot(actorSlot, "Quái thú phải đang ngửa trên sân mới có thể kích hoạt hiệu ứng!");
            return;
        }

        bool ok = false;
        if (monster.data.monsterEffect == MonsterEffectType.DivineObelisk)
        {
            List<MonsterCard> sacrifices = new List<MonsterCard>();
            if (req.tributeZones != null)
            {
                foreach (int z in req.tributeZones.Distinct())
                {
                    if (z >= 0 && z < Player.ZoneCount && actor.monsterZones[z] != null)
                    {
                        sacrifices.Add(actor.monsterZones[z]);
                    }
                }
            }
            ok = authDuel.ActivateObeliskSoulEnergyMax(actor, monster, sacrifices);
        }
        else if (monster.data.monsterEffect == MonsterEffectType.DivineRa)
        {
            if (req.preferredZone == 1)
            {
                ok = authDuel.ActivateRaPointToPointTransfer(actor, monster);
            }
            else
            {
                Player targetOwner = (req.preferredZone == 2) ? actor : authPlayers[1 - actorSlot];
                MonsterCard target = (req.targetOpponentZone >= 0 && req.targetOpponentZone < Player.ZoneCount)
                    ? targetOwner.monsterZones[req.targetOpponentZone]
                    : null;
                ok = authDuel.ActivateRaPhoenixDestroy(actor, monster, target);
            }
        }
        else
        {
            SendStatusFeedbackToSlot(actorSlot, $"[{monster.data.cardName}] không có hiệu ứng kích hoạt chủ động trên sân.");
            return;
        }

        if (!ok)
        {
            SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Chưa đủ điều kiện kích hoạt hiệu ứng quái thú này."));
            return;
        }

        QueueSfx("Spell");
        CheckAuthoritativeLPGameOver();
        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
    }

    // ==================== THỰC THI BÀI PHÉP / BẪY & ASYNC PROMPTS (BƯỚC 5) ====================

    private void ExecuteAuthoritativeSetSpellFromHand(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        if (authTurnState != BattleManager.TurnState.MainPhase)
        {
            SendStatusFeedbackToSlot(actorSlot, "Chỉ có thể đặt úp bài Phép/Bẫy trong Main Phase!");
            return;
        }

        List<CardData> hand = authHands[actorSlot];
        if (req.handIndex < 0 || req.handIndex >= hand.Count) return;

        CardData spellCard = hand[req.handIndex];
        if (spellCard == null || !spellCard.IsSpellOrTrap) return;

        int spellZone = FindEmptyAuthoritativeSpellZone(actorSlot, req.preferredZone);
        if (spellZone < 0)
        {
            SendStatusFeedbackToSlot(actorSlot, "Hết ô bài Phép/Bẫy trên sân!");
            return;
        }

        hand.RemoveAt(req.handIndex);
        SyncAuthoritativeHandCounts();
        authSpellZones[actorSlot][spellZone] = spellCard.Clone();
        authSpellFaceDown[actorSlot][spellZone] = true;

        QueueSfx("Summon");

        // Không tiết lộ tên lá bài đặt úp cho đối phương
        BroadcastSnapshotsToBoth(
            customStatusForSlot0: actorSlot == 0
                ? $"Bạn đặt úp [{spellCard.cardName}] vào ô Phép/Bẫy."
                : $"{actor.name} đặt úp 1 lá bài Phép/Bẫy.",
            customStatusForSlot1: actorSlot == 1
                ? $"Bạn đặt úp [{spellCard.cardName}] vào ô Phép/Bẫy."
                : $"{actor.name} đặt úp 1 lá bài Phép/Bẫy."
        );
    }

    private void ExecuteAuthoritativeActivateSpellFromHand(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        if (authTurnState != BattleManager.TurnState.MainPhase)
        {
            SendStatusFeedbackToSlot(actorSlot, "Chỉ có thể kích hoạt Bài Phép trong Main Phase!");
            return;
        }

        List<CardData> hand = authHands[actorSlot];
        if (req.handIndex < 0 || req.handIndex >= hand.Count) return;

        CardData spellCard = hand[req.handIndex];
        if (spellCard == null) return;

        if (spellCard.cardType == CardType.Trap)
        {
            SendStatusFeedbackToSlot(actorSlot, "Bài Bẫy phải đặt úp xuống sân trước (Chuột phải hoặc kéo thả)!");
            return;
        }
        if (spellCard.cardType != CardType.Spell) return;

        int spellZone = FindEmptyAuthoritativeSpellZone(actorSlot, req.preferredZone);
        if (spellZone < 0)
        {
            SendStatusFeedbackToSlot(actorSlot, "Hết ô bài Phép/Bẫy trên sân!");
            return;
        }

        // Trường hợp đặc biệt: Monster Reborn (Hồi sinh quái thú từ Mộ theo graveyardTargetIndex đã chọn)
        if (spellCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            if (actor.FirstEmptyZone() < 0)
            {
                SendStatusFeedbackToSlot(actorSlot, "Sân của bạn đã đầy chỗ, không thể hồi sinh!");
                return;
            }

            List<CardData> gy = authGraveyards[actorSlot];
            if (req.graveyardTargetIndex < 0 || req.graveyardTargetIndex >= gy.Count ||
                gy[req.graveyardTargetIndex].cardType != CardType.Monster ||
                gy[req.graveyardTargetIndex].CannotBeSpecialSummoned)
            {
                SendStatusFeedbackToSlot(actorSlot, "Mục tiêu hồi sinh trong Mộ bài không hợp lệ!");
                return;
            }

            CardData chosenMonster = gy[req.graveyardTargetIndex];
            gy.RemoveAt(req.graveyardTargetIndex);

            CardData activeSpell = spellCard.Clone();
            hand.RemoveAt(req.handIndex);
            SyncAuthoritativeHandCounts();
            authSpellZones[actorSlot][spellZone] = activeSpell;
            authSpellFaceDown[actorSlot][spellZone] = false;

            QueueSfx("Spell");
            currentActionLogs.Add($"{actor.name} kích hoạt [{activeSpell.cardName}]: Hồi sinh {chosenMonster.cardName} từ Mộ bài!");

            authDuel.Summon(actor, chosenMonster.Clone(), BattlePosition.FaceUpAttack, null, isSpecial: true);
            ScheduleTransientSpellClear(actorSlot, spellZone, activeSpell);

            CheckAuthoritativeLPGameOver();
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        // Các lá Bài Phép khác (Swords of Revealing Light, Raigeki, Dark Hole, Pot of Greed...)
        CardData spellInstance = spellCard.Clone();
        if (!authDuel.ActivateSpell(actor, spellInstance))
        {
            SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Không thể kích hoạt lá bài phép này lúc này."));
            return;
        }

        hand.RemoveAt(req.handIndex);
        SyncAuthoritativeHandCounts();
        authSpellZones[actorSlot][spellZone] = spellInstance;
        authSpellFaceDown[actorSlot][spellZone] = false;

        if (spellInstance.spellEffect != SpellEffectType.SwordsOfRevealingLight)
        {
            ScheduleTransientSpellClear(actorSlot, spellZone, spellInstance);
        }

        CheckAuthoritativeLPGameOver();
        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
    }

    private void ExecuteAuthoritativeActivateSetSpell(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        int spellZone = req.myZoneIndex;
        if (spellZone < 0 || spellZone >= Player.ZoneCount) return;

        CardData spellCard = authSpellZones[actorSlot][spellZone];
        if (spellCard == null || !authSpellFaceDown[actorSlot][spellZone]) return;

        // 1. Bài Bẫy Mũ Tơ Ma Thuật (chỉ kích hoạt trong Battle Phase của đối thủ)
        if (spellCard.spellEffect == SpellEffectType.MagicalHats)
        {
            if (authDuel.currentIndex == actorSlot || authTurnState != BattleManager.TurnState.BattlePhase)
            {
                SendStatusFeedbackToSlot(actorSlot, "Mũ Tơ Ma Thuật chỉ kích hoạt trong Battle Phase của đối thủ!");
                return;
            }

            currentActionLogs.Clear();
            currentActionSfx.Clear();

            if (!authDuel.ActivateSpell(actor, spellCard))
            {
                SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Chưa đủ điều kiện kích hoạt Mũ Tơ Ma Thuật!"));
                return;
            }

            authSpellFaceDown[actorSlot][spellZone] = false;
            ScheduleTransientSpellClear(actorSlot, spellZone, spellCard);
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        // 2. Bài Phép thường đang đặt úp (chỉ kích hoạt trong Main Phase của lượt mình)
        if (authDuel.currentIndex != actorSlot || authTurnState != BattleManager.TurnState.MainPhase)
        {
            SendStatusFeedbackToSlot(actorSlot, "Bài Phép đặt úp chỉ kích hoạt được trong Main Phase của lượt bạn!");
            return;
        }

        if (spellCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
        {
            if (actor.FirstEmptyZone() < 0)
            {
                SendStatusFeedbackToSlot(actorSlot, "Sân của bạn đã đầy chỗ, không thể hồi sinh!");
                return;
            }

            List<CardData> gy = authGraveyards[actorSlot];
            if (req.graveyardTargetIndex < 0 || req.graveyardTargetIndex >= gy.Count ||
                gy[req.graveyardTargetIndex].cardType != CardType.Monster ||
                gy[req.graveyardTargetIndex].CannotBeSpecialSummoned)
            {
                SendStatusFeedbackToSlot(actorSlot, "Mục tiêu hồi sinh trong Mộ bài không hợp lệ!");
                return;
            }

            CardData chosenMonster = gy[req.graveyardTargetIndex];
            gy.RemoveAt(req.graveyardTargetIndex);

            authSpellFaceDown[actorSlot][spellZone] = false;
            QueueSfx("Spell");
            currentActionLogs.Add($"{actor.name} lật kích hoạt [{spellCard.cardName}]: Hồi sinh {chosenMonster.cardName} từ Mộ bài!");

            authDuel.Summon(actor, chosenMonster.Clone(), BattlePosition.FaceUpAttack, null, isSpecial: true);
            ScheduleTransientSpellClear(actorSlot, spellZone, spellCard);

            CheckAuthoritativeLPGameOver();
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        if (!authDuel.ActivateSpell(actor, spellCard))
        {
            SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Không thể kích hoạt lá bài này lúc này."));
            return;
        }

        authSpellFaceDown[actorSlot][spellZone] = false;
        if (spellCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
        {
            ScheduleTransientSpellClear(actorSlot, spellZone, spellCard);
        }

        CheckAuthoritativeLPGameOver();
        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
    }

    private void ExecuteAuthoritativePromptResponse(int responderSlot, NetDuelActionRequest req)
    {
        if (pendingPromptType == NetPendingPromptType.None || responderSlot != pendingPromptPlayerSlot)
            return;

        NetPendingPromptType handledType = pendingPromptType;
        string flipName = pendingFlipCardName;
        int atkActorSlot = pendingAttackActorSlot;
        int atkZone = pendingAttackerZone;
        int tgtZone = pendingTargetZone;
        bool isDirectAtk = pendingIsDirectAttack;

        ClearPendingPromptState();
        currentActionLogs.Clear();
        currentActionSfx.Clear();
        currentActionAttack = null;
        currentActionDestroys.Clear();
        currentActionGodSummon = null;

        if (handledType == NetPendingPromptType.FlipSelectGraveyardToDeck)
        {
            List<CardData> gy = authGraveyards[responderSlot];
            List<CardData> deck = authDecks[responderSlot];
            Player responder = authPlayers[responderSlot];

            if (gy.Count > 0)
            {
                int idx = (req.graveyardTargetIndex >= 0 && req.graveyardTargetIndex < gy.Count)
                    ? req.graveyardTargetIndex
                    : 0;

                CardData chosen = gy[idx];
                gy.RemoveAt(idx);
                deck.Add(chosen);
                ShuffleDeck(deck);

                QueueSfx("Draw");
                string sourceLabel = string.IsNullOrEmpty(flipName) ? "Hiệu ứng FLIP" : flipName;
                BroadcastSnapshotsToBoth($"[FLIP - {sourceLabel}] {responder.name} đã đưa [{chosen.cardName}] từ Mộ xáo trộn vào Deck!");
            }
            else
            {
                BroadcastSnapshotsToBoth();
            }
            return;
        }

        if (handledType == NetPendingPromptType.TrapMagicalHats)
        {
            int chosenSpellZone = req.myZoneIndex;
            Player defender = authPlayers[responderSlot];
            Player attackerOwner = (atkActorSlot >= 0 && atkActorSlot <= 1) ? authPlayers[atkActorSlot] : authPlayers[1 - responderSlot];

            // Trường hợp đặc biệt: Kích hoạt Kuriboh từ trên tay (Hiệu ứng nhanh chặn 100% sát thương chiến đấu)
            if (chosenSpellZone == BattleManager.KURIBOH_HAND_REACTION_ZONE)
            {
                List<CardData> hand = authHands[responderSlot];
                int kIdx = hand.FindIndex(c => c != null && c.cardType == CardType.Monster && c.monsterEffect == MonsterEffectType.KuribohHandGuard);
                if (kIdx >= 0)
                {
                    CardData kuriboh = hand[kIdx];
                    hand.RemoveAt(kIdx);
                    SyncAuthoritativeHandCounts();
                    authGraveyards[responderSlot].Add(kuriboh);
                    defender.kuribohShieldActive = true;
                    QueueSfx("Spell");
                    currentActionLogs.Add($"[Hiệu ứng nhanh - {kuriboh.cardName}] {defender.name} gửi {kuriboh.cardName} từ tay xuống Mộ: Mọi sát thương chiến đấu từ đòn đánh này giảm xuống 0!");
                }
                ResolveAuthoritativePausedAttack(atkActorSlot, atkZone, tgtZone, isDirectAtk);
                return;
            }

            if (chosenSpellZone >= 0 && chosenSpellZone < Player.ZoneCount &&
                CanAuthoritativeSetCardReact(responderSlot, chosenSpellZone, out _))
            {
                CardData setCard = authSpellZones[responderSlot][chosenSpellZone];

                // Trường hợp 1: Hồi sinh quái thú từ Mộ (Monster Reborn)
                if (setCard.spellEffect == SpellEffectType.ReviveFromGraveyard)
                {
                    List<CardData> gy = authGraveyards[responderSlot];
                    int gyIdx = (req.graveyardTargetIndex >= 0 && req.graveyardTargetIndex < gy.Count &&
                                 gy[req.graveyardTargetIndex].cardType == CardType.Monster &&
                                 !gy[req.graveyardTargetIndex].CannotBeSpecialSummoned)
                        ? req.graveyardTargetIndex
                        : gy.FindIndex(c => c != null && c.cardType == CardType.Monster && !c.CannotBeSpecialSummoned);

                    if (gyIdx >= 0 && defender.FirstEmptyZone() >= 0)
                    {
                        CardData revivedMonster = gy[gyIdx];
                        gy.RemoveAt(gyIdx);

                        authSpellFaceDown[responderSlot][chosenSpellZone] = false;
                        QueueSfx("Spell");
                        currentActionLogs.Add($"{defender.name} lật kích hoạt [{setCard.cardName}]: Hồi sinh [{revivedMonster.cardName}] từ Mộ bài!");

                        authDuel.Summon(defender, revivedMonster.Clone(), BattlePosition.FaceUpAttack, null, isSpecial: true);
                        ScheduleTransientSpellClear(responderSlot, chosenSpellZone, setCard);

                        CheckAuthoritativeLPGameOver();
                        currentActionLogs.Add($"Sân của {defender.name} xuất hiện quái thú mới! {attackerOwner.name} hãy chọn lại mục tiêu tấn công.");
                        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
                        return;
                    }
                }
                else
                {
                    // Trường hợp 2: Mọi lá Bài Phép / Bài Bẫy khác đang úp (Kiếm Ánh Sáng, Mũ Tơ, Raigeki, Hố Đen, Bình Tham Lam...)
                    if (authDuel.ActivateSpell(defender, setCard, allowOutOfTurnReaction: true))
                    {
                        authSpellFaceDown[responderSlot][chosenSpellZone] = false;
                        if (setCard.spellEffect != SpellEffectType.SwordsOfRevealingLight)
                        {
                            ScheduleTransientSpellClear(responderSlot, chosenSpellZone, setCard);
                        }

                        CheckAuthoritativeLPGameOver();
                        if (isMatchOver)
                        {
                            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
                            return;
                        }

                        // Nếu là Kiếm Ánh Sáng -> Chặn đứng đòn tấn công
                        if (setCard.spellEffect == SpellEffectType.SwordsOfRevealingLight || attackerOwner.swordsBlockTurnsRemaining > 0)
                        {
                            currentActionLogs.Add($"Đòn tấn công của {attackerOwner.name} đã bị chặn đứng!");
                            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
                            return;
                        }

                        // Nếu là Mũ Tơ Ma Thuật -> Quái đã ẩn & xáo vị trí, người tấn công chọn lại mục tiêu
                        if (setCard.spellEffect == SpellEffectType.MagicalHats)
                        {
                            currentActionLogs.Add($"{attackerOwner.name} hãy chọn lại mục tiêu tấn công trong các Mũ Tơ úp!");
                            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
                            return;
                        }

                        // Nếu quái tấn công đã bị tiêu diệt (ví dụ bởi Raigeki / Dark Hole) -> Đòn đánh bị hủy
                        MonsterCard remainingAttacker = (atkZone >= 0 && atkZone < Player.ZoneCount)
                            ? attackerOwner.monsterZones[atkZone]
                            : null;
                        if (remainingAttacker == null)
                        {
                            currentActionLogs.Add("Quái thú tấn công đã bị tiêu diệt trước khi chạm mục tiêu!");
                            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
                            return;
                        }
                    }
                }
            }

            // Nếu người phòng thủ chọn BỎ QUA hoặc lá bài phép không làm hủy đòn đánh (ví dụ rút bài/tăng ATK/hồi LP) -> Tiếp tục giải quyết đòn tấn công!
            ResolveAuthoritativePausedAttack(atkActorSlot, atkZone, tgtZone, isDirectAtk);
            return;
        }

        if (handledType == NetPendingPromptType.TrapHoleOnSummon)
        {
            int chosenSpellZone = req.myZoneIndex;
            Player defender = authPlayers[responderSlot];
            int summonActorSlot = (atkActorSlot >= 0 && atkActorSlot <= 1) ? atkActorSlot : (1 - responderSlot);
            Player summonOwner = authPlayers[summonActorSlot];
            MonsterCard summonedTarget = (tgtZone >= 0 && tgtZone < Player.ZoneCount)
                ? summonOwner.monsterZones[tgtZone]
                : null;

            if (chosenSpellZone >= 0 && chosenSpellZone < Player.ZoneCount &&
                summonedTarget != null &&
                Duel.CanActivateTrapHoleOnSummon(authSpellZones[responderSlot][chosenSpellZone], summonedTarget, out _))
            {
                CardData trapCard = authSpellZones[responderSlot][chosenSpellZone];
                if (authDuel.ActivateTrapHoleOnSummon(defender, trapCard, summonedTarget))
                {
                    authSpellFaceDown[responderSlot][chosenSpellZone] = false;
                    ScheduleTransientSpellClear(responderSlot, chosenSpellZone, trapCard);
                    CheckAuthoritativeLPGameOver();
                    BroadcastSnapshotsToBoth(BuildCombinedActionLog());
                    return;
                }
            }

            // Nếu người phòng thủ bỏ qua Hang Rơi và quái vừa triệu hồi là Thần Ra -> hỏi người triệu hồi có trả LP còn 100 không
            if (summonedTarget != null &&
                summonOwner.monsterZones[tgtZone] == summonedTarget &&
                summonedTarget.data != null &&
                summonedTarget.data.monsterEffect == MonsterEffectType.DivineRa &&
                !summonedTarget.isSpecialSummoned &&
                summonOwner.lifePoints > 100)
            {
                pendingPromptType = NetPendingPromptType.RaSummonPayLP;
                pendingPromptId++;
                pendingPromptPlayerSlot = summonActorSlot;
                pendingAttackerZone = tgtZone;

                if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
                promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 15f));

                BroadcastSnapshotsToBoth($"[{summonedTarget.data.cardName}] Đang chờ {summonOwner.name} chọn có trả LP xuống còn 100 để truyền ATK/DEF không...");
                return;
            }

            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        if (handledType == NetPendingPromptType.RaSummonPayLP)
        {
            Player responder = authPlayers[responderSlot];
            MonsterCard raCard = (atkZone >= 0 && atkZone < Player.ZoneCount)
                ? responder.monsterZones[atkZone]
                : null;

            if (req.myZoneIndex == 1 && raCard != null &&
                raCard.data != null && raCard.data.monsterEffect == MonsterEffectType.DivineRa)
            {
                if (authDuel.ActivateRaPointToPointTransfer(responder, raCard))
                {
                    QueueSfx("Spell");
                }
            }

            CheckAuthoritativeLPGameOver();
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        if (handledType == NetPendingPromptType.EndPhaseDiscardHand)
        {
            List<CardData> hand = authHands[responderSlot];
            List<CardData> gy = authGraveyards[responderSlot];
            Player responder = authPlayers[responderSlot];

            // Người chơi bấm "HỦY KẾT THÚC LƯỢT (QUAY LẠI)" -> quay lại Main Phase / Battle Phase để tiếp tục triệu hồi quái hoặc dùng bài phép
            if (req.graveyardTargetIndex < 0)
            {
                authTurnState = (authTurnStateBeforeEndPhaseDiscard == BattleManager.TurnState.BattlePhase)
                    ? BattleManager.TurnState.BattlePhase
                    : BattleManager.TurnState.MainPhase;
                string phaseLabel = authTurnState == BattleManager.TurnState.BattlePhase ? "Battle Phase" : "Main Phase";
                BroadcastSnapshotsToBoth($"Đã hủy Kết thúc lượt — {responder.name} quay lại {phaseLabel} để tiếp tục thao tác!");
                return;
            }

            if (hand.Count > BattleManager.MAX_HAND_SIZE_END_PHASE)
            {
                int idx = (req.graveyardTargetIndex >= 0 && req.graveyardTargetIndex < hand.Count)
                    ? req.graveyardTargetIndex
                    : 0;
                CardData discarded = hand[idx];
                hand.RemoveAt(idx);
                SyncAuthoritativeHandCounts();
                if (discarded != null)
                {
                    gy.Add(discarded);
                }
                QueueSfx("Summon");

                if (hand.Count > BattleManager.MAX_HAND_SIZE_END_PHASE)
                {
                    int remainExcess = hand.Count - BattleManager.MAX_HAND_SIZE_END_PHASE;
                    pendingPromptType = NetPendingPromptType.EndPhaseDiscardHand;
                    pendingPromptId++;
                    pendingPromptPlayerSlot = responderSlot;

                    BroadcastSnapshotsToBoth(
                        $"[End Phase] {responder.name} đã bỏ [{discarded.cardName}] xuống Mộ. Cần chọn bỏ thêm {remainExcess} lá nữa (còn {hand.Count}/{BattleManager.MAX_HAND_SIZE_END_PHASE} lá)!"
                    );
                    return;
                }

                currentActionLogs.Add($"[End Phase] {responder.name} đã bỏ [{discarded.cardName}] xuống Mộ (trên tay còn đúng {hand.Count} lá).");
            }

            FinishAuthoritativeEndTurn();
            return;
        }
    }

    // ==================== THỰC THI QUÁI THÚ / CHIẾN ĐẤU / KẾT THÚC LƯỢT (BƯỚC 4) ====================

    private void ExecuteAuthoritativeSummon(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        if (authTurnState != BattleManager.TurnState.MainPhase)
        {
            SendStatusFeedbackToSlot(actorSlot, "Chỉ có thể triệu hồi quái thú trong Main Phase!");
            return;
        }

        List<CardData> hand = authHands[actorSlot];
        if (req.handIndex < 0 || req.handIndex >= hand.Count)
        {
            SendStatusFeedbackToSlot(actorSlot, "Lá bài trên tay không hợp lệ!");
            return;
        }

        CardData cardToSummon = hand[req.handIndex];
        if (cardToSummon == null || cardToSummon.cardType != CardType.Monster)
        {
            SendStatusFeedbackToSlot(actorSlot, "Lá bài này không phải Quái thú!");
            return;
        }

        List<MonsterCard> tributes = new List<MonsterCard>();
        if (req.tributeZones != null)
        {
            foreach (int z in req.tributeZones.Distinct())
            {
                if (z >= 0 && z < Player.ZoneCount && actor.monsterZones[z] != null)
                {
                    tributes.Add(actor.monsterZones[z]);
                }
            }
        }

        BattlePosition pos = (BattlePosition)req.position;
        hand.RemoveAt(req.handIndex);
        SyncAuthoritativeHandCounts();

        MonsterCard summoned = authDuel.Summon(actor, cardToSummon.Clone(), pos, tributes, isSpecial: false, preferredZone: req.preferredZone);
        if (summoned == null)
        {
            hand.Insert(Mathf.Clamp(req.handIndex, 0, hand.Count), cardToSummon);
            SyncAuthoritativeHandCounts();
            SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Không thể triệu hồi quái thú này."));
            return;
        }

        foreach (var t in tributes)
        {
            if (t != null && t.data != null && !t.isMagicalHatDecoy)
            {
                authGraveyards[actorSlot].Add(t.data);
            }
        }

        CheckAuthoritativeLPGameOver();

        int defenderSlot = 1 - actorSlot;
        bool stillOnFieldFaceUp = !isMatchOver && !summoned.IsFaceDown &&
                                  summoned.zoneIndex >= 0 && summoned.zoneIndex < Player.ZoneCount &&
                                  actor.monsterZones[summoned.zoneIndex] == summoned;

        // 1. Kiểm tra nếu đối thủ có Hang Rơi (Trap Hole) đặt úp và quái vừa triệu hồi thường có ATK >= 1000 (không áp dụng cho 3 Thần Ai Cập)
        if (stillOnFieldFaceUp && !summoned.data.IsDivineGod &&
            pendingPromptType == NetPendingPromptType.None &&
            HasAuthoritativeActivatableTrapHoleOnSummon(defenderSlot, summoned))
        {
            pendingPromptType = NetPendingPromptType.TrapHoleOnSummon;
            pendingPromptId++;
            pendingPromptPlayerSlot = defenderSlot;
            pendingAttackActorSlot = actorSlot;
            pendingTargetZone = summoned.zoneIndex;

            if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
            promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 15f));

            BroadcastSnapshotsToBoth(BuildCombinedActionLog($"{actor.name} vừa triệu hồi [{summoned.data.cardName}] (ATK {summoned.Atk})! Đang chờ {authPlayers[defenderSlot].name} phản hồi Bẫy..."));
            return;
        }

        // 2. Nếu là Rồng Cánh Thần Của Ra vừa Triệu hồi Thường -> Hỏi người chơi có trả LP xuống còn 100 để cộng ATK/DEF không
        if (stillOnFieldFaceUp && summoned.data.monsterEffect == MonsterEffectType.DivineRa &&
            actor.lifePoints > 100 && pendingPromptType == NetPendingPromptType.None)
        {
            pendingPromptType = NetPendingPromptType.RaSummonPayLP;
            pendingPromptId++;
            pendingPromptPlayerSlot = actorSlot;
            pendingAttackerZone = summoned.zoneIndex;

            if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
            promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 15f));

            BroadcastSnapshotsToBoth(BuildCombinedActionLog($"[{summoned.data.cardName}] Đang chờ {actor.name} chọn có trả LP xuống còn 100 để truyền ATK/DEF không..."));
            return;
        }

        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
    }

    private void ExecuteAuthoritativeChangePosition(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        if (authTurnState != BattleManager.TurnState.MainPhase)
        {
            SendStatusFeedbackToSlot(actorSlot, "Chỉ có thể đổi thế quái thú trong Main Phase!");
            return;
        }

        if (req.myZoneIndex < 0 || req.myZoneIndex >= Player.ZoneCount)
        {
            return;
        }

        MonsterCard card = actor.monsterZones[req.myZoneIndex];
        if (card == null) return;

        bool wasFaceDown = card.IsFaceDown;
        bool changed = authDuel.ChangePosition(actor, card);
        if (!changed)
        {
            SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Không thể đổi thế quái thú này."));
            return;
        }

        CheckAuthoritativeLPGameOver();

        int defenderSlot = 1 - actorSlot;
        if (!isMatchOver && wasFaceDown && !card.IsFaceDown &&
            card.zoneIndex >= 0 && card.zoneIndex < Player.ZoneCount &&
            actor.monsterZones[card.zoneIndex] == card &&
            pendingPromptType == NetPendingPromptType.None &&
            HasAuthoritativeActivatableTrapHoleOnSummon(defenderSlot, card))
        {
            pendingPromptType = NetPendingPromptType.TrapHoleOnSummon;
            pendingPromptId++;
            pendingPromptPlayerSlot = defenderSlot;
            pendingAttackActorSlot = actorSlot;
            pendingTargetZone = card.zoneIndex;

            if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
            promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 15f));

            BroadcastSnapshotsToBoth(BuildCombinedActionLog($"{actor.name} vừa lật triệu hồi [{card.data.cardName}] (ATK {card.Atk})! Đang chờ {authPlayers[defenderSlot].name} phản hồi Bẫy..."));
            return;
        }

        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
    }

    private bool TryEnterAuthoritativeBattlePhase(int actorSlot, Player actor, out string failReason)
    {
        failReason = "";
        if (authTurnState == BattleManager.TurnState.BattlePhase) return true;
        if (authTurnState != BattleManager.TurnState.MainPhase)
        {
            failReason = "Không thể vào Battle Phase lúc này.";
            return false;
        }
        if (authDuel.turnCount == 1)
        {
            failReason = "Không được tấn công ở lượt đầu tiên của trận đấu!";
            return false;
        }
        if (actor.swordsBlockTurnsRemaining > 0)
        {
            failReason = $"Lớp Kiếm Bảo Hộ Của Ánh Sáng đang chặn tấn công (còn {actor.swordsBlockTurnsRemaining} lượt)!";
            return false;
        }

        authTurnState = BattleManager.TurnState.BattlePhase;
        declinedMagicalHatsThisBattlePhase = false;
        return true;
    }

    private void ExecuteAuthoritativeEnterBattlePhase(int actorSlot, Player actor)
    {
        if (!TryEnterAuthoritativeBattlePhase(actorSlot, actor, out string reason))
        {
            SendStatusFeedbackToSlot(actorSlot, reason);
            return;
        }

        BroadcastSnapshotsToBoth($"Lượt {authDuel.turnCount}: {actor.name} bước vào Battle Phase!");
    }

    private void ExecuteAuthoritativeAttackMonster(int actorSlot, Player actor, Player enemy, NetDuelActionRequest req)
    {
        if (!TryEnterAuthoritativeBattlePhase(actorSlot, actor, out string reason))
        {
            SendStatusFeedbackToSlot(actorSlot, reason);
            return;
        }

        if (req.myZoneIndex < 0 || req.myZoneIndex >= Player.ZoneCount ||
            req.targetOpponentZone < 0 || req.targetOpponentZone >= Player.ZoneCount)
        {
            return;
        }

        MonsterCard attacker = actor.monsterZones[req.myZoneIndex];
        MonsterCard target = enemy.monsterZones[req.targetOpponentZone];
        if (attacker == null || target == null)
        {
            SendStatusFeedbackToSlot(actorSlot, "Mục tiêu tấn công không hợp lệ!");
            return;
        }
        if (!attacker.CanAttack)
        {
            SendStatusFeedbackToSlot(actorSlot, $"{attacker.data.cardName} không thể tấn công!");
            return;
        }

        int defenderSlot = 1 - actorSlot;
        // Kiểm tra nếu người phòng thủ có BẤT KỲ lá bài Phép/Bẫy nào đang úp có thể kích hoạt khi bị tấn công
        if (HasAuthoritativeActivatableSetCardForReaction(defenderSlot))
        {
            pendingPromptType = NetPendingPromptType.TrapMagicalHats;
            pendingPromptId++;
            pendingPromptPlayerSlot = defenderSlot;
            pendingAttackActorSlot = actorSlot;
            pendingAttackerZone = req.myZoneIndex;
            pendingTargetZone = req.targetOpponentZone;
            pendingIsDirectAttack = false;

            if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
            promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 20f));

            BroadcastSnapshotsToBoth($"{actor.name} tuyên bố tấn công bằng [{attacker.data.cardName}]! Đang chờ {enemy.name} chọn kích hoạt bài úp...");
            return;
        }

        ResolveAuthoritativePausedAttack(actorSlot, req.myZoneIndex, req.targetOpponentZone, isDirectAttack: false);
    }

    private void ResolveAuthoritativePausedAttack(int actorSlot, int attackerZone, int targetZone, bool isDirectAttack)
    {
        if (actorSlot < 0 || actorSlot > 1 || isMatchOver) return;
        Player actor = authPlayers[actorSlot];
        Player enemy = authPlayers[1 - actorSlot];

        if (attackerZone < 0 || attackerZone >= Player.ZoneCount)
        {
            BroadcastSnapshotsToBoth();
            return;
        }

        MonsterCard attacker = actor.monsterZones[attackerZone];
        if (attacker == null || !attacker.CanAttack)
        {
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        if (isDirectAttack)
        {
            if (enemy.MonsterCount > 0)
            {
                BroadcastSnapshotsToBoth(BuildCombinedActionLog($"Sân của {enemy.name} đã có quái thú, không thể đánh trực tiếp!"));
                return;
            }

            bool directOk = authDuel.DirectAttack(actor, attacker);
            if (!directOk)
            {
                SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Không thể tấn công trực tiếp."));
                return;
            }

            CheckAuthoritativeLPGameOver();
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        if (targetZone < 0 || targetZone >= Player.ZoneCount)
        {
            BroadcastSnapshotsToBoth(BuildCombinedActionLog());
            return;
        }

        MonsterCard target = enemy.monsterZones[targetZone];
        if (target == null)
        {
            BroadcastSnapshotsToBoth(BuildCombinedActionLog("Mục tiêu tấn công không còn trên sân! Hãy chọn mục tiêu khác."));
            return;
        }

        bool attacked = authDuel.Attack(actor, attacker, target);
        if (!attacked)
        {
            SendStatusFeedbackToSlot(actorSlot, BuildCombinedActionLog("Không thể thực hiện đòn tấn công."));
            return;
        }

        CheckAuthoritativeLPGameOver();
        BroadcastSnapshotsToBoth(BuildCombinedActionLog());
    }

    private void ExecuteAuthoritativeDirectAttack(int actorSlot, Player actor, NetDuelActionRequest req)
    {
        if (!TryEnterAuthoritativeBattlePhase(actorSlot, actor, out string reason))
        {
            SendStatusFeedbackToSlot(actorSlot, reason);
            return;
        }

        if (req.myZoneIndex < 0 || req.myZoneIndex >= Player.ZoneCount) return;

        MonsterCard attacker = actor.monsterZones[req.myZoneIndex];
        if (attacker == null) return;
        if (!attacker.CanAttack)
        {
            SendStatusFeedbackToSlot(actorSlot, $"{attacker.data.cardName} không thể tấn công!");
            return;
        }

        int defenderSlot = 1 - actorSlot;
        Player enemy = authPlayers[defenderSlot];
        if (enemy.MonsterCount > 0)
        {
            SendStatusFeedbackToSlot(actorSlot, "Đối phương còn quái trên sân, không thể đánh trực tiếp!");
            return;
        }

        // Kiểm tra nếu người phòng thủ có lá bài Phép/Bẫy đang úp có thể kích hoạt khi bị tấn công trực tiếp
        if (HasAuthoritativeActivatableSetCardForReaction(defenderSlot))
        {
            pendingPromptType = NetPendingPromptType.TrapMagicalHats;
            pendingPromptId++;
            pendingPromptPlayerSlot = defenderSlot;
            pendingAttackActorSlot = actorSlot;
            pendingAttackerZone = req.myZoneIndex;
            pendingTargetZone = -1;
            pendingIsDirectAttack = true;

            if (promptTimeoutCoroutine != null) StopCoroutine(promptTimeoutCoroutine);
            promptTimeoutCoroutine = StartCoroutine(PromptTimeoutRoutine(pendingPromptId, 20f));

            BroadcastSnapshotsToBoth($"{actor.name} tuyên bố tấn công trực tiếp bằng [{attacker.data.cardName}]! Đang chờ {enemy.name} chọn kích hoạt bài úp...");
            return;
        }

        ResolveAuthoritativePausedAttack(actorSlot, req.myZoneIndex, -1, isDirectAttack: true);
    }

    private void ExecuteAuthoritativeEndTurn(int actorSlot)
    {
        if (authTurnState != BattleManager.TurnState.MainPhase && authTurnState != BattleManager.TurnState.BattlePhase)
        {
            authTurnState = BattleManager.TurnState.MainPhase;
        }

        authTurnStateBeforeEndPhaseDiscard = authTurnState;
        declinedMagicalHatsThisBattlePhase = false;

        // Luật End Phase: Nếu người chơi đang kết thúc lượt có nhiều hơn 6 lá bài trên tay -> bắt buộc chọn bỏ xuống Mộ cho tới khi còn đúng 6 lá
        List<CardData> hand = authHands[actorSlot];
        if (hand != null && hand.Count > BattleManager.MAX_HAND_SIZE_END_PHASE)
        {
            int excess = hand.Count - BattleManager.MAX_HAND_SIZE_END_PHASE;
            pendingPromptType = NetPendingPromptType.EndPhaseDiscardHand;
            pendingPromptId++;
            pendingPromptPlayerSlot = actorSlot;

            BroadcastSnapshotsToBoth(
                $"[End Phase] {authPlayers[actorSlot].name} đang có {hand.Count} lá trên tay — Bắt buộc chọn bỏ {excess} lá xuống Mộ (giới hạn {BattleManager.MAX_HAND_SIZE_END_PHASE} lá)!"
            );
            return;
        }

        FinishAuthoritativeEndTurn();
    }

    private void FinishAuthoritativeEndTurn()
    {
        authTurnState = BattleManager.TurnState.EndPhase;
        authDuel.EndTurn();

        int nextSlot = authDuel.currentIndex;
        Player nextPlayer = authPlayers[nextSlot];

        authTurnState = BattleManager.TurnState.DrawPhase;
        if (!DrawCardAuthoritative(nextSlot, checkDeckOut: true))
        {
            BroadcastSnapshotsToBoth(matchOverReason);
            return;
        }

        authTurnState = BattleManager.TurnState.MainPhase;
        QueueSfx("Draw");

        string turnStatus = BuildCombinedActionLog($"Lượt {authDuel.turnCount}: Tới lượt của {nextPlayer.name} (Đã rút 1 lá bài)");
        BroadcastSnapshotsToBoth(turnStatus);
    }

    private string BuildCombinedActionLog(string fallback = null)
    {
        if (currentActionLogs.Count == 0) return fallback;
        if (currentActionLogs.Count == 1) return currentActionLogs[0];

        int start = Mathf.Max(0, currentActionLogs.Count - 3);
        return string.Join(" | ", currentActionLogs.Skip(start));
    }

    private void ExecuteAuthoritativeRematchVote(int actorSlot)
    {
        if (actorSlot < 0 || actorSlot > 1) return;

        ulong clientId = ResolveRemoteClientId();
        if (clientId == ulong.MaxValue || networkManager == null ||
            networkManager.ConnectedClientsIds == null || !networkManager.ConnectedClientsIds.Contains(clientId))
        {
            SendStatusFeedbackToSlot(actorSlot, "Đối thủ đã rời trận. Hãy bấm 'VỀ MENU' để tạo hoặc vào phòng mới!");
            return;
        }

        rematchVotes[actorSlot] = true;
        int otherSlot = 1 - actorSlot;

        if (!rematchVotes[otherSlot])
        {
            string voterName = authPlayers[actorSlot].name;
            SendStatusFeedbackToSlot(actorSlot, "Đã gửi yêu cầu ĐẤU LẠI (1/2) - Đang chờ đối thủ bấm 'CHƠI LẠI'...");
            SendStatusFeedbackToSlot(otherSlot, $"{voterName} muốn ĐẤU LẠI (1/2)! Bấm nút 'CHƠI LẠI' để bắt đầu ván mới.");
            return;
        }

        // Cả 2 người chơi đã đồng ý Đấu lại -> Khởi tạo ngay ván đấu mới trên BattleScene!
        if (BattleManager.Instance != null)
        {
            pendingRematchResetFlag = true;
            InitializeHostMatch(BattleManager.Instance);
            pendingRematchResetFlag = false;
        }
    }

    // ==================== BUILD FILTERED & PERSPECTIVE-FLIPPED SNAPSHOT ====================

    public MatchStateSnapshot BuildSnapshotForViewer(int viewerSlot, string customStatus = null)
    {
        SyncAuthoritativeHandCounts();
        int oppSlot = 1 - viewerSlot;
        Player me = authPlayers[viewerSlot];
        Player opp = authPlayers[oppSlot];

        bool isMyTurn = (!isMatchOver && authDuel != null && authDuel.currentIndex == viewerSlot);
        int turnNum = authDuel != null ? authDuel.turnCount : 1;

        string statusMsg = customStatus;
        if (string.IsNullOrEmpty(statusMsg))
        {
            if (isMatchOver)
            {
                bool won = (viewerSlot == matchWinnerSlot);
                statusMsg = $"{(won ? "CHIẾN THẮNG!" : "THẤT BẠI!")} ({matchOverReason})";
            }
            else
            {
                string phaseLabel = authTurnState == BattleManager.TurnState.BattlePhase ? "Battle Phase" : "Main Phase";
                statusMsg = isMyTurn
                    ? $"Lượt {turnNum}: Lượt của bạn ({me.name}) - {phaseLabel}"
                    : $"Lượt {turnNum}: Đang là lượt của {opp.name} ({phaseLabel})";
            }
        }

        string promptTitleStr = "";
        if (pendingPromptType == NetPendingPromptType.FlipSelectGraveyardToDeck)
        {
            promptTitleStr = $"[FLIP - {pendingFlipCardName}] CHỌN 1 LÁ TRONG MỘ TRẢ VỀ DECK";
        }
        else if (pendingPromptType == NetPendingPromptType.TrapMagicalHats)
        {
            MonsterCard atkCard = (pendingAttackActorSlot >= 0 && pendingAttackActorSlot <= 1 &&
                                   pendingAttackerZone >= 0 && pendingAttackerZone < Player.ZoneCount)
                ? authPlayers[pendingAttackActorSlot].monsterZones[pendingAttackerZone]
                : null;
            string atkLabel = atkCard != null ? $"[{atkCard.data.cardName}] (ATK {atkCard.Atk})" : "Quái thú";
            string targetLabel = pendingIsDirectAttack ? "trực tiếp vào LP của bạn" : "quái thú của bạn";
            promptTitleStr = $"{opp.name} dùng {atkLabel} tấn công {targetLabel}!\nCHỌN 1 LÁ BÀI PHÉP / BẪY ĐANG ÚP (HOẶC KURIBOH TRÊN TAY) ĐỂ KÍCH HOẠT";
        }
        else if (pendingPromptType == NetPendingPromptType.TrapHoleOnSummon)
        {
            MonsterCard sumCard = (pendingAttackActorSlot >= 0 && pendingAttackActorSlot <= 1 &&
                                   pendingTargetZone >= 0 && pendingTargetZone < Player.ZoneCount)
                ? authPlayers[pendingAttackActorSlot].monsterZones[pendingTargetZone]
                : null;
            string sumLabel = sumCard != null ? $"[{sumCard.data.cardName}] (ATK {sumCard.Atk})" : "Quái thú (ATK >= 1000)";
            promptTitleStr = $"{opp.name} vừa triệu hồi {sumLabel}!\nKÍCH HOẠT BẪY [HANG RƠI] ĐỂ PHÁ HỦY MỤC TIÊU NÀY?";
        }
        else if (pendingPromptType == NetPendingPromptType.RaSummonPayLP)
        {
            int payAmt = Mathf.Max(0, me.lifePoints - 100);
            promptTitleStr = $"[RỒNG CÁNH THẦN CỦA RA] Bạn có muốn trả {payAmt} LP (chỉ giữ lại 100 LP) để truyền +{payAmt} ATK / +{payAmt} DEF cho Thần Ra không?";
        }
        else if (pendingPromptType == NetPendingPromptType.EndPhaseDiscardHand)
        {
            int myCount = authHands[viewerSlot] != null ? authHands[viewerSlot].Count : 0;
            int excess = Mathf.Max(1, myCount - BattleManager.MAX_HAND_SIZE_END_PHASE);
            promptTitleStr = $"[END PHASE - GIỚI HẠN {BattleManager.MAX_HAND_SIZE_END_PHASE} LÁ TRÊN TAY] BẠN ĐANG CÓ {myCount} LÁ — BẮT BUỘC CHỌN BỎ {excess} LÁ XUỐNG MỘ";
        }

        int activeDecisionSlot = GetActiveDecisionSlot();
        int currentAfkCountdown = -1;
        if (!isMatchOver && remoteClientSceneReady && afkIdleElapsed >= BattleManager.AFK_IDLE_GRACE_SECONDS)
        {
            float countdownElapsed = afkIdleElapsed - BattleManager.AFK_IDLE_GRACE_SECONDS;
            currentAfkCountdown = Mathf.CeilToInt(Mathf.Max(1f, BattleManager.AFK_COUNTDOWN_SECONDS - countdownElapsed));
        }

        MatchStateSnapshot snap = new MatchStateSnapshot
        {
            snapshotSeq = snapshotSeq,
            turnCount = turnNum,
            isMyTurn = isMyTurn,
            turnState = (int)authTurnState,
            statusMessage = statusMsg,
            sfxToPlay = currentActionSfx.Count > 0 ? currentActionSfx[0] : "",
            sfxList = new List<string>(currentActionSfx),

            // Góc nhìn người xem (luôn nạp vào humanPlayer ở phía dưới bàn đấu)
            myName = me.name,
            myLP = me.lifePoints,
            myDeckCount = authDecks[viewerSlot].Count,
            myNormalSummonUsed = me.normalSummonUsed,
            mySwordsBlockTurns = me.swordsBlockTurnsRemaining,
            myHandCardNames = new List<string>(),
            myGraveyardCardNames = new List<string>(),
            myMonsters = new NetMonsterSlotDto[Player.ZoneCount],
            mySpells = new NetSpellSlotDto[Player.ZoneCount],

            // Góc nhìn đối thủ (luôn nạp vào aiPlayer ở phía trên bàn đấu - ẩn bài trên tay & bài úp)
            opponentName = opp.name,
            opponentLP = opp.lifePoints,
            opponentDeckCount = authDecks[oppSlot].Count,
            opponentHandCount = authHands[oppSlot].Count,
            opponentNormalSummonUsed = opp.normalSummonUsed,
            opponentHasMonsters = (opp.MonsterCount > 0),
            opponentSwordsBlockTurns = opp.swordsBlockTurnsRemaining,
            opponentGraveyardCardNames = new List<string>(),
            opponentMonsters = new NetMonsterSlotDto[Player.ZoneCount],
            opponentSpells = new NetSpellSlotDto[Player.ZoneCount],

            // Thông tin Prompt bất đồng bộ (Bước 5)
            pendingPromptType = (int)pendingPromptType,
            pendingPromptId = pendingPromptId,
            isPromptForMe = (pendingPromptType != NetPendingPromptType.None && viewerSlot == pendingPromptPlayerSlot),
            promptTitle = promptTitleStr,

            isGameOver = isMatchOver,
            didLocalWin = (isMatchOver && viewerSlot == matchWinnerSlot),
            gameOverReason = matchOverReason,
            isRematchReset = pendingRematchResetFlag,
            afkCountdownSeconds = currentAfkCountdown,
            isAfkCountdownForMe = (activeDecisionSlot == viewerSlot)
        };

        foreach (var c in authHands[viewerSlot])
        {
            if (c != null) snap.myHandCardNames.Add(c.cardName);
        }

        foreach (var c in authGraveyards[viewerSlot])
        {
            if (c != null) snap.myGraveyardCardNames.Add(c.cardName);
        }
        foreach (var c in authGraveyards[oppSlot])
        {
            if (c != null) snap.opponentGraveyardCardNames.Add(c.cardName);
        }

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            snap.myMonsters[i] = BuildMonsterSlotDto(me.monsterZones[i], hideIfFaceDown: false);
            snap.opponentMonsters[i] = BuildMonsterSlotDto(opp.monsterZones[i], hideIfFaceDown: true);
        }

        for (int i = 0; i < Player.ZoneCount; i++)
        {
            CardData mySp = authSpellZones[viewerSlot][i];
            bool myFd = authSpellFaceDown[viewerSlot][i];
            string reactHint = "";
            bool canReact = myFd && CanAuthoritativeSetCardReact(viewerSlot, i, out reactHint);

            snap.mySpells[i] = new NetSpellSlotDto
            {
                hasCard = (mySp != null),
                cardName = mySp != null ? mySp.cardName : "",
                isFaceDown = myFd,
                canActivateInReaction = canReact,
                reactionHint = reactHint ?? ""
            };

            CardData oppSp = authSpellZones[oppSlot][i];
            bool oppFd = authSpellFaceDown[oppSlot][i];
            snap.opponentSpells[i] = new NetSpellSlotDto
            {
                hasCard = (oppSp != null),
                cardName = (oppSp != null && !oppFd) ? oppSp.cardName : "",
                isFaceDown = oppFd,
                canActivateInReaction = false,
                reactionHint = ""
            };
        }

        // Hiệu ứng hình ảnh (VFX) lật góc nhìn theo từng người chơi
        if (currentActionAttack != null)
        {
            snap.vfxAttack = new NetVfxAttackDto
            {
                hasAttack = true,
                attackerIsMe = (currentActionAttack.attackerSlot == viewerSlot),
                attackerZone = currentActionAttack.attackerZone,
                isDirectAttack = currentActionAttack.isDirectAttack,
                targetIsMe = (currentActionAttack.targetSlot == viewerSlot),
                targetZone = currentActionAttack.targetZone
            };
        }

        if (currentActionDestroys.Count > 0)
        {
            foreach (var d in currentActionDestroys)
            {
                snap.vfxDestroys.Add(new NetVfxDestroyDto
                {
                    isMe = (d.ownerSlot == viewerSlot),
                    zoneIndex = d.zoneIndex,
                    isDefense = d.isDefense,
                    cardName = d.cardName ?? ""
                });
            }
        }

        if (currentActionGodSummon != null)
        {
            snap.vfxGodSummon = new NetVfxGodSummonDto
            {
                hasGodSummon = true,
                godEffectType = (int)currentActionGodSummon.godEffect,
                isMe = (currentActionGodSummon.ownerSlot == viewerSlot),
                zoneIndex = currentActionGodSummon.zoneIndex,
                cardName = currentActionGodSummon.cardName ?? ""
            };
        }

        return snap;
    }

    private NetMonsterSlotDto BuildMonsterSlotDto(MonsterCard mc, bool hideIfFaceDown)
    {
        if (mc == null)
        {
            return new NetMonsterSlotDto { hasCard = false };
        }

        bool maskIdentity = hideIfFaceDown && mc.IsFaceDown;
        string resolvedName = "";
        if (!maskIdentity)
        {
            if (mc.isMagicalHatDecoy && mc.originalSpellTrapData != null)
            {
                resolvedName = mc.originalSpellTrapData.cardName;
            }
            else if (mc.data != null)
            {
                resolvedName = mc.data.cardName;
            }
        }

        return new NetMonsterSlotDto
        {
            hasCard = true,
            cardName = resolvedName,
            position = (int)mc.position,
            atkModifier = maskIdentity ? 0 : mc.atkModifier,
            defModifier = maskIdentity ? 0 : mc.defModifier,
            summonedThisTurn = mc.summonedThisTurn,
            hasAttacked = mc.hasAttacked,
            positionChanged = mc.positionChanged,
            faceUpEffectTriggered = mc.faceUpEffectTriggered,
            isMagicalHatDecoy = maskIdentity ? false : mc.isMagicalHatDecoy,
            isSpecialSummoned = mc.isSpecialSummoned
        };
    }

    // ==================== GỬI & NHẬN SNAPSHOT / FEEDBACK QUA MẠNG ====================

    private void BroadcastSnapshotsToBoth(string customStatus = null, bool keepExistingStatusOnClient = false)
    {
        BroadcastSnapshotsToBoth(
            customStatusForSlot0: customStatus,
            customStatusForSlot1: customStatus,
            keepExistingStatus: keepExistingStatusOnClient
        );
    }

    private void BroadcastSnapshotsToBoth(string customStatusForSlot0, string customStatusForSlot1, bool keepExistingStatus = false)
    {
        if (!MatchSessionConfig.LocalIsHost || !hostMatchInitialized) return;

        // Mọi thao tác game hợp lệ dẫn tới cập nhật bàn đấu đều đặt lại đồng hồ Anti-AFK
        if (!keepExistingStatus && !isMatchOver)
        {
            ResetAuthoritativeAfkTimer();
        }

        snapshotSeq++;

        if (BattleManager.Instance != null)
        {
            MatchStateSnapshot hostSnap = BuildSnapshotForViewer(0, customStatusForSlot0);
            if (keepExistingStatus) hostSnap.statusMessage = "";
            BattleManager.Instance.ApplyNetworkSnapshot(hostSnap);
        }

        SendSnapshotToClient(targetClientId: null, customStatus: customStatusForSlot1, clearStatusIfEmpty: keepExistingStatus);

        // Xóa sự kiện VFX sau khi đã gửi cho cả Host và Client để không phát lặp ở snapshot sau
        currentActionAttack = null;
        currentActionDestroys.Clear();
        currentActionGodSummon = null;
    }

    private void SendStatusFeedbackToSlot(int targetSlot, string message, string sfxId = "")
    {
        if (string.IsNullOrEmpty(message)) return;

        if (targetSlot == 0)
        {
            if (BattleManager.Instance != null)
            {
                BattleManager.Instance.SetStatus(message);
                BattleManager.Instance.UpdateGameOverReasonIfVisible(message);
            }
        }
        else
        {
            if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening) return;
            ulong clientId = ResolveRemoteClientId();
            if (clientId != ulong.MaxValue && clientId != NetworkManager.ServerClientId &&
                networkManager.ConnectedClientsIds != null && networkManager.ConnectedClientsIds.Contains(clientId))
            {
                SendJsonMessage(MSG_DUEL_STATUS_ONLY, clientId, new NetStatusFeedbackDto
                {
                    message = message,
                    sfxId = sfxId ?? ""
                });
            }
        }
    }

    private IEnumerator ClientHandshakeRoutine()
    {
        while (!clientReceivedSnapshot)
        {
            if (networkManager == null || !networkManager.IsListening)
            {
                yield break;
            }
            if (networkManager.IsConnectedClient)
            {
                SendJsonMessage(MSG_CLIENT_SCENE_READY, NetworkManager.ServerClientId, new NetClientReadyDto
                {
                    playerName = MatchSessionConfig.GetLocalDisplayName()
                });
            }
            yield return new WaitForSecondsRealtime(0.35f);
        }
    }

    private void OnReceiveClientSceneReady(ulong senderClientId, FastBufferReader reader)
    {
        if (!MatchSessionConfig.LocalIsHost || !hostMatchInitialized) return;
        if (senderClientId == NetworkManager.ServerClientId) return;

        Debug.Log($"[NetworkDuel][Host] Client (#{senderClientId}) đã tải xong BattleScene -> Gửi Snapshot!");
        remoteClientSceneReady = true;
        ResetAuthoritativeAfkTimer();
        currentActionSfx.Clear();
        QueueSfx("Draw");
        SendSnapshotToClient(targetClientId: senderClientId, customStatus: null);
    }

    private void SendSnapshotToClient(ulong? targetClientId = null, string customStatus = null, bool clearStatusIfEmpty = false)
    {
        if (!MatchSessionConfig.LocalIsHost || !hostMatchInitialized) return;
        if (networkManager == null || !networkManager.IsServer || !networkManager.IsListening) return;

        ulong clientId = targetClientId ?? ResolveRemoteClientId();
        if (clientId == ulong.MaxValue || clientId == NetworkManager.ServerClientId) return;
        if (networkManager.ConnectedClientsIds == null || !networkManager.ConnectedClientsIds.Contains(clientId)) return;

        MatchStateSnapshot clientSnap = BuildSnapshotForViewer(
            viewerSlot: 1,
            customStatus: customStatus
        );
        if (clearStatusIfEmpty) clientSnap.statusMessage = "";
        SendJsonMessage(MSG_DUEL_SNAPSHOT, clientId, clientSnap);
    }

    private ulong ResolveRemoteClientId()
    {
        if (MultiplayerLobbyManager.Instance != null &&
            MultiplayerLobbyManager.Instance.ConnectedOpponentClientId != ulong.MaxValue)
        {
            return MultiplayerLobbyManager.Instance.ConnectedOpponentClientId;
        }

        if (networkManager != null && networkManager.ConnectedClientsIds != null)
        {
            foreach (ulong id in networkManager.ConnectedClientsIds)
            {
                if (id != NetworkManager.ServerClientId) return id;
            }
        }
        return ulong.MaxValue;
    }

    private void OnReceiveDuelSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        if (MatchSessionConfig.LocalIsHost) return;

        MatchStateSnapshot snap = ReadJsonMessage<MatchStateSnapshot>(reader);
        if (snap == null) return;

        clientReceivedSnapshot = true;
        if (clientHandshakeRoutine != null)
        {
            StopCoroutine(clientHandshakeRoutine);
            clientHandshakeRoutine = null;
        }

        if (BattleManager.Instance != null)
        {
            BattleManager.Instance.ApplyNetworkSnapshot(snap);
        }
    }

    private void OnReceiveStatusFeedback(ulong senderClientId, FastBufferReader reader)
    {
        if (MatchSessionConfig.LocalIsHost) return;

        NetStatusFeedbackDto fb = ReadJsonMessage<NetStatusFeedbackDto>(reader);
        if (fb == null) return;

        if (BattleManager.Instance != null && !string.IsNullOrEmpty(fb.message))
        {
            BattleManager.Instance.SetStatus(fb.message);
            BattleManager.Instance.UpdateGameOverReasonIfVisible(fb.message);
        }
    }

    private void OnReceivePlayerLeft(ulong senderClientId, FastBufferReader reader)
    {
        NetLeaveMatchDto dto = ReadJsonMessage<NetLeaveMatchDto>(reader);
        string leaver = (dto != null && !string.IsNullOrWhiteSpace(dto.leaverName))
            ? dto.leaverName
            : MatchSessionConfig.GetOpponentDisplayName();

        if (MatchSessionConfig.LocalIsHost && !isMatchOver)
        {
            isMatchOver = true;
            matchWinnerSlot = 0;
            matchOverReason = $"{leaver} đã thoát phòng — Bạn được xử thắng!";
            authTurnState = BattleManager.TurnState.GameOver;
            ClearPendingPromptState();
        }

        if (BattleManager.Instance != null)
        {
            BattleManager.Instance.NotifyOpponentLeftMatch($"{leaver} đã thoát phòng — Bạn được xử thắng!");
        }
    }

    private void SendJsonMessage<T>(string messageName, ulong targetClientId, T payload)
    {
        if (networkManager == null || networkManager.CustomMessagingManager == null) return;

        string json = JsonUtility.ToJson(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        using (FastBufferWriter writer = new FastBufferWriter(sizeof(int) + bytes.Length, Allocator.Temp))
        {
            writer.WriteValueSafe(bytes.Length);
            if (bytes.Length > 0)
            {
                writer.WriteBytesSafe(bytes, bytes.Length);
            }

            networkManager.CustomMessagingManager.SendNamedMessage(
                messageName,
                targetClientId,
                writer,
                NetworkDelivery.ReliableFragmentedSequenced
            );
        }
    }

    private T ReadJsonMessage<T>(FastBufferReader reader) where T : class
    {
        try
        {
            reader.ReadValueSafe(out int length);
            if (length < 0 || length > 131072) return null;

            byte[] bytes = length > 0 ? new byte[length] : Array.Empty<byte>();
            if (length > 0)
            {
                reader.ReadBytesSafe(ref bytes, length);
            }

            string json = Encoding.UTF8.GetString(bytes);
            return JsonUtility.FromJson<T>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[NetworkDuel] Lỗi đọc gói tin {typeof(T).Name}: {ex.Message}");
            return null;
        }
    }
}
