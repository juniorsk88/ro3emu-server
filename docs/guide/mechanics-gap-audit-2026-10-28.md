# Mecânicas que os dados definem e o servidor ainda não aplica (auditoria de 28/10/2026)

Cinco auditorias de leitura (atributos e fórmula de dano; skills; buffs, status e monstros; itens, equipamentos, cartas e progressão; mensagens, tabelas e quests), cruzando `data/config/*.json`, o Lua do cliente (`lua-src`) e o código do servidor (`src/Ro3Emu.Server/Game`). **[V]** = verificado em dado e código; **[I]** = inferido. Nada aqui foi alterado no servidor; já corrigido hoje (não repetido): bônus/reduções de dano 209–216/225/227/229/231/250/252, cura 207/208, cartas de família, Ataque Duplo, reset de recarga, anular magia, elemento da armadura, Splash, autocast, imunidades, golpe após crítico e reflexo PvP.

## 1. Ranking geral (impacto para o jogador ÷ esforço)

### A. Ganhos rápidos (horas cada)

| # | O que falta | Evidência | Esforço |
|---|---|---|---|
| A1 | `Healing Skills` e `Detoxify` são **código morto**: o servidor compara `"Detoxify"`/`"HealingSkills"`/`"Taunt"`, mas o script real no dado é `Cleanse`/`PoisonBoom` | `Effects.cs:220,244-250` [V] | trivial |
| A2 | **Passivas de maestria de arma sem efeito** (Sword/Spear/Mace/Axe Mastery, Double Attack, Dual-Wield, Riding, Dragon Training…): 80 linhas em 11 grupos; o bônus está em `_iSpecialEffect` (ops 16/17/21/22), e o servidor só lê `_kAttri` | `CombatService.Protocol.cs:74` [V] | pequeno |
| A3 | **Magia e cura nunca dão crítico**; chance de crítico extra 200/237/238/254 ignorada. O Lua tem a fórmula do crítico de cura (`LC_PlayerInfoHelper.lua:881-958`, chance `0.5·cri/ParCri`, dano `1.5+0.5·209`) | `CombatService.cs:621` bloqueia `!magic` [V] | pequeno |
| A4 | **Pool "12xx" ignorado** (1207/1208, 1211–1216, 1227–1230, 1250–1253): 44 armas de torneio com 1215 de 36% a 246%, 77 buffs de skill de jogador (Maximize Power, Lex Aeterna, Assumptio, Defending Aura…) | `Visuals.cs` só lê 21x [V]; ser multiplicativo [I] | pequeno |
| A5 | **Elemental Counter 235/236** (todos os jogadores com ressonância de refino/strengthen) e **elemento/raça/tamanho/boss** 301–340, 401–420, 501–506, 601–604 (96 de 242 cartas e 32 buffs têm algum) | `BaseDamage` só usa a tabela de elemento [V]; mapeamento de raça/boss [I] | pequeno/médio |
| A6 | **Cast/recarga por atributo**: 201–204, 1335 (Phen +25%, Arcane Acceleration), 205/206 (Cooldown Potion −10%, Blessing of Odin −30%), overflow 1034/1044 | `ChantOf` usa só 116; `StartCooldownAsync` só `_iCD` [V] | pequeno |
| A7 | **Requisitos de equipamento não validados**: 1018 de 1289 equipamentos têm nível mínimo e 1005 restrição de classe; o servidor equipa qualquer um | `EquipAsync` não chama `ItemRuleError` [V no código; o cliente pode bloquear na UI] | pequeno |
| A8 | **Quests que nunca entregam**: ação 116 (`UpdateSocialityScore`, 53 quests "Connect") e 115 (`MarkGuildActivityJoin`, 24) viram no-op/chamada simples e liberam 77 quests | `TryDeliveryActions` devolve `TaskRequirements` [V] | pequeno |
| A9 | Conversões de stat 20–24, 30, 31 (Soul Echo, 3 cartas) | laço só converte 1..6 [V] | trivial |
| A10 | Custo de MP 1231/1232 (6 cartas, 16 buffs Mana Recharge) | `ResolveCosts` não aplica [V] | pequeno |

### B. Médio (um a poucos dias)

