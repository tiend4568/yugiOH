using System;
using System.Collections.Generic;
using System.Linq;

public enum BattlePosition
{
    FaceUpAttack,
    FaceUpDefense,
    FaceDownDefense
}

public class MonsterCard
{
    public CardData data;
    public Player owner;
    public int zoneIndex;
    public BattlePosition position;

    public bool summonedThisTurn;
    public bool isSpecialSummoned;
    public bool hasAttacked;
    public bool positionChanged;
    public bool faceUpEffectTriggered;

    public int atkModifier;
    public int defModifier;

    // Dành cho hiệu ứng Mũ Tơ Ma Thuật (Magical Hats)
    public bool isMagicalHatDecoy;
    public CardData originalSpellTrapData;

    public int Atk
    {
        get
        {
            if (isMagicalHatDecoy) return 0;
            int baseAtk = (data != null && data.monsterEffect == MonsterEffectType.DivineSlifer && owner != null)
                ? owner.handCount * 1000
                : (data != null ? data.atk : 0);
            return Math.Max(0, baseAtk + atkModifier);
        }
    }

    public int Def
    {
        get
        {
            if (isMagicalHatDecoy) return 0;
            int baseDef = (data != null && data.monsterEffect == MonsterEffectType.DivineSlifer && owner != null)
                ? owner.handCount * 1000
                : (data != null ? data.def : 0);
            return Math.Max(0, baseDef + defModifier);
        }
    }

    public bool IsFaceDown => position == BattlePosition.FaceDownDefense;
    public bool IsDefense => position != BattlePosition.FaceUpAttack;
    public bool CannotBeTargetedByEffects =>
        !isMagicalHatDecoy && !IsFaceDown && data != null && data.CannotBeTargetedByEffects;

    public bool CanAttack =>
        position == BattlePosition.FaceUpAttack && !hasAttacked;

    public bool CanChangePosition =>
        !hasAttacked && !positionChanged && !summonedThisTurn;

    public void ResetTurnState()
    {
        summonedThisTurn = false;
        hasAttacked = false;
        positionChanged = false;
    }
}

public class Player
{
    public const int ZoneCount = 5;

    public string name;
    public int lifePoints = 8000;
    public int handCount = 0;
    public bool normalSummonUsed;
    public bool kuribohShieldActive;
    public MonsterCard[] monsterZones = new MonsterCard[ZoneCount];

    // Số lượt End Phase còn lại bị chặn tấn công bởi Lớp Kiếm Bảo Hộ Của Ánh Sáng
    public int swordsBlockTurnsRemaining = 0;

    public Player(string name) { this.name = name; }

    public IEnumerable<MonsterCard> Monsters => monsterZones.Where(m => m != null);
    public int MonsterCount => Monsters.Count();
    public int FirstEmptyZone() => Array.FindIndex(monsterZones, m => m == null);
}

public class Duel
{
    public Player[] players;
    public int currentIndex;
    public int turnCount = 1;

    public Player Current => players[currentIndex];
    public Player Opponent => players[1 - currentIndex];
    public bool IsGameOver => players[0].lifePoints <= 0 || players[1].lifePoints <= 0;

    public event Action<string> OnLog;
    public event Action<MonsterCard> OnSummoned;
    public event Action<MonsterCard, MonsterCard> OnAttackDeclared;
    public event Action<MonsterCard> OnDestroyed;
    public event Action<MonsterCard> OnPositionChanged;
    public event Action<Player, int> OnDamage;
    public event Action<Player> OnTurnStart;
    public event Action<CardData> OnSpellActivated;

    public event Action<Player, int> OnRequestDraw;
    public Func<Player, CardData> OnGetReviveTarget;
    public Action<Player, MonsterCard> OnTriggerFlipGraveyardToDeck;
    public Action<Player, CardData> OnReturnMonsterToHand;
    public Func<Player, CardData> OnSearchMonsterFromDeck;

    // Sự kiện hỗ trợ Mũ Tơ Ma Thuật & Lớp Kiếm Bảo Hộ Của Ánh Sáng
    public Func<Player, int, List<CardData>> OnRequestDeckSpellsTraps;
    public Action<Player, CardData> OnSendCardToGraveyard;
    public Action<Player> OnSwordsExpired;

    // Sự kiện hình ảnh (VFX) khi quái thú hoặc Mũ Tơ Ma Thuật trên sân bị phá hủy
    public event Action<MonsterCard> OnMonsterShattered;

    public Duel(Player p1, Player p2)
    {
        players = new[] { p1, p2 };
        StartTurn();
    }

    void Log(string msg) => OnLog?.Invoke(msg);

    void StartTurn()
    {
        Current.normalSummonUsed = false;
        foreach (var m in Current.Monsters) m.ResetTurnState();
        Log($"Lượt {turnCount}: {Current.name}");
        OnTurnStart?.Invoke(Current);
    }

