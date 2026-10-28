# Comparação com o original (aethai/ro3emu-server)

Relatório de **leitura**. Nada foi alterado no projeto, nenhum `fetch/pull/merge/checkout/stash` foi feito aqui, nada foi publicado
e a VPS não foi tocada. O upstream foi lido por um clone parcial (`--no-checkout`) numa pasta de rascunho fora do projeto, e por
`gh pr list/view` (somente leitura). Data da análise: 2026-10-24.

## 1. Resumo

- O HEAD local (`6befb4b`, 18/10) é **ancestral direto** do upstream (`69b2c2d`, 24/10): o fork não tem commits próprios que o
  upstream não tenha. Tudo o que é seu está **só na pasta de trabalho, sem commit** (36 dos 73 arquivos alterados também foram
  mexidos pelo upstream; os outros 37 são seus e o upstream nem tem: `CreditShop`, `DropBoost`, `MapRates`, `MonsterAiSettings`,
  `CombatService.Visuals`, `NpcShops`, `NpcServiceTable`, `OriginGuardPatch`, `ReleaseLimit`, `LevelUpEffect`, `NavGrid` etc.).
- São **227 commits / 1018 arquivos**, mas os PRs vão de **#10 a #96** (não só #56–#96), mais um commit direto (`0183849`, 20/10,
  conserto da escolha de talento de profissão). Autores: o mantenedor (aethai/Hubert), `DiNaSoR` (correções pequenas), `hmwater10`
  (voz), `pattytrickz`, dependabot.
- **O obstáculo nº 1 não são os seus arquivos modificados, é a formatação.** Os PRs **#18** (formato + CI, 273 arquivos) e **#31**
  (renomeia **1581 campos privados** para `_camelCase`, 424 arquivos, mais construtores primários) mudaram o código inteiro. Todo
  PR posterior foi escrito contra os nomes novos (`_data`, `_world`, `_character`…; o seu código usa `data`, `world`, `character`).
  Resultado: **`git cherry-pick` de quase qualquer PR depois de #31 dá conflito textual mesmo em arquivo que você não mexeu**.
  Para correção pequena isso se resolve portando à mão (a ideia + poucas linhas). Para PRs grandes (talentos, pets) não.
- O upstream agora **falha o build com warnings** (`Directory.Build.props`), exige campos `_camelCase`, construtor primário e
  `[]` em coleções (IDE0290/IDE0302–0304). Código novo seu (os ~12 arquivos novos de `src/`) teria de se adaptar a isso se um dia
  você migrar para a base do upstream.
- O que o upstream **não tem** e você tem: a cruz do Magnus Exorcismus (`CreateSkillObjectAsync` do upstream continua **sem**
  caso `Magnus_Exorcismus`; o `TalentProcs.Reactions.cs` deles só trata o talento "Holy Cross Judgment"/`Magnus_ExorcismusBD`, que
  é outra coisa), cancelar-cast com tecla segurada, desmontar ao castar (`BeforeCast`), `VisualOf/HitEffect(JsonElement)`, crescimento
  de monstro, drop boost, loja por crédito, serviços de NPC (hooks), guard de origem do cliente, trava de release.

## 2. Pontos de atenção (leia antes de trazer qualquer coisa)

1. **Assinatura `HitEffect`.** Localmente `HitEffect(JsonElement row)` (via `VisualOf`). O upstream ainda tem `HitEffect(long scriptShow)`
   e o **#43 (pets guardiões)** adiciona chamadas novas (`CombatService.GuardianPets.cs`). Trazer #43 sem adaptar não compila.