| # | O que falta | Evidência | Impacto |
|---|---|---|---|
| B1 | **Controles em monstros não fazem nada** (Stun 194 linhas/24 skills, Slow 148/28, Frozen 111/9, Immobilize 78/14, Petrify 54/7, Blind, Sleep): só publicam o ícone; `MonsterService` nem conhece `BuffService`. Slow só pelo totem Icebound | `MonsterService.cs:353-570`, `Effects.cs:216-219` [V] | **muito alto** (Frost Diver, Stone Curse, Jupitel, Quagmire, Storm Gust…) |
| B2 | **Controles em jogador só em 5 grupos** (9016, 9022, 9023, 12003, 9034); Frozen 9020 (129 skills), Immobilize 9024 (103), Petrify 9021 (72), Fear, Stagger, Blind, Curse e variantes não travam ação; nenhum controle interrompe o cast | `BuffService.StateFor` [V] | alto |
| B3 | **Dano multi-hit**: 28 grupos com `_kDamageParam1` de vários valores (Soul Expansion, Tetra Vortex, Soul Strike, Gravitational Impact, Sonic Blow, Holy Cross…) causam só uma instância; `_kDamageFormulaExtra` (46 grupos ativos: +0,3% por 1% de ASPD, "Ressonância 0,1%") só vale para pets/ticks | `BaseDamage` usa só `ratios[0]` [V] | alto (Warlock) |
| B4 | **Escudos de skills de jogador não absorvem** (Kyrie Eleison, Radiant Holy Light, Psalm, Shield Cast, Emergency Treatment; ~34 grupos): `AddAsync` sem `shieldAmount`; a absorção já existe para pets | `Effects.cs:229` [V] | alto (Acólito/Sacerdote) |
| B5 | **Zonas de chão ausentes**: Sanctuary, Ember Zone, Gravitation Field, Epiclesis, Ganbantein; Fire Wall usa tick fixo (até 25 acertos) em vez de "por toque, 5 toques" | `CreateSkillObjectAsync` [V; Fire Wall I] | alto |
| B6 | **Knockback/pull/dash** só em ~10 scripts; faltam 28 grupos (Spear Stab, Charge Attack, Fearless Charge, Crescent Moon, Tetra Vortex, Shadow Step…) | `Displacements` [V] | médio |
| B7 | **Passivas inertes**: 176 grupos de passiva de classe, 26 aplicados (15%); 28 "Notes" (`_kAttriTransformRule`: a cada 4 INT +1 MATK) e ~33 passivas com buff nunca concedidas; Soul Drain, Energy Coat, Mana Recharge, Meditatio… | `AddPassiveAsync` só para carta/totem [V] | alto |
| B8 | **Monstros**: `_kBornEventBuff` só em MVP/torre (1738 monstros têm; invencibilidade, imunidade a CC, split ao morrer); resistência a elemento dos monstros (235/236 em 992 linhas de `AttriMonsterConfig`); aggro/hatred/link (`AIConfig` 69 linhas: decaimento de aggro em 713 monstros, chamada de ajuda em 413, cerco em 1221) | `MonsterService`, `AIConfig` [V] | médio/alto |
| B9 | **Reativas**: Auto Counter, Parry, Shield Reflect, Recoil, Reflect Damage, Death Bound, Devotion, King's Grace | buff → sub-skill em `_kLogicData` [V] | médio |
| B10 | **Furtividade** (Hiding, Cloaking, Disguise, Ruwach, Sight; 19 grupos/64 linhas; cartas de revelar escondidos) e **Turn Undead** | nenhum código [V] | médio |
| B11 | **Talentos sazonais de profissão** (árvores 10102–10104, 38 nós, 218 níveis): `Supported` os recusa; Assassino (Blade Dance, Deadly Poison, Meteor Assault) | `SeasonTalentService.cs:240` [V] | médio |
| B12 | **Penetração 217–220**, **PvP sem bônus** (233/234, 1330/1331; o caminho PvP não aplica nenhum atributo), elemento do atacante fixo em 1 (flechas/arma encantada) | `BaseDamage`, `EventsPvP.cs:72-76` [V] | médio |
| B13 | **Cartas**: Skill(3) (7 cartas: Envenom, Heal, Disguise, Magnum Break, Sight), SP drenado, "controle não interrompe cast" (Phen), Ladybug, Shadow Angeling; "indestrutível" sem dado | `CardEffects` [V] | médio |
| B14 | Revive/penalidade de morte e fadiga: `ReviveConfig` (204 linhas) e campos de mapa de revive não são lidos; contador fixo de 30 s | `PlayerDiesAsync` [V] | médio |
| B15 | Buffs não persistem entre sessões (comida e poção somem ao relogar; `_iOffline` em 350 linhas) e tipos de pilha (TimeStack, tipo 6, `_kMaxMultiSourceCount`) | `BuffService` [V] | médio |
| B16 | Pergaminhos de encantamento só de skill (28 linhas "Realm-Breaking/Warding Power": contar marcas de Eco de Alma e somar 221/223) | `EnchantService.cs:44-46` [V; fórmula I] | pequeno/médio |
| B17 | Passivas de Pet fora da Torre: os pets só dão atributo; 336 linhas com `_kFightSkill` e 84 passivas centrais não lutam | `PetFollowerService` só visual [V] | alto, grande |