    public void EndTurn()
    {
        if (IsGameOver) return;

        ResolveEndOfBattlePhase();

        // Hiệu ứng End Phase của Obelisk và Slifer: Nếu được Triệu hồi Đặc biệt -> Gửi xuống Mộ
        foreach (var pl in players)
        {
            foreach (var m in pl.Monsters.ToList())
            {
                if (m != null && !m.isMagicalHatDecoy && m.isSpecialSummoned &&
                    (m.data.monsterEffect == MonsterEffectType.DivineObelisk ||
                     m.data.monsterEffect == MonsterEffectType.DivineSlifer))
                {
                    Log($"End Phase: [{m.data.cardName}] được Triệu hồi Đặc biệt nên tự động trở về Mộ bài!");
                    RemoveFromField(m, destroyedByBattle: true);
                }
            }
        }

        // Đếm ngược Lớp Kiếm Bảo Hộ Của Ánh Sáng vào End Phase của người đang bị khóa
        if (Current.swordsBlockTurnsRemaining > 0)
        {
            Current.swordsBlockTurnsRemaining--;
            if (Current.swordsBlockTurnsRemaining <= 0)
            {
                Log($"End Phase: Lớp Kiếm Bảo Hộ Của Ánh Sáng khóa {Current.name} đã tự hủy!");
                OnSwordsExpired?.Invoke(Current);
            }
            else
            {
                Log($"Lớp Kiếm Bảo Hộ Của Ánh Sáng còn hiệu lực thêm {Current.swordsBlockTurnsRemaining} lượt của {Current.name}.");
            }
        }

        currentIndex = 1 - currentIndex;
        turnCount++;
        StartTurn();
    }

    public MonsterCard Summon(Player p, CardData data, BattlePosition pos,
                               List<MonsterCard> tributes = null, bool isSpecial = false, int preferredZone = -1)
    {
        if (IsGameOver || data == null || data.cardType != CardType.Monster) return null;
        tributes = tributes ?? new List<MonsterCard>();

        if (isSpecial && data.CannotBeSpecialSummoned)
        {
            Log($"[{data.cardName}] không thể được Triệu hồi Đặc biệt!");
            return null;
        }

        if (!isSpecial)
        {
            if (p != Current) { Log("Không phải lượt của bạn."); return null; }
            if (p.normalSummonUsed) { Log("Đã triệu hồi thường trong lượt này."); return null; }
            if (pos == BattlePosition.FaceUpDefense)
            { Log("Triệu hồi thường chỉ được ngửa-tấn-công hoặc úp-phòng-thủ (Set)."); return null; }

            if (data.IsDivineGod && pos != BattlePosition.FaceUpAttack)
            {
                Log($"[{data.cardName}] chỉ được Triệu hồi Thường ở Tư thế Tấn công ngửa (không thể Triệu hồi Úp)!");
                return null;
            }

            int need = RequiredTributes(data);
            if (tributes.Count != need) { Log($"Cần hiến tế {need} quái thú."); return null; }
            if (tributes.Any(t => t == null || t.owner != p)) { Log("Chỉ được hiến tế quái của mình."); return null; }
        }

        int projectedCount = p.MonsterCount - tributes.Count;
        if (projectedCount >= Player.ZoneCount) { Log("Hết ô quái trên sân."); return null; }

        foreach (var t in tributes) RemoveFromField(t, destroyedByBattle: false);

        int zone = (preferredZone >= 0 && preferredZone < Player.ZoneCount && p.monsterZones[preferredZone] == null)
            ? preferredZone
            : p.FirstEmptyZone();

        if (zone < 0) { Log("Hết ô quái trên sân."); return null; }

        var card = new MonsterCard
        {
            data = data,
            owner = p,
            zoneIndex = zone,
            position = pos,
            summonedThisTurn = true,
            isSpecialSummoned = isSpecial
        };
        p.monsterZones[zone] = card;
        if (!isSpecial) p.normalSummonUsed = true;

        Log($"{p.name} triệu hồi {(card.IsFaceDown ? "[Úp]" : $"{data.cardName} (ATK {card.Atk}/DEF {card.Def})")}");
        OnSummoned?.Invoke(card);

        if (!card.IsFaceDown)
        {
            ApplySliferThunderforceOnSummon(card);
            if (p.monsterZones[zone] == card && !IsGameOver)
            {
                TriggerMonsterEffect(card, wasFlippedFromFaceDown: false);
            }
        }

        return card;
    }

    public void ApplySliferThunderforceOnSummon(MonsterCard summonedCard)
    {
        if (summonedCard == null || summonedCard.IsFaceDown || summonedCard.isMagicalHatDecoy)
            return;
        if (summonedCard.position != BattlePosition.FaceUpAttack)
            return;

        Player summonedOwner = summonedCard.owner;
        Player enemy = (summonedOwner == players[0]) ? players[1] : players[0];

        var enemySlifers = enemy.Monsters
            .Where(m => m != null && !m.IsFaceDown && !m.isMagicalHatDecoy && m.data.monsterEffect == MonsterEffectType.DivineSlifer)
            .ToList();

        if (enemySlifers.Count == 0) return;

        foreach (var slifer in enemySlifers)
        {
            if (summonedOwner.monsterZones[summonedCard.zoneIndex] != summonedCard) break;

            summonedCard.atkModifier -= 2000;
            Log($"[Đạn Sấm Sét - {slifer.data.cardName}] Giảm 2000 ATK của [{summonedCard.data.cardName}] vừa triệu hồi (ATK còn {summonedCard.Atk})!");

            if (summonedCard.Atk <= 0)
            {
                Log($"[Đạn Sấm Sét - {slifer.data.cardName}] ATK của [{summonedCard.data.cardName}] giảm về 0 -> Bị phá hủy ngay lập tức!");
                RemoveFromField(summonedCard, destroyedByBattle: true);
                break;
            }
        }
    }

