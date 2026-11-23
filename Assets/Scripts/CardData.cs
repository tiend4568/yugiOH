using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

public enum CardType
{
    Monster,
    Spell,
    Trap
}

public enum SpellEffectType
{
    None,
    DestroyAllEnemyMonsters,    // Raigeki: Tiêu diệt toàn bộ quái đối thủ
    DestroyAllMonsters,         // Dark Hole (Hố Đen): Phá hủy tất cả quái thú trên sân
    DrawCards,                  // Pot of Greed (Hũ Tham Lam): Rút 2 lá bài
    HealLP,                     // Hồi LP bản thân
    DamageEnemyLP,              // Trừ thẳng LP đối thủ
    BuffAllMyMonstersATK,       // Tăng ATK toàn bộ quái phe mình
    DebuffAllEnemyMonstersATK,  // Giảm ATK toàn bộ quái đối thủ
    ReviveFromGraveyard,        // Monster Reborn: Hồi sinh 1 quái thú từ Mộ
    SwordsOfRevealingLight,     // Lớp Kiếm Bảo Hộ Của Ánh Sáng: Lật ngửa quái địch, chặn tấn công 3 lượt đối thủ
    MagicalHats,                // Mũ Tơ Ma Thuật (Trap): Giấu 1 quái cùng 2 Phép/Bẫy từ Deck thành quái úp 0/0 và xáo vị trí
    TrapHole                    // Hang Rơi (Trap): Khi đối thủ Triệu hồi Thường / Lật quái có ATK >= 1000, phá hủy quái đó
}

public enum MonsterEffectType
{
    None,                           // Quái thường (NORMAL)
    FlipReturnGraveyardToDeck,      // FLIP (Gremlin Tử Thần): Chọn 1 lá trong Mộ xáo trộn vào Deck
    FlipDestroyEnemyMonster,        // FLIP: Tiêu diệt 1 quái mạnh nhất của đối thủ
    FlipReturnEnemyMonsterToHand,   // FLIP: Đẩy 1 quái trên sân đối phương về lại tay
    OnSummonDestroyEnemyMonster,    // Khi Triệu Hồi: Tiêu diệt 1 quái mạnh nhất của đối thủ
    OnSummonDrawCard,               // Khi Triệu Hồi: Rút thêm lá bài
    OnSummonBurnLP,                 // Khi Triệu Hồi: Bắn sát thương thẳng LP địch
    OnSummonHealLP,                 // Khi Triệu Hồi: Hồi phục LP cho bản thân
    OnDestroyedSearchMonster,       // Khi Bị Tiêu Diệt Xuống Mộ: Tìm 1 quái ATK <= 1500 lên tay
    PiercingDamage,                 // Xuyên Thủ: Tấn công quái phòng thủ vẫn trừ LP chênh lệch
    BattleIndestructible,           // Bất Tử Chiến Đấu: Không bị phá hủy bởi chiến đấu
    BoostATKOnAttack,               // Cuồng Nộ: Tăng vĩnh viễn +300 ATK mỗi lần tấn công
    KuribohHandGuard,               // Kuriboh: Khi bị tấn công, bỏ lá này từ trên tay xuống Mộ để nhận 0 sát thương chiến đấu
    DivineObelisk,                  // Thần Lính Khổng Lồ Obelisk: 3 hiến tế, không thể bị chọn làm mục tiêu, hiến tế 2 quái diệt sạch quái đối thủ, tự xuống Mộ ở End Phase nếu Triệu hồi Đặc biệt
    DivineSlifer,                   // Rồng Thần Bầu Trời Slifer: 3 hiến tế, +1000 ATK/DEF mỗi lá trên tay, quái đối thủ triệu hồi thế Công mất 2000 ATK (về 0 thì nổ), tự xuống Mộ ở End Phase nếu Triệu hồi Đặc biệt
    DivineRa                        // Rồng Cánh Thần Của Ra: 3 hiến tế, không thể Triệu hồi Đặc biệt, trả LP còn 100 khi Triệu hồi Thường để cộng ATK/DEF, trả 1000 LP phá hủy 1 quái
}

[Serializable]
public class CardData
{
    public string cardName;
    public Sprite cardSprite;
    public CardType cardType = CardType.Monster;