### C. Sistemas grandes (dias a semanas; várias já existem no original)

| # | Sistema | Evidência |
|---|---|---|
| C1 | **Astral Level / Paragon** (função 71, nível 99): mensagens 83101–83105; tabelas `Patron*` (247 linhas); hoje não há EXP acima de 99. PR #71 do original (conflito alto, colunas novas) | zero código [V] |
| C2 | **Guilda**: impeachment/fusão (32235/32236, 32314–32319), e-mail em grupo (32241), Ilmata (32266), tecnologias e estratégias (32304–32309; tabelas `GuildWarTechConfig` 30, `GuildTimedStrategyConfig` 48), estoque de guerra (32312). PRs #75/#77/#95 do original cobrem parte | 141 pedidos de cliente sem rota [V] |
| C3 | **Taverna** (função 2410; 23 mensagens 82481–82514; `Barroom*`), **voz de canal de guilda** (83700–83723) | requer relay de voz [V] |
| C4 | **Ritual do Dragão Starshatter** (82968–82973), **Kafra Survivor** extras (12 mensagens), **Títulos** (função 14, nível 9999 no original) | README lista como "not yet" |
| C5 | **46 tipos de alvo de quest** sem leitor (165 quests) além das 78 sem entrega: **183 de 1072 quests (17%)** não completam | `TaskObjectives.Required()` [V] |
| C6 | Cerco (ferramentas 32270–32284), Card Bonds, AFK offline, Home/Fazenda, mini-jogos | rotas respondem `FeatureUnavailable` [V] |

## 2. Detalhes por área

### 2.1 Atributos e fórmula (auditoria 1)
- Todo atributo entra no `StatCalculator` e é enviado ao cliente; o problema é **quem lê**. Lidos em fórmula: 1–6, 101–116, 207–216, 225, 227, 229, 231, 232, 250, 252, 701–728 (só por `BuffTagConfig`). Armazenados e **ignorados**: derivados 23/24/30/31, penetração 217–220, crítico 200/237/238/254, cast/cooldown 201–206/1335, 221–224, elemento/raça/tamanho/boss 235/236/301–340/401–420/501–506/601–604, PvP 233/234/1330/1331, reduções 226/228/230/251/253, o pool "12xx", MP/cura 239–248/1231/1232, overflow 1034/1044.
- Parâmetros de `PublicParamConfig` que o dado define e o servidor não lê: `*DamageModifierMin/Max` (clamp 0,1–10 só vale para elemento), `RaceCorrected`, `SizeCorrection`, `BOSS/NonBOSSDamage`, `DmgAttenuationParam`/`DmgAttenuationDamNumParam` (queda de dano em área [I]), `PVE/PVPElementRestraint`.
- **Divergência de fórmula de cast (a decidir):** a **tela da skill** do cliente usa `variável·(1 − √(ChantSpeed/600) + PreVarChant/10000)` (`UI_Com_Skill_PopupItemWidgetCtrl.lua`, `math.sqrt`; é o que o servidor aplica desde 27/10), mas a **tela de atributos** (`LC_PlayerInfoHelper._doFormulaCastTime`) mostra a redução como `3·c/(800+c)` com `VariableChantPerParamA/B/C` = 3/800/1 e teto 400. Para c = 100/200/300/400 o resto da parte variável é 0,59/0,42/0,29/0,18 (√) contra 0,67/0,40/0,18/0 (3c/(800+c)). Os dados não dizem qual é a do servidor original; hoje vale a da tela da skill [I].
- Overflow (`OverflowAtkSpd2PhysDmgR`, `OverflowCastSpd2MagicDmgR`): cada ponto acima do teto vira 0,1% de PATK/MATK até 20% [dado V, fórmula I].
- Verificado correto: tabela de elementos, regeneração de HP/SP, ASPD (raiz 115, teto 5), pontos de atributo, `AttriBaseLvConfig`, `JobConfig._kAttri`.