    void TriggerMonsterEffect(MonsterCard card, bool wasFlippedFromFaceDown)
    {
        if (card == null || card.isMagicalHatDecoy || card.faceUpEffectTriggered || card.data.monsterEffect == MonsterEffectType.None)
            return;

        Player owner = card.owner;
        Player enemy = (owner == players[0]) ? players[1] : players[0];

        switch (card.data.monsterEffect)
        {
            case MonsterEffectType.FlipReturnGraveyardToDeck:
                if (!wasFlippedFromFaceDown) return;
                card.faceUpEffectTriggered = true;
                OnTriggerFlipGraveyardToDeck?.Invoke(owner, card);
                break;

            case MonsterEffectType.FlipDestroyEnemyMonster:
                if (!wasFlippedFromFaceDown) return;
                card.faceUpEffectTriggered = true;
                MonsterCard flipTarget = enemy.Monsters
                    .Where(m => !m.CannotBeTargetedByEffects)
                    .OrderByDescending(m => m.Atk)
                    .FirstOrDefault();
                if (flipTarget != null)
                {
                    Log($"[FLIP - {card.data.cardName}] Tiêu diệt {flipTarget.data.cardName}!");
                    RemoveFromField(flipTarget, destroyedByBattle: true);
                }
                break;

            case MonsterEffectType.FlipReturnEnemyMonsterToHand:
                if (!wasFlippedFromFaceDown) return;
                card.faceUpEffectTriggered = true;
                MonsterCard bounceTarget = enemy.Monsters
                    .Where(m => !m.CannotBeTargetedByEffects)
                    .OrderByDescending(m => m.Atk)
                    .FirstOrDefault();
                if (bounceTarget != null)
                {
                    RemoveFromField(bounceTarget, destroyedByBattle: false);
                    if (!bounceTarget.isMagicalHatDecoy)
                    {
                        Log($"[FLIP - {card.data.cardName}] Đẩy {bounceTarget.data.cardName} về lại tay!");
                        OnReturnMonsterToHand?.Invoke(enemy, bounceTarget.data);
                    }
                    else if (bounceTarget.originalSpellTrapData != null)
                    {
                        OnSendCardToGraveyard?.Invoke(enemy, bounceTarget.originalSpellTrapData);
                    }
                }
                break;

            case MonsterEffectType.OnSummonDestroyEnemyMonster:
                card.faceUpEffectTriggered = true;
                MonsterCard summonTarget = enemy.Monsters
                    .Where(m => !m.CannotBeTargetedByEffects)
                    .OrderByDescending(m => m.Atk)
                    .FirstOrDefault();
                if (summonTarget != null)
                {
                    Log($"[Hiệu ứng {card.data.cardName}] Tiêu diệt {summonTarget.data.cardName}!");
                    RemoveFromField(summonTarget, destroyedByBattle: true);
                }
                break;

            case MonsterEffectType.OnSummonDrawCard:
                card.faceUpEffectTriggered = true;
                int drawCnt = card.data.effectValue > 0 ? card.data.effectValue : 1;
                Log($"[Hiệu ứng {card.data.cardName}] Rút thêm {drawCnt} lá bài!");
                OnRequestDraw?.Invoke(owner, drawCnt);
                break;

            case MonsterEffectType.OnSummonBurnLP:
                card.faceUpEffectTriggered = true;
                int burn = card.data.effectValue > 0 ? card.data.effectValue : 500;
                Log($"[Hiệu ứng {card.data.cardName}] Gây {burn} sát thương thẳng vào LP!");
                DealDamage(enemy, burn);
                break;

            case MonsterEffectType.OnSummonHealLP:
                card.faceUpEffectTriggered = true;
                int heal = card.data.effectValue > 0 ? card.data.effectValue : 600;
                owner.lifePoints += heal;
                Log($"[Hiệu ứng {card.data.cardName}] Hồi phục {heal} LP!");
                OnDamage?.Invoke(owner, 0);
                break;
        }
    }

    public static bool CanActivateSpellOrTrap(
        CardData spell,
        Player caster,
        Player enemy,
        int casterDeckCount,
        bool casterDeckHasSpellOrTrap,
        bool casterGraveyardHasMonster,
        out string failReason)
    {
        failReason = "";
        if (spell == null || !spell.IsSpellOrTrap)
        {
            failReason = "Không phải bài Phép/Bẫy.";
            return false;
        }
        if (caster == null || enemy == null)
        {
            failReason = "Dữ liệu người chơi chưa sẵn sàng.";
            return false;
        }

        switch (spell.spellEffect)
        {
            case SpellEffectType.DestroyAllEnemyMonsters:
                if (enemy.MonsterCount == 0)
                {
                    failReason = "Đối phương chưa có quái thú trên sân.";
                    return false;
                }
                return true;

            case SpellEffectType.DestroyAllMonsters:
                if (caster.MonsterCount + enemy.MonsterCount == 0)
                {
                    failReason = "Trên sân chưa có quái thú nào.";
                    return false;
                }
                return true;

            case SpellEffectType.DrawCards:
                if (casterDeckCount <= 0)
                {
                    failReason = "Bộ bài đã hết lá để rút.";
                    return false;
                }
                return true;

            case SpellEffectType.BuffAllMyMonstersATK:
                if (caster.MonsterCount == 0)
                {
                    failReason = "Bạn chưa có quái thú trên sân để tăng ATK.";
                    return false;
                }
                return true;

            case SpellEffectType.DebuffAllEnemyMonstersATK:
                if (enemy.MonsterCount == 0)
                {
                    failReason = "Đối phương chưa có quái thú trên sân.";
                    return false;
                }
                return true;

            case SpellEffectType.ReviveFromGraveyard:
                if (caster.FirstEmptyZone() < 0)
                {
                    failReason = "Sân quái thú của bạn đã đầy chỗ.";
                    return false;
                }
                if (!casterGraveyardHasMonster)
                {
                    failReason = "Trong Mộ bài chưa có quái thú để hồi sinh.";
                    return false;
                }
                return true;

            case SpellEffectType.SwordsOfRevealingLight:
                if (enemy.swordsBlockTurnsRemaining > 0)
                {
                    failReason = $"Đang còn hiệu lực khóa {enemy.swordsBlockTurnsRemaining} lượt trên sân.";
                    return false;
                }
                return true;

            case SpellEffectType.MagicalHats:
                if (caster.MonsterCount == 0)
                {
                    failReason = "Cần ít nhất 1 quái thú của bạn trên sân.";
                    return false;
                }
                if (caster.MonsterCount >= Player.ZoneCount)
                {
                    failReason = "Không còn ô quái thú trống trên sân.";
                    return false;
                }
                if (!casterDeckHasSpellOrTrap)
                {
                    failReason = "Trong Deck không còn lá Phép/Bẫy nào.";
                    return false;
                }
                return true;

            case SpellEffectType.TrapHole:
                failReason = "Hang Rơi chỉ kích hoạt khi đối thủ vừa Triệu hồi Thường / Lật quái thú có ATK >= 1000.";
                return false;

            default:
                return true;
        }
    }