    public int atk;
    public int def;
    public int level;

    public SpellEffectType spellEffect = SpellEffectType.None;
    public MonsterEffectType monsterEffect = MonsterEffectType.None;
    public int effectValue = 0;

    [TextArea] public string description;

    public bool IsSpellOrTrap => cardType == CardType.Spell || cardType == CardType.Trap;

    public bool IsDivineGod =>
        monsterEffect == MonsterEffectType.DivineObelisk ||
        monsterEffect == MonsterEffectType.DivineSlifer ||
        monsterEffect == MonsterEffectType.DivineRa;

    public bool CannotBeSpecialSummoned => monsterEffect == MonsterEffectType.DivineRa;
    public bool CannotBeTargetedByEffects => monsterEffect == MonsterEffectType.DivineObelisk;
    public bool HasQuestionMarkStats =>
        monsterEffect == MonsterEffectType.DivineSlifer ||
        monsterEffect == MonsterEffectType.DivineRa;

    public string StatDisplay => HasQuestionMarkStats ? "?/?" : $"{atk}/{def}";

    public string CardTag
    {
        get
        {
            if (cardType == CardType.Spell) return "BÀI PHÉP";
            if (cardType == CardType.Trap) return "BÀI BẪY";
            if (IsDivineGod) return "THẦN AI CẬP";
            return monsterEffect != MonsterEffectType.None ? "QUÁI HIỆU ỨNG" : "QUÁI THÚ";
        }
    }

    public CardData(string name, Sprite sprite, int atk = 1500, int def = 1200, int level = 4, string desc = "")
    {
        this.cardName = name;
        this.cardSprite = sprite;
        this.cardType = CardType.Monster;
        this.atk = atk;
        this.def = def;
        this.level = level;
        this.monsterEffect = MonsterEffectType.None;
        this.description = desc;
    }

    public static CardData CreateSpell(string name, Sprite sprite, SpellEffectType effect, int value, string desc = "")
    {
        return new CardData(name, sprite, 0, 0, 0, desc)
        {
            cardType = CardType.Spell,
            spellEffect = effect,
            effectValue = value
        };
    }

    public static CardData CreateTrap(string name, Sprite sprite, SpellEffectType effect, int value, string desc = "")
    {
        return new CardData(name, sprite, 0, 0, 0, desc)
        {
            cardType = CardType.Trap,
            spellEffect = effect,
            effectValue = value
        };
    }

    public CardData Clone()
    {
        return new CardData(cardName, cardSprite, atk, def, level, description)
        {
            cardType = this.cardType,
            spellEffect = this.spellEffect,
            monsterEffect = this.monsterEffect,
            effectValue = this.effectValue
        };
    }
}

[Serializable]
public class CardStatConfig
{
    public string cardName;
    public CardType cardType = CardType.Monster;

    [Header("Cấu hình Quái Thú")]
    [Range(1, 12)] public int level = 4;
    public int atk = 1500;
    public int def = 1200;
    public MonsterEffectType monsterEffect = MonsterEffectType.None;

    [Header("Cấu hình Bài Phép / Bẫy")]
    public SpellEffectType spellEffect = SpellEffectType.None;

    [Header("Giá trị hiệu ứng")]
    public int effectValue = 0;

    [TextArea] public string description;
}

[CreateAssetMenu(fileName = "CardDatabase", menuName = "YuGiOh/Card Database")]
public class CardDatabaseSO : ScriptableObject
{
    public List<CardStatConfig> cards = new List<CardStatConfig>();

    // Hỗ trợ cả __LV4, __LV7__EFF, __LVNULL_SPELL, __LVNULL__TRAP
    private const string TAG_PATTERN = @"__LV(?:__)?(\d{1,2}|NULL)(?:_+(.*))?$";

