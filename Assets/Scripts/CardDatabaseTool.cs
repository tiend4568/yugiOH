#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

public class CardDatabaseTool
{
    [MenuItem("YuGiOh/Đồng Bộ Tên & Mô Tả Lá Bài Vào CardDatabase")]
    public static void SyncAllCardsToDatabase()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources"))
        {
            AssetDatabase.CreateFolder("Assets", "Resources");
        }

        string assetPath = "Assets/Resources/CardDatabase.asset";
        CardDatabaseSO db = AssetDatabase.LoadAssetAtPath<CardDatabaseSO>(assetPath);

        if (db == null)
        {
            db = ScriptableObject.CreateInstance<CardDatabaseSO>();
            AssetDatabase.CreateAsset(db, assetPath);
        }

        int addedCount = 0;
        int updatedCount = 0;

        // 1. Quái thú (Resources/Cards/Monster)
        Sprite[] monsters = Resources.LoadAll<Sprite>("Cards/Monster");
        foreach (var sp in monsters)
        {
            if (sp == null) continue;
            string cleanName = CardDatabaseSO.ExtractCleanName(sp.name);
            CardData autoData = CardDatabaseSO.AutoBuildFallbackMonster(sp.name, sp);

            CardStatConfig existing = db.cards.Find(c =>
                c.cardName.Equals(sp.name, System.StringComparison.OrdinalIgnoreCase) ||
                c.cardName.Equals(cleanName, System.StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                db.cards.Add(new CardStatConfig
                {
                    cardName = cleanName,
                    cardType = CardType.Monster,
                    level = autoData.level,
                    atk = autoData.atk,
                    def = autoData.def,
                    monsterEffect = autoData.monsterEffect,
                    effectValue = autoData.effectValue,
                    description = autoData.description
                });
                addedCount++;
            }
            else
            {
                existing.cardName = cleanName;
                existing.cardType = CardType.Monster;
                existing.level = autoData.level;
                existing.atk = autoData.atk;
                existing.def = autoData.def;
                existing.monsterEffect = autoData.monsterEffect;
                existing.effectValue = autoData.effectValue;
                existing.description = autoData.description;
                updatedCount++;
            }
        }

        // 2. Bài Phép (Resources/Cards/Spell)
        Sprite[] spells = Resources.LoadAll<Sprite>("Cards/Spell");
        foreach (var sp in spells)
        {
            if (sp == null) continue;
            string cleanName = CardDatabaseSO.ExtractCleanName(sp.name);
            CardData autoData = CardDatabaseSO.AutoBuildFallbackSpell(sp.name, sp);

            CardStatConfig existing = db.cards.Find(c =>
                c.cardName.Equals(sp.name, System.StringComparison.OrdinalIgnoreCase) ||
                c.cardName.Equals(cleanName, System.StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                db.cards.Add(new CardStatConfig
                {
                    cardName = cleanName,
                    cardType = CardType.Spell,
                    spellEffect = autoData.spellEffect,
                    effectValue = autoData.effectValue,
                    description = autoData.description
                });
                addedCount++;
            }
            else
            {
                existing.cardName = cleanName;
                existing.cardType = CardType.Spell;
                existing.spellEffect = autoData.spellEffect;
                existing.effectValue = autoData.effectValue;
                existing.description = autoData.description;
                updatedCount++;
            }
        }

        // 3. Bài Bẫy (Resources/Cards/Trap)
        Sprite[] traps = Resources.LoadAll<Sprite>("Cards/Trap");
        foreach (var sp in traps)
        {
            if (sp == null) continue;
            string cleanName = CardDatabaseSO.ExtractCleanName(sp.name);
            CardData autoData = CardDatabaseSO.AutoBuildFallbackSpell(sp.name, sp);

            CardStatConfig existing = db.cards.Find(c =>
                c.cardName.Equals(sp.name, System.StringComparison.OrdinalIgnoreCase) ||
                c.cardName.Equals(cleanName, System.StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                db.cards.Add(new CardStatConfig
                {
                    cardName = cleanName,
                    cardType = CardType.Trap,
                    spellEffect = autoData.spellEffect,
                    effectValue = autoData.effectValue,
                    description = autoData.description
                });
                addedCount++;
            }
            else
            {
                existing.cardName = cleanName;
                existing.cardType = CardType.Trap;
                existing.spellEffect = autoData.spellEffect;
                existing.effectValue = autoData.effectValue;
                existing.description = autoData.description;
                updatedCount++;
            }
        }

        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();
        Selection.activeObject = db;

        Debug.Log($"[YuGiOh] Đồng bộ thành công {addedCount + updatedCount} lá bài (Monster/Spell/Trap) vào CardDatabase!");
    }
}
#endif