    public static bool CanActivateTrapHoleOnSummon(CardData trapCard, MonsterCard summonedMonster, out string failReason)
    {
        failReason = "";
        if (trapCard == null || trapCard.spellEffect != SpellEffectType.TrapHole)
        {
            failReason = "Không phải lá bài Hang Rơi.";
            return false;
        }
        if (summonedMonster == null || summonedMonster.IsFaceDown || summonedMonster.isMagicalHatDecoy)
        {
            failReason = "Quái thú triệu hồi không nằm ngửa trên sân.";
            return false;
        }
        if (summonedMonster.owner == null || summonedMonster.owner.monsterZones[summonedMonster.zoneIndex] != summonedMonster)
        {
            failReason = "Quái thú triệu hồi không còn trên sân.";
            return false;
        }
        if (summonedMonster.data != null && summonedMonster.data.IsDivineGod)
        {
            failReason = $"Không thể kích hoạt bài/hiệu ứng khi [{summonedMonster.data.cardName}] được Triệu hồi Thường!";
            return false;
        }
        if (summonedMonster.CannotBeTargetedByEffects)
        {
            failReason = $"[{summonedMonster.data.cardName}] không thể bị chọn làm mục tiêu của hiệu ứng!";
            return false;
        }
        int minAtk = trapCard.effectValue > 0 ? trapCard.effectValue : 1000;
        if (summonedMonster.Atk < minAtk)
        {
            failReason = $"ATK của [{summonedMonster.data.cardName}] ({summonedMonster.Atk}) thấp hơn {minAtk}.";
            return false;
        }
        return true;
    }

    public bool ActivateTrapHoleOnSummon(Player trapOwner, CardData trapCard, MonsterCard summonedTarget)
    {
        if (IsGameOver || trapOwner == null || trapCard == null || summonedTarget == null) return false;
        if (!CanActivateTrapHoleOnSummon(trapCard, summonedTarget, out string reason))
        {
            Log(reason);
            return false;
        }

        OnSpellActivated?.Invoke(trapCard);
        Log($"{trapOwner.name} kích hoạt Bài Bẫy [{trapCard.cardName}]: Phá hủy [{summonedTarget.data.cardName}] (ATK {summonedTarget.Atk}) vừa triệu hồi!");
        RemoveFromField(summonedTarget, destroyedByBattle: true);
        return true;
    }

    public bool ActivateObeliskSoulEnergyMax(Player p, MonsterCard obelisk, List<MonsterCard> tributes)
    {
        if (IsGameOver || p == null || obelisk == null) return false;
        if (p != Current) { Log("Không phải lượt của bạn."); return false; }
        if (obelisk.owner != p || obelisk.IsFaceDown || obelisk.data == null || obelisk.data.monsterEffect != MonsterEffectType.DivineObelisk)
        {
            Log("Không phải Thần Lính Khổng Lồ Obelisk đang nằm ngửa của bạn.");
            return false;
        }
        if (obelisk.hasAttacked)
        {
            Log($"[{obelisk.data.cardName}] đã tấn công hoặc đã dùng hiệu ứng trong lượt này, không thể kích hoạt!");
            return false;
        }

        Player enemy = (p == players[0]) ? players[1] : players[0];
        if (enemy.MonsterCount == 0)
        {
            Log("Đối phương chưa có quái thú nào trên sân để phá hủy!");
            return false;
        }

        if (tributes == null || tributes.Count != 2 || tributes.Distinct().Count() != 2 ||
            tributes.Any(t => t == null || t.owner != p || p.monsterZones[t.zoneIndex] != t))
        {
            Log($"Cần chọn đúng 2 quái thú trên sân của bạn để hiến tế cho [{obelisk.data.cardName}]!");
            return false;
        }

        foreach (var t in tributes)
        {
            bool wasDecoy = t.isMagicalHatDecoy;
            CardData tData = t.data;
            RemoveFromField(t, destroyedByBattle: false);
            if (!wasDecoy && tData != null)
            {
                OnSendCardToGraveyard?.Invoke(p, tData);
            }
        }

        foreach (var em in enemy.Monsters.ToList())
        {
            RemoveFromField(em, destroyedByBattle: true);
        }

        if (p.monsterZones[obelisk.zoneIndex] == obelisk)
        {
            obelisk.hasAttacked = true;
            obelisk.faceUpEffectTriggered = true;
        }

        Log($"[{obelisk.data.cardName}] Hiến tế 2 quái thú -> Phá hủy toàn bộ quái thú của {enemy.name}! (Obelisk không thể tấn công lượt này)");
        return true;
    }