### 2.2 Skills (auditoria 2)
- 2190 valores de `_kScriptLogic`; ~535 pertencem a classes jogáveis; só ~32 têm tratamento próprio, os demais caem no caminho genérico (dano + `_kBuff1/_kBuff2`). Sem nenhum tratamento: Sanctuary, Ember Zone, Gravitation Field, Kyrie Eleison, Lex Aeterna, Turn Undead, Aspersio, Hiding/Disguise, Enchant Poison/Envenom/Venom Dust, Counter Attack, Shield Reflect/Reflect Damage/Death Bound, Devotion/King's Grace, Release, Soul Expansion, Tetra Vortex, Vector Shock, Singularity, Comet, Jupitel Thunder, Sightrasher.
- `_kSkillAttributeList`: 488 das 1525 tuplas ignoradas (181 troca de buff, 76 chance de crítico, 38 custo). `_iSkillCondition` (99 grupos: exige arma/buff/montaria) não é validado. O CD público não escala com ASPD (`_iASPDPubCD`, 62 grupos). Cast não é interrompido por golpe recebido (`_iInterruptedType`, 450 grupos).
- Raio zero: `_kRange[0] = 0` em Chain Lightning, Sonic Burst, Rain of Spears, Holy Cross → só o alvo principal.
- Recursos/energia: `_kSkillCostParam` (23 grupos) não é lido; Reading Spellbook, Book Pages, Meteor/Comet extras, Hundred Spears, Shield Stance, Stigma ficam sem o ciclo.

### 2.3 Buffs, status e monstros (auditoria 3)
- Controles em monstro: ver B1. O atributo 1131 (MSPD) das 48 linhas Slow é ignorado nos monstros.
- `CanHitMonster` não verifica invencibilidade/seleção; `DisplaceTargetAsync` empurra qualquer monstro, inclusive chefes. Born buffs relevantes ignorados em campo: 4903001/2 (imunidade a CC e knockback, 28 monstros), 14720801 (invencível, 34), 4112001 (Dragonblood, 12), 4116601 (Split em 3, 36), 4200101 (Imobilizar de 800 ms ao nascer, 802).
- Dispel/imunidade genérica: nenhuma checagem das categorias 20/21/22/70/99; `_iDisperse` (2004 linhas) sem tratamento; `Detoxify`/`HealingSkills` mortos (A1).
- `MVPConfig._iShortestDeadTime`, `_kFixedpointrefresh`, `_iRareRewardTeamProbability` não são lidos; `AttriMonsterConfig` 3002 (Max Rage) e 3004 (fases) em 36 monstros sem medidor.
- Correção sobre uma nota antiga: `NPCConfig._iRace` existe (1 Brute, 2 Demi-Human, 3 Demon, 4 Plant, 5 Undead, 6 Formless, 7 Fish, 8 Angel, 9 Insect, 10 Dragon; a raça n corresponde a 400+n "Damage to" e 410+n "Reduction from"). As cartas de **família** ("todos os Orcs") não são raça, por isso o servidor casa pelo nome do monstro; os atributos 401–420 (raça) é que ainda não são lidos.

