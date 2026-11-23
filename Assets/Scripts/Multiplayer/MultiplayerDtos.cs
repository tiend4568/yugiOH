using System;
using System.Collections.Generic;

public enum NetDuelActionType
{
    None = 0,
    SummonMonster = 1,
    ChangeMonsterPosition = 2,
    EnterBattlePhase = 3,
    AttackMonster = 4,
    DirectAttack = 5,
    EndTurn = 6,

    // Bước 5: Bài Phép / Bẫy / Async Prompt
    ActivateSpellFromHand = 7,
    SetSpellTrapFromHand = 8,
    ActivateSetSpellTrap = 9,
    RespondPromptChoice = 10,

    // Bước 6: Đấu lại (Rematch) & Rời trận
    RequestRematch = 11,
    LeaveMatch = 12,

    // Hiệu ứng kích hoạt của quái thú trên sân (Obelisk hiến tế 2 quái / Ra trả LP)
    ActivateMonsterFieldEffect = 13
}

public enum NetPendingPromptType
{
    None = 0,
    FlipSelectGraveyardToDeck = 1,
    TrapMagicalHats = 2,
    TrapHoleOnSummon = 3,
    RaSummonPayLP = 4,
    EndPhaseDiscardHand = 5
}

/// <summary>
/// Các gói dữ liệu đồng bộ trạng thái bàn đấu (Snapshot), Hành động (Action Request) và Phản hồi (Feedback)
/// giữa Host và Client qua CustomMessagingManager (JSON UTF-8).
/// </summary>
[Serializable]
public class NetMonsterSlotDto
{
    public bool hasCard = false;
    public string cardName = "";
    public int position = 0; // BattlePosition
    public int atkModifier = 0;
    public int defModifier = 0;
    public bool summonedThisTurn = false;
    public bool isSpecialSummoned = false;
    public bool hasAttacked = false;
    public bool positionChanged = false;
    public bool faceUpEffectTriggered = false;
    public bool isMagicalHatDecoy = false;
}

[Serializable]
public class NetSpellSlotDto
{
    public bool hasCard = false;
    public string cardName = "";
    public bool isFaceDown = false;
    public bool canActivateInReaction = false;
    public string reactionHint = "";
}

[Serializable]
public class NetVfxAttackDto
{
    public bool hasAttack = false;
    public bool attackerIsMe = false;
    public int attackerZone = -1;
    public bool isDirectAttack = false;
    public bool targetIsMe = false;
    public int targetZone = -1;
}

[Serializable]
public class NetVfxDestroyDto
{
    public bool isMe = false;
    public int zoneIndex = -1;
    public bool isDefense = false;
    public string cardName = "";
}

[Serializable]
public class NetVfxGodSummonDto
{
    public bool hasGodSummon = false;
    public int godEffectType = 0; // MonsterEffectType (DivineObelisk / DivineSlifer / DivineRa)
    public bool isMe = false;
    public int zoneIndex = -1;
    public string cardName = "";
}

[Serializable]
public class MatchStateSnapshot
{
    public int snapshotSeq = 0;
    public int turnCount = 1;
    public bool isMyTurn = false;
    public int turnState = 0; // BattleManager.TurnState
    public string statusMessage = "";
    public string sfxToPlay = "";
    public List<string> sfxList = new List<string>();

    // Thông tin phía người xem (Local - Hiển thị ở nửa dưới bàn đấu)
    public string myName = "Player";
    public int myLP = 8000;
    public int myDeckCount = 0;
    public bool myNormalSummonUsed = false;
    public int mySwordsBlockTurns = 0;
    public List<string> myHandCardNames = new List<string>();
    public List<string> myGraveyardCardNames = new List<string>();
    public NetMonsterSlotDto[] myMonsters = new NetMonsterSlotDto[Player.ZoneCount];
    public NetSpellSlotDto[] mySpells = new NetSpellSlotDto[Player.ZoneCount];

    // Thông tin phía đối thủ (Opponent - Hiển thị ở nửa trên bàn đấu, đã ẩn bài trên tay & bài úp)
    public string opponentName = "Đối thủ";
    public int opponentLP = 8000;
    public int opponentDeckCount = 0;
    public int opponentHandCount = 0;
    public bool opponentNormalSummonUsed = false;
    public bool opponentHasMonsters = false;
    public int opponentSwordsBlockTurns = 0;
    public List<string> opponentGraveyardCardNames = new List<string>();
    public NetMonsterSlotDto[] opponentMonsters = new NetMonsterSlotDto[Player.ZoneCount];
    public NetSpellSlotDto[] opponentSpells = new NetSpellSlotDto[Player.ZoneCount];

    // Tương tác bất đồng bộ (Bước 5: FLIP chọn Mộ / Hỏi kích hoạt Bẫy Magical Hats)
    public int pendingPromptType = 0; // NetPendingPromptType
    public int pendingPromptId = 0;
    public bool isPromptForMe = false;
    public string promptTitle = "";

    // Trạng thái kết thúc trận & Đấu lại (Bước 4 & 6)
    public bool isGameOver = false;
    public bool didLocalWin = false;
    public string gameOverReason = "";
    public bool isRematchReset = false;

    // Hiệu ứng hình ảnh (VFX) đồng bộ cho đòn tấn công (Quả cầu lửa), phá hủy (Đa giác vàng) & triệu hồi 3 Thần Ai Cập
    public NetVfxAttackDto vfxAttack = new NetVfxAttackDto();
    public List<NetVfxDestroyDto> vfxDestroys = new List<NetVfxDestroyDto>();
    public NetVfxGodSummonDto vfxGodSummon = new NetVfxGodSummonDto();

    // Bộ đếm Anti-AFK (10s không thao tác -> đếm ngược 35s -> xử thua)
    public int afkCountdownSeconds = -1;
    public bool isAfkCountdownForMe = false;
}

[Serializable]
public class NetAfkTimerDto
{
    public int remainingSeconds = -1;
    public bool isForMe = false;
    public string targetPlayerName = "";
}

[Serializable]
public class NetLeaveMatchDto
{
    public string leaverName = "";
}

[Serializable]
public class NetDuelActionRequest
{
    public int actionType = 0; // NetDuelActionType
    public int handIndex = -1;
    public int position = 0;   // BattlePosition
    public int preferredZone = -1;
    public int myZoneIndex = -1;
    public int targetOpponentZone = -1;
    public int graveyardTargetIndex = -1;
    public int promptId = 0;
    public List<int> tributeZones = new List<int>();
}

[Serializable]
public class NetStatusFeedbackDto
{
    public string message = "";
    public string sfxId = "";
}

[Serializable]
public class NetClientReadyDto
{
    public string playerName = "";
}