    public bool ActivateRaPointToPointTransfer(Player p, MonsterCard ra)
    {
        if (IsGameOver || p == null || ra == null) return false;
        if (ra.owner != p || ra.IsFaceDown || ra.data == null || ra.data.monsterEffect != MonsterEffectType.DivineRa)
            return false;
        if (p.lifePoints <= 100)
        {
            Log("LP hiện tại không đủ để trả xuống còn 100 LP!");
            return false;
        }

        int paidLP = p.lifePoints - 100;
        p.lifePoints = 100;
        ra.atkModifier += paidLP;
        ra.defModifier += paidLP;
        ra.faceUpEffectTriggered = true;

        Log($"[{ra.data.cardName}] {p.name} trả {paidLP} LP (chỉ còn 100 LP) -> Nhận +{paidLP} ATK / +{paidLP} DEF!");
        OnDamage?.Invoke(p, paidLP);
        return true;
    }

    public bool ActivateRaPhoenixDestroy(Player p, MonsterCard ra, MonsterCard target)
    {
        if (IsGameOver || p == null || ra == null || target == null) return false;
        if (p != Current) { Log("Không phải lượt của bạn."); return false; }
        if (ra.owner != p || ra.IsFaceDown || ra.data == null || ra.data.monsterEffect != MonsterEffectType.DivineRa)
        {
            Log("Không phải Rồng Cánh Thần Của Ra đang nằm ngửa của bạn.");
            return false;
        }
        if (p.lifePoints <= 1000)
        {
            Log($"Cần có trên 1000 LP để kích hoạt hiệu ứng trả 1000 LP của [{ra.data.cardName}]!");
            return false;
        }
        if (target.owner == null || target.owner.monsterZones[target.zoneIndex] != target)
        {
            Log("Mục tiêu không còn trên sân!");
            return false;
        }
        if (target.CannotBeTargetedByEffects)
        {
            Log($"Không thể chọn [{target.data.cardName}] làm mục tiêu của hiệu ứng bài!");
            return false;
        }

        p.lifePoints -= 1000;
        OnDamage?.Invoke(p, 1000);

        string targetName = target.IsFaceDown ? "quái thú úp" : target.data.cardName;
        Log($"[{ra.data.cardName}] {p.name} trả 1000 LP (còn {p.lifePoints} LP) -> Phá hủy [{targetName}]!");
        RemoveFromField(target, destroyedByBattle: true);
        return true;
    }