### 2.4 Itens, cartas e progressão (auditoria 4)
- `SpecialEffectConfig`: 959 linhas, 22 tipos de operação. Executados: 1 (atributo; equipamento, cartas flash, totem, eco de alma; **não** talento sazonal e guilda), 2 (buff passivo; cartas e totem, **não** talento sazonal e guilda), 4/5 (stunts), 7/9/10/18 (totem), 8/14 (conversão), 13 (poção), 15. **Não executados:** 3 (concede skill), 6 (SkillInUse, parcial), 11 (elemento de eco de alma), 12 (talento), 16 (atributo por tipo de arma: A2), 17, 19, 20, 21, 22.
- Sem dado a implementar: durabilidade/reparo, conjuntos de equipamento (não há tabela), skill em `EquipConfig`, bônus de WardRobe; `TitleConfig._kAttr` está zerado; `SizeRestraintConfig`/`EquipEffectivenessConfig` neutros.
- Menores: páginas da biblioteca (16 pares HP+100/STR+10) e buffs de "amigo próximo" (`FriendlinessConfig`, +vida/ATQ até 0,8%) não aplicados; bênção do Valhalla (atributo 4008) não aplicada; ações de item 606 e 110/111 sem tratador (Madogear/Tank/Peco; taverna); `_kCouplingExtraEffect` (9 itens) não lido; passivas de totem de PvP (6 famílias) nunca disparam (`OnPlayerKilledAsync`/`OnPlayerDamagedAsync` não são chamados); dual-wield: armas de parte 2 só vão para a grade 1.

### 2.5 Mensagens, tabelas e quests (auditoria 5)
- 156 pedidos do cliente sem tratador (≈137 reais); um pedido sem rota recebe resposta vazia com `result=0`, que o cliente lê como **sucesso** (a tela abre vazia ou "aceita" sem efeito), o que mascara o que falta.
- 218 de 517 tabelas de config não aparecem no servidor; ~100 são arte/áudio/GM/UI; 17 estão vazias no export. As voltadas ao jogador estão nos sistemas C1–C6 e em: Battle Glory (`BattleGlory*`), mini-jogos (`MusicGame*`, `Journey*`, `KoiBet*`, `QTEEvent`, `TenChallenge*`), Home/Fazenda, `VipConfig`, `WarehouseConfig`, `BackpackGridConfig`, `TransformConfig`, `ReviveConfig`, `CampFire*`.
- Stubs com rota e sem efeito: pagamento/recarga (2002/2003), `ClimTowerSweep`, AFK offline (43405/43408, `OnHook*`), `PetBreak` (a tabela tem 0 linhas), ações de item 107/108/109.
- Quests: 90 dos 136 tipos de alvo são tratados; 46 sem leitor bloqueiam 165 quests; 78 têm entrega de servidor sem tratador (53 ação 116, 24 ação 115, 1 ação 410); união 183. Maiores bloqueios: alvo 107005 (31 quests: "limpar instância em grupo"), 210005 (25), 106012 (20: MVP em grupo), 101021 (12: montaria), 106010 (9: taverna).

## 3. Ordem sugerida (para quem joga Mago/Acólito)

1. **Pacote rápido de skills e fórmula** (A1, A3, A4, A6, A2): ordem de um a dois dias, vários itens triviais; todos têm teste de unidade fácil.
2. **Controles em monstros** (B1) e **estados de jogador** (B2): o maior buraco visível de combate.
3. **Multi-hit e fórmulas extras** (B3), **escudos** (B4), **Sanctuary e zonas** (B5).
4. **Passivas** (B7) e **elemento/raça/boss** (A5).
5. **Quests** (A8 depois os alvos) e **requisitos de equipamento** (A7).
6. Sistemas grandes (C1…C6) só se a meta for paridade de endgame; a migração para a base do original (já preparada em `D:\RO3Emu\merge-work`) traz parte de C1/C2/C4.

## 4. Limitações

- Análise estática: itens "referenciados" por constante em serviços de interceptação foram aceitos como tratados; falsos negativos são possíveis se uma rota for registrada de forma não literal.
- Contagens de "buff de skill de jogador" ignoram buffs concedidos por item ou carta.
- Fórmulas marcadas [I] vêm de nomes de parâmetro e descrições; não foram confirmadas contra o servidor original.
- `AttriDungeonCorrectionsConfig` e `AttriSeasonalCorrectionsConfig` foram exportadas vazias.