    public CardData BuildCardData(string rawFileName, Sprite sprite, bool isSpellFolder)
    {
        string cleanName = ExtractCleanName(rawFileName);
        int? explicitLevel = ExtractLevelFromTag(rawFileName);
        CardType? explicitCardType = ExtractCardTypeTag(rawFileName);

        CardStatConfig found = cards.Find(c =>
            c.cardName.Equals(rawFileName, StringComparison.OrdinalIgnoreCase) ||
            c.cardName.Equals(cleanName, StringComparison.OrdinalIgnoreCase));

        if (found != null)
        {
            bool treatAsSpellOrTrap = isSpellFolder
                || explicitCardType == CardType.Spell
                || explicitCardType == CardType.Trap
                || found.cardType == CardType.Spell
                || found.cardType == CardType.Trap;

            if (treatAsSpellOrTrap)
            {
                CardType finalType = explicitCardType ?? (found.cardType == CardType.Trap ? CardType.Trap : CardType.Spell);
                SpellEffectType finalSpellEffect = found.spellEffect;
                int finalSpellVal = found.effectValue;
                string finalSpellDesc = found.description;

                if (finalSpellEffect == SpellEffectType.None)
                {
                    CardData autoSp = AutoBuildFallbackSpell(rawFileName, sprite);
                    finalSpellEffect = autoSp.spellEffect;
                    finalSpellVal = autoSp.effectValue;
                    if (!string.IsNullOrEmpty(autoSp.description)) finalSpellDesc = autoSp.description;
                }

                return finalType == CardType.Trap
                    ? CardData.CreateTrap(cleanName, sprite, finalSpellEffect, finalSpellVal, finalSpellDesc)
                    : CardData.CreateSpell(cleanName, sprite, finalSpellEffect, finalSpellVal, finalSpellDesc);
            }

            int finalLevel = explicitLevel ?? Mathf.Clamp(found.level, 1, 12);
            MonsterEffectType finalMonsterEff = found.monsterEffect;
            int finalMonsterVal = found.effectValue;
            string finalMonsterDesc = found.description;

            if (finalMonsterEff == MonsterEffectType.None &&
                TryGetMonsterByName(cleanName, out _, out _, out _, out MonsterEffectType presetEff, out int presetVal, out string presetDesc) &&
                presetEff != MonsterEffectType.None)
            {
                finalMonsterEff = presetEff;
                finalMonsterVal = presetVal;
                if (!string.IsNullOrEmpty(presetDesc)) finalMonsterDesc = presetDesc;
            }

            return new CardData(cleanName, sprite, found.atk, found.def, finalLevel, finalMonsterDesc)
            {
                cardType = CardType.Monster,
                monsterEffect = finalMonsterEff,
                effectValue = finalMonsterVal
            };
        }

        if (isSpellFolder || explicitCardType == CardType.Spell || explicitCardType == CardType.Trap)
        {
            return AutoBuildFallbackSpell(rawFileName, sprite);
        }

        return AutoBuildFallbackMonster(rawFileName, sprite);
    }