    public bool ActivateSpell(Player p, CardData spell, MonsterCard protectedMonster = null, bool allowOutOfTurnReaction = false)
    {
        if (IsGameOver || spell == null || !spell.IsSpellOrTrap) return false;
        if (spell.cardType == CardType.Spell && p != Current && !allowOutOfTurnReaction)
        {
            Log("Không phải lượt của bạn.");
            return false;
        }

        Player enemy = (p == players[0]) ? players[1] : players[0];

        switch (spell.spellEffect)
        {
            case SpellEffectType.DestroyAllEnemyMonsters:
                if (enemy.MonsterCount == 0) { Log("Đối phương không có quái trên sân!"); return false; }
                Log($"{p.name} kích hoạt [{spell.cardName}]: Quét sạch quái đối phương!");
                OnSpellActivated?.Invoke(spell);
                foreach (var m in enemy.Monsters.ToList()) RemoveFromField(m, destroyedByBattle: true);
                return true;

            case SpellEffectType.DestroyAllMonsters:
                if (p.MonsterCount + enemy.MonsterCount == 0) { Log("Trên sân không có quái thú nào!"); return false; }
                Log($"{p.name} kích hoạt [{spell.cardName}]: Hố Đen nuốt chửng toàn bộ sân!");
                OnSpellActivated?.Invoke(spell);
                foreach (var m in p.Monsters.ToList()) RemoveFromField(m, destroyedByBattle: true);
                foreach (var m in enemy.Monsters.ToList()) RemoveFromField(m, destroyedByBattle: true);
                return true;

            case SpellEffectType.DrawCards:
                int drawCount = spell.effectValue > 0 ? spell.effectValue : 2;
                Log($"{p.name} kích hoạt [{spell.cardName}]: Rút thêm {drawCount} lá bài!");
                OnSpellActivated?.Invoke(spell);
                OnRequestDraw?.Invoke(p, drawCount);
                return true;

            case SpellEffectType.HealLP:
                int heal = spell.effectValue > 0 ? spell.effectValue : 1000;
                p.lifePoints += heal;
                Log($"{p.name} kích hoạt [{spell.cardName}]: Hồi {heal} LP (hiện tại {p.lifePoints})!");
                OnSpellActivated?.Invoke(spell);
                OnDamage?.Invoke(p, 0);
                return true;

            case SpellEffectType.DamageEnemyLP:
                int dmg = spell.effectValue > 0 ? spell.effectValue : 800;
                Log($"{p.name} kích hoạt [{spell.cardName}]: Bắn {dmg} sát thương thẳng vào LP!");
                OnSpellActivated?.Invoke(spell);
                DealDamage(enemy, dmg);
                return true;

            case SpellEffectType.BuffAllMyMonstersATK:
                if (p.MonsterCount == 0) { Log("Bạn chưa có quái thú nào trên sân!"); return false; }
                int buff = spell.effectValue > 0 ? spell.effectValue : 500;
                foreach (var m in p.Monsters) m.atkModifier += buff;
                Log($"{p.name} kích hoạt [{spell.cardName}]: Toàn bộ quái phe mình tăng +{buff} ATK!");
                OnSpellActivated?.Invoke(spell);
                return true;

            case SpellEffectType.DebuffAllEnemyMonstersATK:
                if (enemy.MonsterCount == 0) { Log("Đối phương không có quái thú nào trên sân!"); return false; }
                int debuff = spell.effectValue > 0 ? spell.effectValue : 500;
                foreach (var m in enemy.Monsters) m.atkModifier -= debuff;
                Log($"{p.name} kích hoạt [{spell.cardName}]: Toàn bộ quái đối phương bị giảm -{debuff} ATK!");
                OnSpellActivated?.Invoke(spell);
                return true;

            case SpellEffectType.ReviveFromGraveyard:
                if (p.FirstEmptyZone() < 0) { Log("Sân của bạn đã đầy chỗ!"); return false; }
                CardData reviveTarget = OnGetReviveTarget?.Invoke(p);
                if (reviveTarget == null) { Log("Không có quái thú nào trong Mộ bài để hồi sinh!"); return false; }
                Log($"{p.name} kích hoạt [{spell.cardName}]: Hồi sinh {reviveTarget.cardName} từ Mộ bài!");
                OnSpellActivated?.Invoke(spell);
                Summon(p, reviveTarget, BattlePosition.FaceUpAttack, null, isSpecial: true);
                return true;

            case SpellEffectType.SwordsOfRevealingLight:
                {
                    if (enemy.swordsBlockTurnsRemaining > 0)
                    {
                        Log($"[{spell.cardName}] đang còn hiệu lực khóa {enemy.name} (còn {enemy.swordsBlockTurnsRemaining} lượt), không thể kích hoạt chồng thêm!");
                        return false;
                    }

                    OnSpellActivated?.Invoke(spell);

                    var faceDownEnemies = enemy.Monsters.Where(m => m.IsFaceDown).ToList();
                    foreach (var m in faceDownEnemies)
                    {
                        m.position = BattlePosition.FaceUpDefense;
                        OnPositionChanged?.Invoke(m);
                        TriggerMonsterEffect(m, wasFlippedFromFaceDown: true);
                    }

                    int blockTurns = spell.effectValue > 0 ? spell.effectValue : 3;
                    enemy.swordsBlockTurnsRemaining = blockTurns;

                    string flipInfo = faceDownEnemies.Count > 0 ? $" Lật ngửa {faceDownEnemies.Count} quái úp của {enemy.name}!" : "";
                    Log($"{p.name} kích hoạt [{spell.cardName}]!{flipInfo} Quái thú của {enemy.name} không thể tấn công trong {blockTurns} lượt!");
                    return true;
                }

            case SpellEffectType.MagicalHats:
                return ActivateMagicalHats(p, spell, protectedMonster);

            default:
                Log($"{p.name} kích hoạt [{spell.cardName}]!");
                OnSpellActivated?.Invoke(spell);
                return true;
        }
    }

    bool ActivateMagicalHats(Player p, CardData trapCard, MonsterCard chosenMonster)
    {
        if (p.MonsterCount == 0)
        {
            Log("Cần có ít nhất 1 quái thú trên sân để kích hoạt Mũ Tơ Ma Thuật!");
            return false;
        }

        int emptyZones = Player.ZoneCount - p.MonsterCount;
        if (emptyZones <= 0)
        {
            Log("Không còn ô quái thú trống trên sân!");
            return false;
        }

        if (chosenMonster == null || chosenMonster.owner != p)
        {
            chosenMonster = p.Monsters.OrderByDescending(m => m.Atk).FirstOrDefault();
        }
        if (chosenMonster == null) return false;

        int maxHats = Math.Min(2, emptyZones);
        List<CardData> deckSpellsTraps = OnRequestDeckSpellsTraps?.Invoke(p, maxHats) ?? new List<CardData>();
        if (deckSpellsTraps.Count == 0)
        {
            Log("Trong Deck không còn lá Bài Phép / Bẫy nào cho Mũ Tơ Ma Thuật!");
            return false;
        }

        OnSpellActivated?.Invoke(trapCard);

        // Úp quái thú đã chọn nếu đang nằm ngửa
        chosenMonster.position = BattlePosition.FaceDownDefense;
        chosenMonster.faceUpEffectTriggered = false;
        OnPositionChanged?.Invoke(chosenMonster);

        List<MonsterCard> hatGroup = new List<MonsterCard> { chosenMonster };

        // Triệu hồi Đặc biệt tối đa 2 lá Phép/Bẫy từ Deck dưới dạng quái Thường (ATK 0 / DEF 0) úp
        foreach (var stCard in deckSpellsTraps)
        {
            int zone = p.FirstEmptyZone();
            if (zone < 0) break;

            CardData dummyData = new CardData("Mũ Tơ Ma Thuật", trapCard.cardSprite, 0, 0, 1,
                "Quái thú Thường (ATK 0 / DEF 0) tạo ra từ Mũ Tơ Ma Thuật. Sẽ bị phá hủy vào cuối Battle Phase.")
            {
                cardType = CardType.Monster,
                monsterEffect = MonsterEffectType.None
            };

            var decoy = new MonsterCard
            {
                data = dummyData,
                owner = p,
                zoneIndex = zone,
                position = BattlePosition.FaceDownDefense,
                summonedThisTurn = true,
                isMagicalHatDecoy = true,
                originalSpellTrapData = stCard
            };

            p.monsterZones[zone] = decoy;
            hatGroup.Add(decoy);
            OnSummoned?.Invoke(decoy);
        }

        // Xáo trộn vị trí các lá trong nhóm Mũ Tơ Ma Thuật trên sân
        List<int> occupiedIndices = hatGroup.Select(m => m.zoneIndex).ToList();
        var rng = new System.Random();
        for (int i = hatGroup.Count - 1; i > 0; i--)
        {
            int rnd = rng.Next(0, i + 1);
            MonsterCard tmp = hatGroup[i];
            hatGroup[i] = hatGroup[rnd];
            hatGroup[rnd] = tmp;
        }

        for (int i = 0; i < occupiedIndices.Count; i++)
        {
            int z = occupiedIndices[i];
            p.monsterZones[z] = hatGroup[i];
            hatGroup[i].zoneIndex = z;
        }

        Log($"{p.name} kích hoạt [{trapCard.cardName}]! Giấu [{chosenMonster.data.cardName}] cùng {deckSpellsTraps.Count} lá Phép/Bẫy thành Mũ Tơ (0/0) và xáo trộn vị trí!");
        return true;
    }

