using System;
using System.Globalization;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    internal static class StormSkillDescriptions
    {
        private const long TextBase = 99130006000;
        private static readonly int[] Power = { 2000, 2333, 2667, 3000, 3333, 3667, 4000, 4333, 4667, 5000 };
        private static readonly int[] Flat = { 3, 7, 10, 13, 17, 20, 23, 27, 30, 33 };

        internal static void Inject(Obf_R.Obf_ff dbm, Obf_R.Obf_ff global)
        {
            try
            {
                var skills = PassChallenges.Values(dbm, "m_kCFG_SkillConfig");
                var texts = global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
                if (skills == null || texts == null) return;
                for (int i = 0; i < 10; i++)
                {
                    int level = i + 1;
                    string damage = (Power[i] / 100.0).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + "% + " + Flat[i];
                    var meteor = skills.Obf_ruA<long, Obf_R.Obf_ff>(1210600L + level);
                    var storm = skills.Obf_ruA<long, Obf_R.Obf_ff>(1210800L + level);
                    if (meteor == null || storm == null) return;
                    long meteorText = TextBase + level, stormText = TextBase + 20 + level;
                    PassChallenges.Text(texts, meteorText,
                        "Invoca meteoros a até 12 m, numa área de 5 m de raio, durante 10 s. " + level +
                        " levas, cada uma com 10 golpes independentes de dano mágico de Fogo: ATQ Mág. × " + damage +
                        ". Cada golpe tem 2% de chance de causar Atordoamento por 1 s. Atinge até 99 inimigos.");
                    PassChallenges.Text(texts, stormText,
                        "Invoca uma nevasca a até 12 m, numa área de 5 m de raio, durante 5 s. " + level +
                        " ondas de dano mágico de Água: ATQ Mág. × " + damage +
                        ". Inimigos na área sofrem 20% de Lentidão. Cada onda tem 10% de chance de congelar por " +
                        Math.Min(10, 2 * level - 1) + " s. Inimigos não congelados são empurrados; os congelados não recebem dano das próximas ondas. Atinge até 99 inimigos.");
                    PassChallenges.RawSet(meteor, "_iDescription", PassChallenges.List(meteorText));
                    PassChallenges.RawSet(storm, "_iDescription", PassChallenges.List(stormText));
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.StormDescriptions] " + e.Message); }
        }
    }
}