2. **`OriginGuardPatch` (seu) × `AccessFixPatch` (#33).** Os dois tratam o mesmo `MethodAccessException` do login
   (`RecoveryOfficialSdkLoginStart` → método privado aninhado). O #33 só alarga membros privados para `internal`; o seu também
   contorna o guard de origens assinadas, que o upstream **não** trata. Não trocar um pelo outro. O #33 pode servir de verificação cruzada.
3. **Cliente 140079.** O #84 diz que o cliente de **23/10 (release 140079, "ro3-live-na-na-trade-parity")** funciona com o servidor
   (login, mundo, Trade Exchange, sem erro de Lua), e que `update.genesis3.is` **não resolve mais** (o instalador usa o espelho Linode
   já em `PublisherConfig`). Isto é relevante para o seu limite de release (140075 liberado, 140076 não verificado): é evidência de
   terceiros, **não** verifica o seu `OriginGuardPatch` no 140079. O teste dele usou o `RO3Emu.exe update` do próprio upstream, e o login funcionou; não há prova sobre o guard de origens assinadas.
4. **Mudanças de banco.** #71 (colunas `paragon_level/exp`), #73 (`server_state`), #90 (índices/revisões de leilão), #39/#46 (checagem
   de schema), #75/#95 (GuildStore). Antes de qualquer deploy: backup do SQLite da VPS e deploy em horário calmo. Não verifiquei
   migrações reversíveis.