    public bool ChangePosition(Player p, MonsterCard card)
    {
        if (IsGameOver || card == null) return false;
        if (p != Current || card.owner != p) { Log("Không thể đổi thế bài đối phương hoặc ngoài lượt."); return false; }
        if (!card.CanChangePosition) { Log($"{card.data.cardName} không thể đổi thế lúc này."); return false; }

        bool wasFaceDown = card.IsFaceDown;

        if (card.position == BattlePosition.FaceUpAttack)
            card.position = BattlePosition.FaceUpDefense;
        else
            card.position = BattlePosition.FaceUpAttack;

        card.positionChanged = true;
        string displayName = card.isMagicalHatDecoy ? "Quái Thú Ảo Ảnh" : card.data.cardName;
        Log($"{p.name} chuyển {displayName} sang thế {(card.IsDefense ? "Phòng thủ" : "Tấn công")}");
        OnPositionChanged?.Invoke(card);

        if (wasFaceDown)
        {
            TriggerMonsterEffect(card, wasFlippedFromFaceDown: true);
        }

        return true;
    }

    public bool Attack(Player p, MonsterCard attacker, MonsterCard target)
    {
        if (IsGameOver || attacker == null || target == null) return false;
        if (turnCount == 1) { Log("Không được tấn công ở lượt đầu tiên của trận đấu."); return false; }
        if (p.swordsBlockTurnsRemaining > 0)
        {
            Log($"Lớp Kiếm Bảo Hộ Của Ánh Sáng đang chặn {p.name} tấn công (còn {p.swordsBlockTurnsRemaining} lượt)!");
            return false;
        }
        if (p != Current || attacker.owner != p) { Log("Không phải lượt của bạn."); return false; }
        if (!attacker.CanAttack) { Log($"{attacker.data.cardName} không thể tấn công."); return false; }
        if (target.owner != Opponent) { Log("Mục tiêu tấn công không hợp lệ."); return false; }

        attacker.hasAttacked = true;
        OnAttackDeclared?.Invoke(attacker, target);

        if (attacker.data.monsterEffect == MonsterEffectType.BoostATKOnAttack)
        {
            int boost = attacker.data.effectValue > 0 ? attacker.data.effectValue : 300;
            attacker.atkModifier += boost;
            Log($"[Hiệu ứng {attacker.data.cardName}] Tăng +{boost} ATK!");
        }

        string targetDisplayName = target.isMagicalHatDecoy
            ? (target.originalSpellTrapData != null ? $"Mũ Tơ ({target.originalSpellTrapData.cardName})" : "Mũ Tơ Ma Thuật")
            : target.data.cardName;

        if (target.IsFaceDown)
        {
            target.position = BattlePosition.FaceUpDefense;
            Log($"Quái thú úp lật ngửa: {targetDisplayName} (DEF {target.Def})!");
            OnPositionChanged?.Invoke(target);
            TriggerMonsterEffect(target, wasFlippedFromFaceDown: true);

            if (attacker.owner.monsterZones[attacker.zoneIndex] != attacker || IsGameOver)
                return true;
        }

        if (target.position == BattlePosition.FaceUpAttack)
        {
            int diff = attacker.Atk - target.Atk;
            if (diff > 0)
            {
                TryDestroyInBattle(target);
                DealBattleDamageToDefender(Opponent, diff);
            }
            else if (diff < 0)
            {
                TryDestroyInBattle(attacker);
                DealDamage(Current, -diff);
            }
            else
            {
                Log("Hai quái thú cùng sức tấn công!");
                TryDestroyInBattle(attacker);
                TryDestroyInBattle(target);
            }
        }
        else
        {
            int diff = attacker.Atk - target.Def;
            if (diff > 0)
            {
                TryDestroyInBattle(target);
                if (attacker.data.monsterEffect == MonsterEffectType.PiercingDamage)
                {
                    Log($"[Xuyên thủ] {attacker.data.cardName} gây {diff} sát thương xuyên qua phòng thủ!");
                    DealBattleDamageToDefender(Opponent, diff);
                }
            }
            else if (diff < 0)
            {
                Log($"{attacker.data.cardName} không xuyên thủng được phòng thủ của {targetDisplayName}!");
                DealDamage(Current, -diff);
            }
            else
            {
                Log("Tấn công hòa phòng thủ, không quái thú nào bị tiêu diệt.");
            }
        }

        Opponent.kuribohShieldActive = false;
        return true;
    }