    public static int? ExtractLevelFromTag(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return null;
        Match m = Regex.Match(rawName.Trim(), TAG_PATTERN, RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int lv))
        {
            return Mathf.Clamp(lv, 1, 12);
        }
        return null;
    }

    public static CardType? ExtractCardTypeTag(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return null;
        Match m = Regex.Match(rawName.Trim(), TAG_PATTERN, RegexOptions.IgnoreCase);
        if (m.Success && !string.IsNullOrEmpty(m.Groups[2].Value))
        {
            string tag = m.Groups[2].Value.Trim().ToLower();
            if (tag.Contains("trap") || tag.Contains("bẫy") || tag.Contains("bay"))
                return CardType.Trap;
            if (tag.Contains("spell") || tag.Contains("phép") || tag.Contains("phep"))
                return CardType.Spell;
        }
        return null;
    }

    public static bool? ExtractMonsterTypeTag(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return null;
        Match m = Regex.Match(rawName.Trim(), TAG_PATTERN, RegexOptions.IgnoreCase);
        if (m.Success && !string.IsNullOrEmpty(m.Groups[2].Value))
        {
            string tag = m.Groups[2].Value.Trim().ToLower();
            if (tag.Contains("eff") || tag.Contains("flip") || tag.Contains("hiệu") || tag.Contains("hieu"))
                return true;
            if (tag.Contains("normal") || tag.Contains("thường") || tag.Contains("thuong") || tag.Contains("quái thú") || tag.Contains("quai thu"))
                return false;
        }
        return null;
    }

    public static string ExtractCleanName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "";
        string cleaned = Regex.Replace(rawName.Trim(), TAG_PATTERN, "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    public static CardData AutoBuildFallbackMonster(string rawFileName, Sprite sprite)
    {
        string cleanName = ExtractCleanName(rawFileName);
        int? explicitLevel = ExtractLevelFromTag(rawFileName);

        if (TryGetMonsterByName(cleanName, out int presetLv, out int presetAtk, out int presetDef, out MonsterEffectType presetEff, out int presetVal, out string presetDesc))
        {
            int finalLv = explicitLevel ?? presetLv;

            return new CardData(cleanName, sprite, presetAtk, presetDef, finalLv, presetDesc)
            {
                cardType = CardType.Monster,
                monsterEffect = presetEff,
                effectValue = presetVal
            };
        }

        int fallbackLevel = explicitLevel ?? 4;
        int fallbackAtk = fallbackLevel <= 4 ? 1500 : (fallbackLevel <= 6 ? 2200 : 2600);
        int fallbackDef = fallbackLevel <= 4 ? 1200 : (fallbackLevel <= 6 ? 1800 : 2100);

        return new CardData(cleanName, sprite, fallbackAtk, fallbackDef, fallbackLevel, "Quái thú chiến đấu.")
        {
            cardType = CardType.Monster,
            monsterEffect = MonsterEffectType.None
        };
    }

    public static bool TryGetMonsterByName(string monsterName, out int lv, out int atk, out int def, out MonsterEffectType eff, out int val, out string desc)
    {
        lv = 4; atk = 1500; def = 1200; eff = MonsterEffectType.None; val = 0; desc = "Quái thú chiến đấu.";
        if (string.IsNullOrEmpty(monsterName)) return false;

        string key = monsterName.Trim().ToLower();

        if (key.Contains("beaver warrior") || key.Contains("lính hải li"))
        {
            lv = 4; atk = 1200; def = 1500;
            desc = "Những gì sinh vật này thiếu về kích thước mà nó bù đắp để phòng thủ khi chiến đấu trên đồng cỏ.";
            return true;
        }

        if (key.Contains("celtic guardian") || key.Contains("kiếm sĩ yêu tinh"))
        {
            lv = 4; atk = 1400; def = 1200;
            desc = "Là một yêu tinh đã học cách sử dụng kiếm, anh ta đánh bại kẻ thù bằng những đòn tấn công nhanh như chớp.";
            return true;
        }

        if (key.Contains("curse of dragon") || key.Contains("rồng lời nguyền"))
        {
            lv = 5; atk = 2000; def = 1500;
            desc = "Một con rồng độc ác khai thác vào thế lực bóng tối để thực hiện tấn công mạnh mẽ.";
            return true;
        }

        if (key.Contains("dark magician") || key.Contains("phù thủy áo đen"))
        {
            lv = 7; atk = 2500; def = 2100;
            desc = "The ultimate wizard in terms of attack and defense.";
            return true;
        }

        if (key.Contains("feral imp") || key.Contains("gremlin tử thần"))
        {
            lv = 4; atk = 1600; def = 1800;
            eff = MonsterEffectType.FlipReturnGraveyardToDeck;
            desc = "FLIP: Chọn vào 1 lá bài trong Mộ của bạn; xáo trộn mục tiêu đó vào Deck.";
            return true;
        }

        if (key.Contains("kuriboh"))
        {
            lv = 1; atk = 300; def = 200;
            eff = MonsterEffectType.KuribohHandGuard;
            desc = "Trong damage calculation, nếu quái thú của đối thủ tấn công (Hiệu ứng nhanh): Bạn có thể gửi xuống Mộ lá bài này từ trên tay; bạn không nhận bất cứ thiệt hại nào từ chiến đấu đó.";
            return true;
        }

        if (key.Contains("obelisk") || key.Contains("thần lính khổng lồ"))
        {
            lv = 10; atk = 4000; def = 4000;
            eff = MonsterEffectType.DivineObelisk;
            desc = "Yêu cầu 3 hiến tế để Triệu hồi Thường (không thể Triệu hồi Úp Thường). Khi được Triệu hồi Thường, các lá bài và hiệu ứng không thể được kích hoạt. Cả hai người chơi không thể chọn lá bài này làm mục tiêu với các hiệu ứng của lá bài. Một lần mỗi lượt, trong End Phase, nếu lá bài này được Triệu hồi Đặc biệt: Gửi nó đến Mộ. Bạn có thể Hiến tế 2 quái thú; phá huỷ tất cả quái thú mà đối thủ của bạn điều khiển (không thể tuyên bố tấn công khi hiệu ứng này được kích hoạt).";
            return true;
        }

        if (key.Contains("slifer") || key.Contains("rồng thần bầu trời"))
        {
            lv = 10; atk = 0; def = 0;
            eff = MonsterEffectType.DivineSlifer;
            desc = "Yêu cầu 3 hiến tế để Triệu hồi Thường (không thể Triệu hồi Úp Thường). Khi được Triệu hồi Thường, các lá bài và hiệu ứng không thể được kích hoạt. Một lần mỗi lượt, trong End Phase, nếu lá bài này được Triệu hồi Đặc biệt: Gửi nó đến Mộ. Nhận 1000 ATK / DEF cho mỗi lá bài trong tay của bạn. Nếu quái thú được Triệu hồi Thường hoặc Đặc biệt đến sân của đối thủ ở Tư thế Tấn công: Quái thú đó mất 2000 ATK, nếu ATK của nó bị giảm xuống 0 do đó, hãy phá huỷ nó.";
            return true;
        }

        if (key.Contains("winged dragon of ra") || key.Contains("rồng cánh thần của ra"))
        {
            lv = 10; atk = 0; def = 0;
            eff = MonsterEffectType.DivineRa;
            desc = "Không thể được Triệu hồi Đặc biệt. Yêu cầu 3 hiến tế để Triệu hồi Thường (không thể Triệu hồi Úp Thường). Khi được Triệu hồi Thường, các lá bài và hiệu ứng khác không thể được kích hoạt. Khi lá bài này được Triệu hồi Thường: Bạn có thể trả LP để bạn chỉ còn lại 100; lá bài này nhận được ATK / DEF bằng với số LP được trả. Bạn có thể trả 1000 LP, sau đó chọn vào 1 quái thú trên sân; phá huỷ mục tiêu đó.";
            return true;
        }

        if (key.Contains("gaia the fierce knight") || key.Contains("kỵ sĩ hắc ám gaia"))
        {
            lv = 7; atk = 2300; def = 2100;
            desc = "Một hiệp sĩ có ngựa phi nhanh hơn cả gió. Cú xung phong của chàng là một sức mạnh không thể xem thường.";
            return true;
        }

        if (key.Contains("gazelle"))
        {
            lv = 4; atk = 1500; def = 1200;
            desc = "Con quái thú này di chuyển nhanh đến mức nó trông giống như một ảo ảnh đối với đôi mắt của người phàm.\n(Lá bài này luôn được coi là lá bài \"Phantom Beast\".)";
            return true;
        }

        if (key.Contains("giant soldier of stone") || key.Contains("lính đá khổng lồ"))
        {
            lv = 3; atk = 1300; def = 2000;
            desc = "Một chiến binh khổng lồ làm bằng đá. Một cú đấm từ sinh vật này khiến trái đất rung chuyển.";
            return true;
        }

        if (key.Contains("mystical elf") || key.Contains("elf thánh"))
        {
            lv = 4; atk = 800; def = 2000;
            desc = "Một yêu tinh mỏng manh, thiếu tấn công, nhưng có khả năng phòng thủ tuyệt vời được hỗ trợ bởi sức mạnh thần bí.";
            return true;
        }

        if (key.Contains("summoned skull") || key.Contains("quỉ được triệu hồi") || key.Contains("quỷ được triệu hồi"))
        {
            lv = 6; atk = 2500; def = 1200;
            desc = "Một tên yêu quái với sức mạnh đen tối vì đã gây nhầm lẫn cho kẻ thù. Trong số những quái thú thuộc loại Fiend-Type, quái thú này tự hào về lực lượng đáng kể.\n(Lá bài này luôn được coi là \"Archfiend\".)";
            return true;
        }

        if (key.Contains("torike"))
        {
            lv = 3; atk = 1200; def = 600;
            desc = "Mặc dù phòng thủ kém, nhưng chiếc sừng sắc nhọn của quái thú này giúp nó có lợi thế khi tấn công.";
            return true;
        }

        if (key.Contains("winged dragon") || key.Contains("rồng có cánh thủ thành"))
        {
            lv = 4; atk = 1400; def = 1200;
            desc = "Một con rồng thường được tìm thấy canh gác các pháo đài trên núi. Cuộc tấn công đặc trưng của nó là một cú bổ nhào sâu từ bên ngoài.";
            return true;
        }

        return false;
    }

    public static CardData AutoBuildFallbackSpell(string rawFileName, Sprite sprite)
    {
        string cleanName = ExtractCleanName(rawFileName);
        CardType? taggedType = ExtractCardTypeTag(rawFileName);
        string key = cleanName.ToLower();

        if (key.Contains("monster reborn") || key.Contains("phục sinh quái thú"))
        {
            return CardData.CreateSpell(
                cleanName,
                sprite,
                SpellEffectType.ReviveFromGraveyard,
                1,
                "Chọn mục tiêu 1 quái thú trong Mộ; Triệu hồi Đặc biệt nó."
            );
        }

        if (key.Contains("swords of revealing light") || key.Contains("lớp kiếm bảo hộ của ánh sáng"))
        {
            return CardData.CreateSpell(
                cleanName,
                sprite,
                SpellEffectType.SwordsOfRevealingLight,
                3,
                "Sau khi kích hoạt lá bài này, nó vẫn còn trên sân, nhưng bạn phải phá hủy nó trong End Phase của lượt thứ 3 của đối thủ. Khi lá bài này được kích hoạt: Nếu đối thủ của bạn điều khiển một quái thú úp, hãy lật tất cả quái thú mà họ điều khiển ngửa. Trong khi lá bài này ngửa trên sân, quái thú của đối thủ của bạn không thể tuyên bố tấn công."
            );
        }

        if (key.Contains("magical hats") || key.Contains("mũ tơ ma thuật"))
        {
            return CardData.CreateTrap(
                cleanName,
                sprite,
                SpellEffectType.MagicalHats,
                2,
                "Trong Battle Phase của đối thủ: Chọn 2 Lá Bài Phép / Bẫy từ Deck của bạn và 1 quái thú trong Vùng quái thú Chính của bạn. Triệu hồi Đặc biệt chúng dưới dạng quái thú Thường (ATK 0 / DEF 0) trong Tư thế Phòng thủ úp, Úp quái thú đã chọn nếu nó nằm ngửa và xáo trộn chúng trên sân. 2 lá bài được chọn từ Deck của bạn sắp bị phá hủy vào cuối Battle Phase và không thể ở lại trên sân ngoại trừ trong Battle Phase này."
            );
        }

        if (key.Contains("pot of greed") || key.Contains("hũ tham lam") || key.Contains("chiếc bình tham lam"))
        {
            return CardData.CreateSpell(cleanName, sprite, SpellEffectType.DrawCards, 2, "Rút 2 lá bài.");
        }

        if (key.Contains("raigeki") || key.Contains("sét đánh"))
        {
            return CardData.CreateSpell(cleanName, sprite, SpellEffectType.DestroyAllEnemyMonsters, 0, "Tiêu diệt toàn bộ quái thú đối thủ điều khiển.");
        }

        if (key.Contains("dark hole") || key.Contains("hố đen"))
        {
            return CardData.CreateSpell(cleanName, sprite, SpellEffectType.DestroyAllMonsters, 0, "Phá hủy tất cả những quái thú trên sân.");
        }

        if (key.Contains("trap hole") || key.Contains("hang rơi"))
        {
            return CardData.CreateTrap(
                cleanName,
                sprite,
                SpellEffectType.TrapHole,
                1000,
                "Khi đối thủ của bạn Thường hoặc Lật Hiến tế 1 quái thú có 1000 ATK hoặc lớn hơn: Chọn vào quái thú đó; phá huỷ mục tiêu đó."
            );
        }

        if (taggedType == CardType.Trap)
        {
            return CardData.CreateTrap(cleanName, sprite, SpellEffectType.None, 0, "Lá bài bẫy.");
        }

        return CardData.CreateSpell(cleanName, sprite, SpellEffectType.None, 0, "Lá bài phép.");
    }
}