5. **`tests/Snapshots/dispatcher-admission.txt`** é alterado por quase todo PR que cria mensagem nova (#14, #36, #71, #73–#75, #77,
   #79, #95) e por você. O conflito é sempre o mesmo: regenerar o snapshot depois de portar.
6. **`prebuilt/client/*.dll` do upstream (#48) nunca deve sobrescrever os seus hooks** (NpcShops, LevelUpEffect…).
7. **Regras do projeto.** Cada item "conteúdo novo" abaixo marca quando o upstream declara `(inferred)` (não é conteúdo original
   confirmado). O **#74 (Título/Title ranks)** abre uma função que o original mantém fechada (precisa de nível 9999) via hook de cliente
   — não combina com "compatível com o conteúdo original"; só trazer com decisão sua. Nenhum PR analisado cria compra sem saldo
   ou função de GM nova (`GmCommands.cs` só aparece em #18/#31, formatação). Não auditei segurança a fundo; os PRs de hardening
   (#36–#39) estão apenas classificados.
8. **Registro.** Tudo o que você decidir trazer entra em `docs/guide/fixes-log.md` (uma entrada por PR: o que o jogador via,
   causa, correção, onde, como foi testado), na mesma tarefa.

## 3. Tabela por PR

Legenda de conflito (cruza os arquivos que o PR toca com os 73 arquivos locais sem commit; **todo PR ≥ #31 ainda exige adaptar os
nomes de campos**): **Nenhum** = não toca arquivo seu; **Baixo** = toca arquivo seu em trecho distante; **Médio** = mesma região ou
muitos arquivos seus; **Alto** = reescreve a área que você alterou. "Já temos?": **Não**, **Parcial**, **Equiv.** (você já resolveu
de outro jeito), **n/a**.

### 3.1 Correções de bug pequenas (servidor) — melhor custo/benefício

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #80 | Persistir snapshot de poder antes de notificar | bug (perda de poder se o cliente cai logo após 31502) | Não (`PowerService.cs` igual ao HEAD) | Nenhum (só renomear campos) | **Trazer (onda 1)** |
| #81 | Não repetir o histórico de chat em inscrição repetida | bug (linhas duplicadas) | Não | Nenhum | **Trazer (onda 1)** |
| #85 | Parar publicação de revive depois que o papel desanexa | bug (NRE/estado em handover) | Não | Baixo (`CombatService.cs`: trecho `PublishReviveAsync`, longe das suas mudanças) | **Trazer (onda 1)** |
| #82 | Limitar o drenar de conexões HTTP no shutdown (+#83 teste) | estabilidade (desligar/deploy mais rápido) | Não | Baixo (`ServerHost.cs`: você só adicionou 2 linhas em outro ponto) | **Trazer (onda 1)**; #83 só se trouxer o teste do writer |
| #87 | Liberar recursos de coleta quando o requisito da profissão é atingido | bug ("minério Lv.2 não libera") | Não (`UnlockEarnedCollections` não existe) | Nenhum (arquivo novo + 3 linhas) | **Trazer (onda 1)** |
| #86 | Chegada de Kafra em chão andável | bug (cair dentro de pedra, Payon Sul) | Não | Nenhum (`WorldData.cs`; seu `NavGrid.cs` só adiciona) | **Trazer (onda 1)** |
| #91 | Inicializar atalhos de poção e aceitar slots vazios | bug | Não | Nenhum | **Trazer (onda 1)** |
| #78 | Visibilidade da bolsa e sincronia de atalhos de item | bug | Não | Nenhum | **Trazer (onda 1)** |
| #88 | Roda do Dodo para no prêmio que realmente dá | bug ("hard lock" do Dodo Pink Gem) | Não (`TaskService.RoleOperations.cs` ainda usa `LuckyTrunConfig`) | Médio: usa `AwardConfig.Preview` das visões tipadas (#47); precisa reescrever com `GameData.Int` | **Trazer adaptado (onda 2)** |
| #89 | Ondas do portal do ladrão de ovos seguem o jogador | bug (monstros ficam fora da tela) | Não (`customTemplate` já existe localmente) | Nenhum | **Trazer (onda 2)** — marcado `(inferred)` |
| #93 | Continuar auto-atacando depois da parada da chegada | bug de combate | Não | Médio: `CombatService.cs`/`Casting.cs` (você mexeu em cast e `AttackLoop` na mesma área) | **Trazer à mão (onda 2)** |
| #44 | Contar o nível do talento mostrado no painel para liberar árvores | bug (árvore 126 não abria) | Não | Baixo (SeasonTalent não mexido por você) | **Trazer (onda 2)** |
| 0183849 (commit direto) | Escolha de caminho de talento de profissão: seleção e persistência | bug | Não | Baixo | **Trazer junto com #44** |
| #49 | Dividir o golpe no número de hits mostrado (Pierce 302 e 302) | bug visual de dano | Não | Médio (`CombatService.Effects.cs`/`.cs`, e usa `SkillConfig` tipado #47) | Onda 3 (depende de #47) |
| #72 | Enviar datasets vazios de Wizard/Questionnaire/Bar Room no login | estabilidade (cliente) | Não | Baixo (`GameSession.cs`) | Opcional; verificado pelo autor sem erro de Lua |
| #76 | Classificar estado `flying_dragon` no leitor de economia | ferramenta | n/a | Nenhum | Só se trouxer #73 |
| #90 | Filtrar leituras de lote do leilão com índice e revisões juntadas | desempenho | Não | Médio (muda schema/planos de persistência do leilão) | **Só se o leilão estiver lento**; exige backup/migração |

### 3.2 Hardening e consistência interna

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #36 | Recusar mensagens de jogo enviadas antes do estágio | segurança (conexão sem login rodava handlers) | Não | Médio: `GameSession`, `CoreMessages`, `BotClient`, snapshot (16 arquivos, 5 seus) | **Onda 3**, vale a pena; regenerar snapshot |
| #37 | Recusar chamadas micro antes de ter conta | segurança | Não | Baixo | **Onda 3**, par do #36 |
| #38 | Ler sessões nos endpoints HTTP no loop (`SessionCensus`) | segurança/consistência | Não | Baixo (`ServerHost.cs`) | Onda 3 |
| #39 | Reconhecer falhas por tipo/schema, não por texto | robustez | Não | Médio (`GuildService.cs`, 16 arq.) | Onda 3 |
| #40 | Contadores de id e chaves de teste no host, não em estáticos | refator | Não | Médio (ex.: `_skillObjectIds` em `Effects.cs`) | Só dentro da migração |
| #41 | Política e modelo de segurança (SECURITY.md) | doc | Não | Baixo (`README.md`) | Opcional (copiar texto) |
| #42 | Relógio do guild, prova do censo, uma checagem de obrigações | bug/teste | **Equiv.**: seu `GuildService.ClockMs()` faz o mesmo (teste falhava por data; produção não era afetada) | Baixo | Já resolvido; **não** trazer o hunk do guild |
| #46 | Fecha achados da auditoria dos estágios 0 e 1 | robustez | Não | Médio (stores de conta/guild) | Só dentro da migração |
| #32 | Fixar nomes que os recibos guardam | teste | n/a | Nenhum | Ignorar |
| #47 | Ler tabelas do cliente por visões tipadas (275 arq.) | infra | Não | Baixo em arquivos (quase tudo é arquivo novo; só `README.md` seu) | **Base** para #49, #88 e todo o bloco de talentos; fazer só se for migrar |

### 3.3 Guildas

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #75 | E-mail em grupo da guilda (32241) | conteúdo | Não | Baixo (`GuildService.cs` só no hunk do relógio) | Onda 4 |
| #77 | Convocação de Ilmata (32266/32267) | conteúdo | Não | Baixo (snapshot) | Onda 4 |
| #79 | Espectador do Wedding Crashers (30452) | conteúdo | Não | Baixo (snapshot) | Onda 4 |
| #92 | Tarefas de evento do Guild To-Do (participar conclui) | conteúdo/bug | Não | Baixo (`ServerHost.cs`) | Onda 4 |
| #95 | Impeachment do mestre da guilda e **fusão de guildas** (32235/32236, 32314–32319) | conteúdo novo (solicitado) | Não (`GuildService.Impeach/Merge` não existem) | Médio (`GuildService.cs`, `GuildStore.cs`, snapshot; mexe em schema de guilda) | Onda 4; backup do banco |
| #14, #16, #26 | Canais de voz de guilda: painel, som | conteúdo | Parcial (voz de grupo já em HEAD) | Médio (`GuildService.cs`, `ServerHost.cs`, snapshot) | Só se usar voz |

### 3.4 Voz (grupo e guilda)

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #10 | Dica "voz desligada neste servidor" ao clicar Talk | melhoria | Não | Baixo | Se usa voz |
| #11 | Criptografar o fluxo do relay de voz | segurança | Não | Baixo | Se usa voz (o relay vem desligado por padrão) |
| #12 | `--voice-port` no launcher | melhoria | Não | Baixo (`InstallFlow.cs`, `ServerOptions.cs` seus) | Se usa voz |
| #27 | Follow-ups (baseline, **fechar kick de voz**) | bug | Não | Médio | Pegar só a parte do kick de voz |
| #94 | Teste de voz com timeout normal | teste | n/a | Nenhum | Ignorar |

### 3.5 Combate, skills, talentos, pets (conteúdo e correção de comportamento)

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #43 | Habilidades de guardião de pet comum + efeitos reativos (4909 linhas, 69 arq.) | conteúdo | Não | **Alto** (`BossSkills`, `Casting`, `Effects`, `MonsterSkills`, `TowerPets`, `CombatService.cs`, `MonsterService`, `MonsterUnit`; muda `HitEffect`) | Só na migração |
| #50–#55 | Efeitos especiais e procs dos talentos de profissão (todas as linhas) | conteúdo | Não (`TalentProcs*` e `SeasonTalentService.Effects` não existem) | **Alto** (`CombatService.cs`, `Effects.cs`, `GameSession.cs`, `ServerHost.cs`) | Só na migração, em bloco |
| #56 | Dados e eventos dos passivos da árvore de skills | conteúdo | Não | Alto | Bloco talentos |
| #57 | Union, Sonic Blow, Arcane Mastery; casts ao fim de uma caminhada | conteúdo/bug | Parcial (você já trata `StartedWalking` em cast) | Alto (`Casting.cs`, `Effects.cs`) | Bloco talentos; olhar o trecho "cast ao fim da caminhada" à parte |
| #58 | Passivos de montaria e buffs de bind | conteúdo | Não | Médio (`MountService.cs`, onde você adicionou `DismountForCastAsync`) | Bloco talentos |
| #59 | Psychokinetic Astral Body | conteúdo | Não | Médio (`ServerHost.cs`) | Bloco talentos |
| #60 | Bosses resistem a controles; Element Control | bug de balanceamento | Não | Baixo (`PassPlayTests`) | **Onda 3**; depende de buff `BuffService.ImmuneTo` (arquivo não seu) |
| #61 | Todo monstro recebe os buffs de nascimento do NPCConfig | bug | Não | Médio (`MonsterService.cs` +205 linhas suas) | Onda 3, com cuidado |
| #63 | Linha de modo mostra recarga sob a linha que o cliente pediu | bug | Não | Médio (`Casting.cs`) | Onda 3 |
| #64 | MVPs de campo invocam seus minions e põem objetos de chão | conteúdo | Não | Médio (`BossSkills.cs`) | Onda 3 |
| #65 | Esconder/detectar para qualquer fonte (Disguise, Ruwach…) | conteúdo | Não | Médio (`CombatService.cs`, `ServerHost.cs`) | Onda 3 |
| #66 | Shield Bash Echo, Falcon's Might, Scouting | conteúdo | Não | Médio (`Casting.cs`) | Bloco talentos |
| #67 | Skills de carta por cartas encaixadas | conteúdo | Não | Nenhum | Onda 3 |
| #68 | Knockback/pull de boss move os jogadores atingidos | conteúdo | Não | Baixo (`BossSkills.cs`) | Onda 3 |
| *(sem PR)* Magnus Exorcismus | cruz no chão (`Magnus_Exorcismus`) | bug | **Você tem; o upstream não** | n/a | Manter o seu. Se quiser, vale oferecer ao upstream |

### 3.6 Conteúdo novo, mapas e economia

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #69 | Dungeons de história com estágios roteirizados | conteúdo (parte `inferred`) | Não | Baixo | Opcional, onda 5 |
| #70 | Moonlight Flower (esconde-esconde) e Pirâmide do Faraó | conteúdo **inferido** | Não | Baixo (`ServerOptions.cs`) | Opcional, avaliar fidelidade |
| #71 | **Astral Level (Paragon)**: estrelas, Star Cores, EXP acima do 99 | conteúdo novo | Não | **Alto**: `GameSession`, `RewardService` (seu drop boost), `ServerHost`, `ServerOptions`, snapshot; colunas novas no banco | Onda 5, depois de decidir a base |
| #73 | Ritual do Dragão Starshatter e Lord's Mane de MVPs | conteúdo | Não | Médio | Onda 5; exige `server_state` |
| #74 | **Títulos com widget hospedado** | conteúdo (original o mantém fechado) | Não | **Alto** (`LuaPatches`, `UiTweaks` seus) | **Não trazer** sem sua decisão |

### 3.7 Cliente, instalador, release, CI e docs

| PR | Título | Tipo | Já temos? | Conflito | Recomendação |
|---|---|---|---|---|---|
| #29 | Suportar o cliente de 20/10 (condições LC_ConditionList 2000062/21000012) | cliente | Não | Médio (`ServerOptions.cs`, `SeasonPageTests`, `MountTests`) | Pegar a regra `OpenContent.Functions` forçar a função 69 |
| #33 | `AccessFixPatch`: torna `internal` o membro privado que quebra o login | cliente | Parcial (seu `OriginGuardPatch` cobre o mesmo erro) | Médio (`ClientPatcher.cs`, `Ro3Emu.Setup.csproj`) | **Não trocar**; usar para verificação cruzada |
| #84 | Cliente Genesis 2026.10.23.1 (release 140079) testado; `check` mostra update próprio | cliente | Não | Baixo (`ClientLocator.cs`) | Ler como evidência (item 3 acima); a correção do `check` (mostrar a release instalada) vale pegar |
| #48 | Atualizar `prebuilt/client` e checar no CI | build | n/a | — | **Não trazer** (DLLs do upstream) |
| #18, #26, #27, #30, #31, #34, #35, #20–#25, #28 | Gates de qualidade, CI Windows, release no GitHub, Dependabot, SDK | infra | Não | **Alto** (formatação em tudo) | **Não trazer isolado**. Pacotes: KeraLua 1.4.11, MessagePack 3.1.12, Microsoft.Data.Sqlite 10.0.13, SDK 10.0.402 podem ser subidos à mão |
| #96 | README para jogadores e docs | doc | Não | Baixo (`docs/README.md` seu) | Opcional; `scripts/` vem junto |

## 4. Como trazer com segurança (sem executar nada agora)

**Antes de tudo:** commitar (ou ao menos salvar em branch) o trabalho local. Hoje ele só existe na pasta; qualquer operação de
git na mesma pasta pode perdê-lo. Use um `git worktree` separado para experimentar.

**Opção A, recomendada agora: portar à mão as correções pequenas (ondas 1–2).** Num worktree/branch a partir do HEAD, aplicar cada
correção como commit próprio e pequeno, lendo o diff do upstream (`gh pr diff N -R aethai/ro3emu-server`), traduzindo os nomes
de campo, e rodando a suíte do servidor. `git cherry-pick` direto só funciona onde o PR não toca linhas com campos renomeados
(raro). Um commit por PR, mensagem curta em inglês citando o PR de origem, e uma entrada em `fixes-log.md`.

**Opção B, se quiser o conteúdo novo (ondas 3–5): migrar para a base do upstream, em vez de ir cherry-pickando.** Criar um worktree
a partir de `origin/main` do upstream e **reaplicar o seu trabalho** (cerca de 1165 linhas em arquivos existentes + os ~30 arquivos
novos) em vez de reaplicar 227 commits deles. Os arquivos novos seus precisam passar nas regras novas (campos `_camelCase`,
construtor primário, warnings como erro) e `HitEffect`/`VisualOf` precisam ser reconciliados com #43. É trabalho de dias, mas cada
passo é verificável e o conflito é uma vez só.

**Ordem sugerida:**

1. **Onda 1** (nenhum conflito real): #80, #81, #85, #82, #87, #86, #91, #78.
2. **Onda 2**: #88, #89, #93, #44 + `0183849`.
3. **Onda 3** (só se quiser endurecer): #36, #37, #38, #39; depois #60, #61, #63, #67, #68.
4. **Decisão sua:** o limite de release 140075 e o `OriginGuardPatch` frente ao relato de que 140079 funciona.
5. **Ondas 4–5 (conteúdo)**: guildas (#75, #77, #79, #92, #95), depois #71/#73, dungeons #69/#70, e por último o bloco de talentos/pets
   (#43, #50–#59, #66) como migração da Opção B.

**Depois de cada onda:** suíte de testes do servidor, build, deploy só com seu OK, backup do banco antes de qualquer PR com mudança
de schema, e registro em `docs/guide/fixes-log.md`.

## 5. Trazer primeiro (lista curta)

1. #80 poder persistido antes de notificar
2. #81 histórico de chat duplicado
3. #85 revive depois do handover
4. #82 shutdown HTTP limitado
5. #87 liberar coleta por nível de profissão
6. #86 Kafra em chão andável
7. #91 atalhos de poção
8. #78 bolsa/atalhos
9. #88 roda do Dodo (trava)
10. #44 + `0183849` talentos de profissão

Não trazer agora: #18/#31 (formatação), #48 (DLLs), #74 (Títulos), e o bloco de talentos/pets (#43, #50–#59), que só faz sentido
dentro de uma migração.