    void DealBattleDamageToDefender(Player defender, int amount)
    {
        if (amount <= 0) return;
        if (defender != null && defender.kuribohShieldActive)
        {
            defender.kuribohShieldActive = false;
            Log($"[Kuriboh] Đã chặn hoàn toàn {amount} sát thương chiến đấu cho {defender.name} (0 sát thương)!");
            return;
        }
        DealDamage(defender, amount);
    }

    void TryDestroyInBattle(MonsterCard card)
    {
        if (card == null) return;
        if (!card.isMagicalHatDecoy && card.data.monsterEffect == MonsterEffectType.BattleIndestructible)
        {
            Log($"[Hiệu ứng {card.data.cardName}] Không thể bị phá hủy bởi chiến đấu!");
            return;
        }
        RemoveFromField(card, destroyedByBattle: true);
    }

    public bool DirectAttack(Player p, MonsterCard attacker)
    {
        if (IsGameOver || attacker == null) return false;
        if (turnCount == 1) { Log("Không được tấn công ở lượt đầu tiên của trận đấu."); return false; }
        if (p.swordsBlockTurnsRemaining > 0)
        {
            Log($"Lớp Kiếm Bảo Hộ Của Ánh Sáng đang chặn {p.name} tấn công (còn {p.swordsBlockTurnsRemaining} lượt)!");
            return false;
        }
        if (p != Current || attacker.owner != p) { Log("Không phải lượt của bạn."); return false; }
        if (!attacker.CanAttack) { Log($"{attacker.data.cardName} không thể tấn công."); return false; }
        if (Opponent.MonsterCount > 0) { Log("Đối phương còn quái trên sân, không thể đánh trực tiếp!"); return false; }

        attacker.hasAttacked = true;
        Log($"{attacker.data.cardName} tấn công trực tiếp {Opponent.name}!");
        OnAttackDeclared?.Invoke(attacker, null);

        if (attacker.data.monsterEffect == MonsterEffectType.BoostATKOnAttack)
        {
            int boost = attacker.data.effectValue > 0 ? attacker.data.effectValue : 300;
            attacker.atkModifier += boost;
            Log($"[Hiệu ứng {attacker.data.cardName}] Tăng +{boost} ATK!");
        }

        DealBattleDamageToDefender(Opponent, attacker.Atk);
        Opponent.kuribohShieldActive = false;
        return true;
    }

    public static int RequiredTributes(int level) => level >= 10 ? 3 : level >= 7 ? 2 : level >= 5 ? 1 : 0;

    public static int RequiredTributes(CardData data)
    {
        if (data == null) return 0;
        if (data.IsDivineGod) return 3;
        return RequiredTributes(data.level);
    }

    public void DealDamage(Player p, int amount)
    {
        if (amount <= 0) return;
        p.lifePoints = Math.Max(0, p.lifePoints - amount);
        Log($"{p.name} mất {amount} LP (còn {p.lifePoints})");
        OnDamage?.Invoke(p, amount);
        if (p.lifePoints == 0) Log($"{p.name} đã hết điểm gốc (LP = 0)!");
    }

    public void ResolveEndOfBattlePhase()
    {
        foreach (var p in players)
        {
            int removedHats = 0;
            for (int i = 0; i < Player.ZoneCount; i++)
            {
                MonsterCard m = p.monsterZones[i];
                if (m != null && m.isMagicalHatDecoy)
                {
                    p.monsterZones[i] = null;
                    OnMonsterShattered?.Invoke(m);
                    if (m.originalSpellTrapData != null)
                    {
                        OnSendCardToGraveyard?.Invoke(p, m.originalSpellTrapData);
                    }
                    removedHats++;
                }
            }
            if (removedHats > 0)
            {
                Log($"Cuối Battle Phase: {removedHats} lá Phép/Bẫy ảo ảnh từ Mũ Tơ Ma Thuật của {p.name} bị phá hủy!");
            }
        }
    }

    void RemoveFromField(MonsterCard card, bool destroyedByBattle)
    {
        if (card == null || card.owner.monsterZones[card.zoneIndex] != card) return;
        card.owner.monsterZones[card.zoneIndex] = null;

        if (card.isMagicalHatDecoy)
        {
            if (destroyedByBattle)
            {
                string hatName = card.originalSpellTrapData != null
                    ? $"Mũ Tơ ({card.originalSpellTrapData.cardName})"
                    : "Mũ Tơ Ma Thuật";
                Log($"{hatName} bị phá hủy!");
                OnMonsterShattered?.Invoke(card);
            }
            if (card.originalSpellTrapData != null)
            {
                OnSendCardToGraveyard?.Invoke(card.owner, card.originalSpellTrapData);
            }
            return;
        }

        if (destroyedByBattle)
        {
            Log($"{card.data.cardName} bị phá hủy!");
            OnMonsterShattered?.Invoke(card);
            OnDestroyed?.Invoke(card);

            if (card.data.monsterEffect == MonsterEffectType.OnDestroyedSearchMonster)
            {
                CardData searched = OnSearchMonsterFromDeck?.Invoke(card.owner);
                if (searched != null)
                {
                    Log($"[Hiệu ứng {card.data.cardName}] Tìm được {searched.cardName} từ Deck đưa lên tay!");
                }
            }
        }
    }
}